import type { BatchEventPayload, HeartbeatPayload } from "@ai-life/shared";
import {
  db,
  insertEvent,
  latestEventByBucket,
  privacyRulesByUser,
  updateEventEnd,
  upsertAggregateEvent,
  upsertBucket,
  upsertDeviceState,
  type EventRow,
  type PrivacyRuleRow,
} from "../../db";
import { closeOpenEventAt, durationMs, eventFingerprint, shouldMergeHeartbeat } from "../../shared/event-merge";
import { applyEventPrivacy, hmacText, type PrivacyRuleInput } from "../../shared/privacy";
import { bucketId, bucketParts, eventType, isoTime, jsonObject, parseJsonString, recentIsoTime, safeDataJson } from "../../shared/validation";
import type { DeviceIdentity, PrivacyLevel } from "@ai-life/shared";

function eventId(): string {
  return `evt_${crypto.randomUUID()}`;
}

function ensureBucket(bucket: string, device: DeviceIdentity, source: string, type: string, nowIso: string) {
  upsertBucket.run(bucket, device.userId, device.deviceId, source, type, nowIso, "{}");
}

function privacyRules(userId: string): PrivacyRuleInput[] {
  return (privacyRulesByUser.all(userId) as PrivacyRuleRow[])
    .map((rule) => ({
      target_type: rule.target_type,
      pattern: rule.pattern,
      action: rule.action,
    }))
    .filter((rule): rule is PrivacyRuleInput =>
      ["allow_title", "hash_title", "hide_title", "drop_event", "category_only"].includes(rule.action),
    );
}

export function ingestHeartbeat(device: DeviceIdentity, payload: HeartbeatPayload): { merged: boolean } {
  const type = eventType(payload.type);
  if (!type) throw new Error("type required");

  const bucket = bucketId(payload.bucket, `${device.platform}:${device.deviceId}:${type}`);
  const parts = bucketParts(bucket, type);
  const timestamp = recentIsoTime(payload.timestamp);
  const timestampIso = timestamp.toISOString();
  const heartbeatIntervalMs =
    typeof payload.heartbeat_interval_ms === "number" && Number.isFinite(payload.heartbeat_interval_ms)
      ? Math.max(1000, Math.min(60 * 60 * 1000, Math.round(payload.heartbeat_interval_ms)))
      : 10_000;

  const privacy = applyEventPrivacy(type, jsonObject(payload.data), privacyRules(device.userId));
  if (privacy.drop) return { merged: false };
  const data = privacy.data;
  const dataJson = safeDataJson(data);
  const nowIso = new Date().toISOString();
  ensureBucket(bucket, device, parts.source, parts.bucketType, nowIso);

  const latest = latestEventByBucket.get(bucket) as EventRow | undefined;
  const latestData = latest ? parseJsonString(latest.data_json) : null;
  const canMerge = shouldMergeHeartbeat(
    latest
      ? {
          type: latest.type,
          startAt: latest.start_at,
          endAt: latest.end_at,
          fingerprint: eventFingerprint(latest.type, latestData ?? {}, latest.value, latest.unit),
        }
      : null,
    {
      type,
      timestamp,
      heartbeatIntervalMs,
      fingerprint: eventFingerprint(type, data),
    },
  );

  if (latest && canMerge) {
    updateEventEnd.run(timestampIso, durationMs(latest.start_at, timestampIso), nowIso, latest.id);
  } else {
    if (latest && !latest.end_at) {
      const closedAt = closeOpenEventAt(latest.start_at, timestamp);
      updateEventEnd.run(closedAt, durationMs(latest.start_at, closedAt), nowIso, latest.id);
    }
    insertEvent.run(
      eventId(),
      bucket,
      device.userId,
      device.deviceId,
      parts.source,
      type,
      timestampIso,
      timestampIso,
      0,
      null,
      null,
      dataJson,
      "normal",
      1,
      null,
      nowIso,
      nowIso,
    );
  }

  upsertDeviceState.run(device.deviceId, device.userId, device.deviceName, device.platform, type, dataJson, nowIso);
  return { merged: Boolean(canMerge) };
}

export function ingestEvents(device: DeviceIdentity, payload: BatchEventPayload): { accepted: number; skipped: number } {
  if (!Array.isArray(payload.events) || payload.events.length === 0) {
    throw new Error("events array required");
  }
  if (payload.events.length > 500) {
    throw new Error("too many events");
  }

  let accepted = 0;
  let skipped = 0;
  const nowIso = new Date().toISOString();
  const rules = privacyRules(device.userId);

  const tx = db.transaction(() => {
    for (const event of payload.events) {
      const type = eventType(event.type);
      const start = isoTime(event.start_at);
      if (!type || !start) {
        skipped++;
        continue;
      }
      const end = isoTime(event.end_at);
      if (end && end.getTime() < start.getTime()) {
        skipped++;
        continue;
      }

      const value = typeof event.value === "number" && Number.isFinite(event.value) ? event.value : null;
      const unit = typeof event.unit === "string" ? event.unit.slice(0, 32) : null;
      const privacy = applyEventPrivacy(type, jsonObject(event.data), rules);
      if (privacy.drop) {
        skipped++;
        continue;
      }
      const data = privacy.data;
      const dataJson = safeDataJson(data);
      const bucket = bucketId(event.bucket, `${device.platform}:${device.deviceId}:${type}`);
      const parts = bucketParts(bucket, type);
      const startIso = start.toISOString();
      const endIso = end ? end.toISOString() : null;
      const rawHash = event.idempotency_key
        ? hmacText(`idempotency:${device.deviceId}:${event.idempotency_key}`)
        : hmacText(`${device.deviceId}:${bucket}:${type}:${startIso}:${endIso ?? ""}:${value ?? ""}:${unit ?? ""}:${dataJson}`);
      const privacyLevel: PrivacyLevel = event.privacy_level ?? "normal";
      const confidence =
        typeof event.confidence === "number" && Number.isFinite(event.confidence)
          ? Math.max(0, Math.min(1, event.confidence))
          : 1;

      ensureBucket(bucket, device, parts.source, parts.bucketType, nowIso);
      const writeEvent = type === "usage.app_daily" ? upsertAggregateEvent : insertEvent;
      const result = writeEvent.run(
        eventId(),
        bucket,
        device.userId,
        device.deviceId,
        parts.source,
        type,
        startIso,
        endIso,
        endIso ? durationMs(startIso, endIso) : null,
        value,
        unit,
        dataJson,
        privacyLevel,
        confidence,
        rawHash,
        nowIso,
        nowIso,
      );
      if (result.changes > 0) accepted++;
      else skipped++;
    }
  });

  tx();
  return { accepted, skipped };
}
