import { randomUUID } from "node:crypto";
import type { Env } from "../../config/env.js";
import { BucketModel, DeviceStateModel, EventModel, PrivacyRuleModel } from "../../db/models.js";
import { closeOpenEventAt, durationMs, eventFingerprint, shouldMergeHeartbeat } from "../../shared/event-merge.js";
import { AppError } from "../../shared/errors.js";
import { applyEventPrivacy, hmacText, type PrivacyRuleInput } from "../../shared/privacy.js";
import { bucketId, bucketParts, eventType, isoTime, jsonObject, recentIsoTime, safeData } from "../../shared/validation.js";
import type { BatchEventPayload, DeviceIdentity, EventPayload, HeartbeatPayload, PrivacyLevel } from "../../types/contracts.js";

function eventId(): string {
  return `evt_${randomUUID()}`;
}

async function rulesFor(userId: string): Promise<PrivacyRuleInput[]> {
  const rows = await PrivacyRuleModel.find({ user_id: userId }).sort({ created_at: 1 }).lean().exec();
  return rows.filter((row): row is typeof row & PrivacyRuleInput =>
    ["allow_title", "hash_title", "hide_title", "drop_event", "category_only"].includes(row.action),
  ) as PrivacyRuleInput[];
}

async function ensureBucket(bucket: string, device: DeviceIdentity, source: string, type: string, now: Date): Promise<void> {
  await BucketModel.updateOne(
    { id: bucket },
    { $setOnInsert: { id: bucket, user_id: device.userId, device_id: device.deviceId, source, type, metadata: {}, created_at: now } },
    { upsert: true },
  );
}

export async function ingestHeartbeat(env: Env, device: DeviceIdentity, payload: HeartbeatPayload): Promise<{ merged: boolean }> {
  const type = eventType(payload.type);
  if (!type) throw new AppError(400, "type required");

  const bucket = bucketId(payload.bucket, `${device.platform}:${device.deviceId}:${type}`);
  const parts = bucketParts(bucket, type);
  const timestamp = recentIsoTime(payload.timestamp);
  const interval = typeof payload.heartbeat_interval_ms === "number" && Number.isFinite(payload.heartbeat_interval_ms)
    ? Math.max(1_000, Math.min(3_600_000, Math.round(payload.heartbeat_interval_ms)))
    : 10_000;
  const privacy = applyEventPrivacy(env.HASH_SECRET, type, jsonObject(payload.data), await rulesFor(device.userId));
  if (privacy.drop) return { merged: false };
  const data = safeData(privacy.data);
  const now = new Date();

  await ensureBucket(bucket, device, parts.source, parts.bucketType, now);
  const latest = await EventModel.findOne({ bucket_id: bucket }).sort({ start_at: -1 }).lean().exec();
  const canMerge = shouldMergeHeartbeat(
    latest ? {
      type: latest.type,
      startAt: latest.start_at,
      endAt: latest.end_at ?? null,
      fingerprint: eventFingerprint(latest.type, jsonObject(latest.data), latest.value, latest.unit),
    } : null,
    { type, timestamp, heartbeatIntervalMs: interval, fingerprint: eventFingerprint(type, data) },
  );

  if (latest && canMerge) {
    await EventModel.updateOne({ id: latest.id }, { end_at: timestamp, duration_ms: durationMs(latest.start_at, timestamp), updated_at: now });
  } else {
    if (latest && !latest.end_at) {
      const closedAt = closeOpenEventAt(latest.start_at, timestamp);
      await EventModel.updateOne({ id: latest.id }, { end_at: closedAt, duration_ms: durationMs(latest.start_at, closedAt), updated_at: now });
    }
    await EventModel.create({
      id: eventId(), bucket_id: bucket, user_id: device.userId, device_id: device.deviceId,
      source: parts.source, type, start_at: timestamp, end_at: timestamp, duration_ms: 0,
      value: null, unit: null, data, privacy_level: "normal", confidence: 1, raw_hash: null,
      created_at: now, updated_at: now,
    });
  }

  await DeviceStateModel.updateOne(
    { device_id: device.deviceId },
    { $set: { user_id: device.userId, device_name: device.deviceName, platform: device.platform, current_type: type, current_data: data, last_seen_at: now, is_online: true } },
    { upsert: true },
  );
  return { merged: canMerge };
}

export async function ingestEvents(env: Env, device: DeviceIdentity, payload: BatchEventPayload): Promise<{ accepted: number; skipped: number }> {
  if (!Array.isArray(payload.events) || payload.events.length === 0) throw new AppError(400, "events array required");
  if (payload.events.length > 500) throw new AppError(400, "too many events");

  const rules = await rulesFor(device.userId);
  let accepted = 0;
  let skipped = 0;

  for (const candidate of payload.events as EventPayload[]) {
    const type = eventType(candidate.type);
    const start = isoTime(candidate.start_at);
    const end = isoTime(candidate.end_at);
    if (!type || !start || (end && end < start)) {
      skipped++;
      continue;
    }

    const privacy = applyEventPrivacy(env.HASH_SECRET, type, jsonObject(candidate.data), rules);
    if (privacy.drop) {
      skipped++;
      continue;
    }
    const data = safeData(privacy.data);
    const value = typeof candidate.value === "number" && Number.isFinite(candidate.value) ? candidate.value : null;
    const unit = typeof candidate.unit === "string" ? candidate.unit.slice(0, 32) : null;
    const bucket = bucketId(candidate.bucket, `${device.platform}:${device.deviceId}:${type}`);
    const parts = bucketParts(bucket, type);
    const idempotency = typeof candidate.idempotency_key === "string" && candidate.idempotency_key
      ? `idempotency:${device.deviceId}:${candidate.idempotency_key}`
      : `${device.deviceId}:${bucket}:${type}:${start.toISOString()}:${end?.toISOString() ?? ""}:${value ?? ""}:${unit ?? ""}:${JSON.stringify(data)}`;
    const rawHash = hmacText(env.HASH_SECRET, idempotency);
    const privacyLevel: PrivacyLevel = ["normal", "sensitive", "private"].includes(String(candidate.privacy_level))
      ? candidate.privacy_level as PrivacyLevel
      : "normal";
    const confidence = typeof candidate.confidence === "number" && Number.isFinite(candidate.confidence)
      ? Math.max(0, Math.min(1, candidate.confidence))
      : 1;
    const now = new Date();
    await ensureBucket(bucket, device, parts.source, parts.bucketType, now);

    const event = {
      id: eventId(), bucket_id: bucket, user_id: device.userId, device_id: device.deviceId, source: parts.source, type,
      start_at: start, end_at: end, duration_ms: end ? durationMs(start, end) : null, value, unit, data,
      privacy_level: privacyLevel, confidence, raw_hash: rawHash, created_at: now, updated_at: now,
    };
    try {
      if (type === "usage.app_daily") {
        await EventModel.updateOne({ raw_hash: rawHash }, { $set: event, $setOnInsert: { created_at: now } }, { upsert: true });
        accepted++;
      } else {
        const result = await EventModel.updateOne({ raw_hash: rawHash }, { $setOnInsert: event }, { upsert: true });
        if (result.upsertedCount > 0) accepted++;
        else skipped++;
      }
    } catch (error) {
      if (typeof error === "object" && error && "code" in error && error.code === 11000) skipped++;
      else throw error;
    }
  }
  return { accepted, skipped };
}
