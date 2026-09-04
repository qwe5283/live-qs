import type {
  VersionedEvent,
  CredentialPrivacyCeiling,
  EventAcknowledgement,
  EventBatchResponse,
  EventPage,
  PageMetadata,
  QueryContext,
} from "../../generated/contract-models.js";
import type { Env } from "../../config/env.js";
import { BucketModel, EventModel, EventRevisionModel } from "../../db/models.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import { hmacText } from "../../shared/privacy.js";
import { AppError } from "../../shared/errors.js";
import {
  ACTIVITY_EVENT_TYPES,
  HEALTH_EVENT_TYPES,
  PAYMENT_EVENT_TYPES,
  REGISTERED_EVENT_TYPES,
  defaultPrivacyLevel,
  requiredWriteScope,
  validateRegisteredEvent,
} from "./payload-registry.js";
import type { RegisteredEventType } from "./payload-registry.js";
import type { EventRow } from "../../types/contracts.js";
import { isDuplicateKeyError } from "../../shared/mongo.js";

export { REGISTERED_EVENT_TYPES };

/** Registered event types granted by held read scopes; a read never crosses an ungranted domain. */
export function eventTypesForReadScopes(scopes: string[] | undefined): string[] {
  const granted = new Set<string>();
  if (scopes?.includes("events:read")) ACTIVITY_EVENT_TYPES.forEach((type) => granted.add(type));
  if (scopes?.includes("health:read")) HEALTH_EVENT_TYPES.forEach((type) => granted.add(type));
  if (scopes?.includes("payment:read")) PAYMENT_EVENT_TYPES.forEach((type) => granted.add(type));
  return [...granted];
}

export interface EventRangeQuery {
  userId: string;
  from: Date;
  to: Date;
  timezone: string;
  eventType?: string;
  cursor?: string;
  pageSize: number;
  /** Maximum privacy level the credential may see; absent for Owner sessions. */
  privacyCeiling?: CredentialPrivacyCeiling;
  /** Credential-restricted event types; empty or absent means unrestricted. */
  allowedEventTypes?: string[];
  /** Event types granted by the credential's read scopes; absent for Owner sessions. */
  scopeGrantedEventTypes?: string[];
  /**
   * Types counted as the unrestricted baseline for completeness reporting.
   * Defaults to every registered type; a domain read (health) baselines its
   * own domain so out-of-domain data never marks the page partial.
   */
  completenessBaseline?: string[];
}

/** Levels a read may return per privacy ceiling; `private` cannot enter the contract envelope. */
const PRIVACY_LEVELS_UP_TO: Record<CredentialPrivacyCeiling, string[]> = {
  normal: ["normal"],
  sensitive: ["normal", "sensitive"],
  private: ["normal", "sensitive"],
};

/** Privacy levels a read may touch; absent ceilings fall back to the widest contract-representable set. */
export function privacyLevelsForRead(privacyCeiling?: CredentialPrivacyCeiling): string[] {
  return PRIVACY_LEVELS_UP_TO[privacyCeiling ?? "private"];
}

/** Registered event types a read may touch; a non-empty allowed list is an exact intersection. */
export function readableEventTypes(allowedEventTypes?: string[], domainTypes: readonly string[] = REGISTERED_EVENT_TYPES): string[] {
  const base = domainTypes.filter((type) => (REGISTERED_EVENT_TYPES as readonly string[]).includes(type));
  if (allowedEventTypes === undefined || allowedEventTypes.length === 0) return base;
  const allowed = new Set(allowedEventTypes);
  return base.filter((type) => allowed.has(type));
}

/**
 * Maps one stored event to the contract envelope. Rows written before the
 * versioned protocol lack envelope columns and receive neutral defaults; rows
 * marked `private` are a legacy-only state the contract cannot represent and
 * are never returned. The payload was validated at ingest against the schema
 * registry and is passed through opaquely here.
 */
function toEnvelopeEvent(row: EventRow): VersionedEvent {
  const envelope: VersionedEvent = {
    event_id: row.id,
    event_type: (row.type ?? "activity.interval") as VersionedEvent["event_type"],
    schema_version: row.schema_version ?? 1,
    owner_id: row.user_id,
    source: {
      kind: (row.source_kind ?? row.source) as VersionedEvent["source"]["kind"],
      record_id: row.source_record_id ?? row.id,
    },
    device: { id: row.device_id, platform: row.device_platform === "android" ? "android" : "windows" },
    start_at: row.start_at.toISOString(),
    capture_timezone: row.capture_timezone ?? "UTC",
    capture_offset_minutes: row.capture_offset_minutes ?? 0,
    privacy_level: row.privacy_level === "sensitive" ? "sensitive" : "normal",
    revision: row.revision ?? 1,
    finalization_state: row.finalization_state === "final" ? "final" : "checkpoint",
    provenance: row.provenance ?? { collector_version: "0.0.0", observed_at: row.created_at.toISOString() },
    invalidated: row.invalidated ?? false,
    payload: row.data as unknown as VersionedEvent["payload"],
  };
  if (row.end_at) envelope.end_at = row.end_at.toISOString();
  return envelope;
}

function encodeCursor(row: Pick<EventRow, "start_at" | "id">): string {
  return Buffer.from(JSON.stringify({ s: row.start_at.toISOString(), i: row.id }), "utf8").toString("base64url");
}

function decodeCursor(cursor: string): { startAt: Date; id: string } | null {
  try {
    const parsed: unknown = JSON.parse(Buffer.from(cursor, "base64url").toString("utf8"));
    if (
      typeof parsed === "object" && parsed !== null
      && typeof (parsed as { s?: unknown }).s === "string"
      && typeof (parsed as { i?: unknown }).i === "string"
    ) {
      const startAt = new Date((parsed as { s: string }).s);
      if (!Number.isNaN(startAt.getTime())) return { startAt, id: (parsed as { i: string }).i };
    }
  } catch {
    // Fall through to the rejection below.
  }
  return null;
}

export async function listEvents(query: EventRangeQuery): Promise<EventPage> {
  // Scope-granted domains bound credential reads before the optional
  // allowed_event_types allow-list is applied; Owner sessions pass no scope
  // grant and read every registered domain.
  const domainTypes = query.scopeGrantedEventTypes ?? REGISTERED_EVENT_TYPES;
  const credentialRestricted = query.privacyCeiling !== undefined
    || (query.allowedEventTypes !== undefined && query.allowedEventTypes.length > 0)
    || query.scopeGrantedEventTypes !== undefined;
  const privacyLevels = privacyLevelsForRead(query.privacyCeiling);
  const restrictedTypes = readableEventTypes(query.allowedEventTypes, domainTypes);

  const baselineTypes = query.completenessBaseline ?? REGISTERED_EVENT_TYPES;
  const unrestrictedFilter: Record<string, unknown> = {
    user_id: query.userId,
    start_at: { $gte: query.from, $lt: query.to },
    privacy_level: { $in: privacyLevelsForRead() },
    type: query.eventType
      ? { $eq: query.eventType, $in: baselineTypes }
      : { $in: baselineTypes },
  };
  const filter: Record<string, unknown> = {
    user_id: query.userId,
    start_at: { $gte: query.from, $lt: query.to },
    privacy_level: { $in: privacyLevels },
    type: query.eventType ? { $eq: query.eventType, $in: restrictedTypes } : { $in: restrictedTypes },
  };

  if (query.cursor !== undefined) {
    const cursor = decodeCursor(query.cursor);
    if (!cursor) throw new AppError(400, "The cursor is not a valid continuation cursor.", "invalid_cursor");
    const continuation = [
      { start_at: { $gt: cursor.startAt } },
      { start_at: cursor.startAt, id: { $gt: cursor.id } },
    ];
    filter.$or = continuation;
    unrestrictedFilter.$or = continuation;
  }

  const rows = await EventModel.find(filter).sort({ start_at: 1, id: 1 }).limit(query.pageSize + 1).lean<EventRow[]>();
  const hasMore = rows.length > query.pageSize;
  const pageRows = hasMore ? rows.slice(0, query.pageSize) : rows;
  const last = pageRows[pageRows.length - 1];

  // When a credential's ceiling or event-type restriction withheld in-range
  // data, the query context reports the page as partial instead of complete.
  let completeness: QueryContext["completeness"] = "complete";
  if (credentialRestricted
    && (await EventModel.countDocuments(unrestrictedFilter)) > (await EventModel.countDocuments(filter))) {
    completeness = "partial";
  }

  const provenance = (await EventModel.distinct("source_kind", filter)).filter(
    (kind): kind is string => typeof kind === "string" && kind.length > 0,
  ).sort();
  const context: QueryContext = {
    from: query.from.toISOString(),
    to: query.to.toISOString(),
    timezone: query.timezone,
    provenance,
    completeness,
  };
  const page: PageMetadata = {
    page_size: query.pageSize,
    next_cursor: hasMore && last ? encodeCursor(last) : null,
  };
  return { data: pageRows.map(toEnvelopeEvent), page, context };
}

const EVENT_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const COLLECTOR_VERSION_PATTERN = /^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$/;
const PRIVACY_RANK: Record<CredentialPrivacyCeiling | "normal" | "sensitive", number> = {
  normal: 0,
  sensitive: 1,
  private: 2,
};

interface ParsedBatchItem {
  eventId: string;
  ownerId: string;
  eventType: RegisteredEventType;
  schemaVersion: number;
  revision: number;
  privacy: "normal" | "sensitive";
  startAt: Date;
  endAt: Date | null;
  source: { kind: string; recordId: string };
  deviceClaimed: { id: string; platform: "windows" | "android" };
  captureTimezone: string;
  captureOffsetMinutes: number;
  finalizationState: "checkpoint" | "final";
  provenance: { collector_version: string; observed_at: string };
  invalidated: boolean;
  payload: Record<string, unknown>;
}

type ParsedBatchResult = { item: ParsedBatchItem } | { error: { code: string; message: string }; eventId: string; revision: number };

/** Structural envelope validation, then deep payload validation from the schema registry. */
function parseBatchItem(raw: unknown): ParsedBatchResult {
  const invalid = (message: string): ParsedBatchResult => ({ error: { code: "invalid_event", message }, eventId: rawEventId(raw), revision: rawRevision(raw) });
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) return invalid("Each event must be an object.");
  const event = raw as Record<string, unknown>;

  const eventId = event.event_id;
  if (typeof eventId !== "string" || !EVENT_ID_PATTERN.test(eventId)) return invalid("event_id must be a UUID.");
  const revisionResult = rawRevision(raw);
  if (typeof event.revision !== "number" || !Number.isInteger(event.revision) || event.revision < 1) {
    return { error: { code: "invalid_event", message: "revision must be a positive integer." }, eventId, revision: revisionResult };
  }
  if (typeof event.event_type !== "string" || !(REGISTERED_EVENT_TYPES as readonly string[]).includes(event.event_type)) {
    return { error: { code: "unknown_event_type", message: "The event type is not registered." }, eventId, revision: revisionResult };
  }
  const eventType = event.event_type as RegisteredEventType;
  if (event.schema_version !== 1) {
    return { error: { code: "unknown_schema_version", message: "The schema version is not registered." }, eventId, revision: revisionResult };
  }
  if (typeof event.owner_id !== "string" || event.owner_id.length === 0) return invalid("owner_id is required.");
  const startAt = typeof event.start_at === "string" ? new Date(event.start_at) : null;
  if (!startAt || Number.isNaN(startAt.getTime())) return invalid("start_at must be an ISO 8601 instant.");
  let endAt: Date | null = null;
  if (event.end_at !== undefined) {
    endAt = typeof event.end_at === "string" ? new Date(event.end_at) : null;
    if (!endAt || Number.isNaN(endAt.getTime())) return invalid("end_at must be an ISO 8601 instant when present.");
  }
  const device = event.device;
  if (typeof device !== "object" || device === null) return invalid("device is required.");
  const deviceId = (device as Record<string, unknown>).id;
  const platform = (device as Record<string, unknown>).platform;
  if (typeof deviceId !== "string" || deviceId.length === 0) return invalid("device.id is required.");
  if (platform !== "windows" && platform !== "android") return invalid("device.platform must be windows or android.");
  const source = event.source;
  if (typeof source !== "object" || source === null) return invalid("source is required.");
  const sourceKind = (source as Record<string, unknown>).kind;
  const sourceRecordId = (source as Record<string, unknown>).record_id;
  if (typeof sourceKind !== "string" || sourceKind.length === 0) return invalid("source.kind is required.");
  if (typeof sourceRecordId !== "string" || sourceRecordId.length === 0) return invalid("source.record_id is required.");
  if (typeof event.capture_timezone !== "string" || event.capture_timezone.length === 0) return invalid("capture_timezone is required.");
  if (typeof event.capture_offset_minutes !== "number" || !Number.isInteger(event.capture_offset_minutes)
    || event.capture_offset_minutes < -840 || event.capture_offset_minutes > 840) {
    return invalid("capture_offset_minutes must be an integer between -840 and 840.");
  }
  if (event.finalization_state !== "checkpoint" && event.finalization_state !== "final") {
    return invalid("finalization_state must be checkpoint or final.");
  }
  const provenance = event.provenance;
  if (typeof provenance !== "object" || provenance === null) return invalid("provenance is required.");
  const collectorVersion = (provenance as Record<string, unknown>).collector_version;
  const observedAtRaw = (provenance as Record<string, unknown>).observed_at;
  const observedAt = typeof observedAtRaw === "string" ? new Date(observedAtRaw) : null;
  if (typeof collectorVersion !== "string" || !COLLECTOR_VERSION_PATTERN.test(collectorVersion)) {
    return invalid("provenance.collector_version must be a semantic version.");
  }
  if (!observedAt || Number.isNaN(observedAt.getTime())) return invalid("provenance.observed_at must be an ISO 8601 instant.");
  if (event.privacy_level !== undefined && event.privacy_level !== "normal" && event.privacy_level !== "sensitive") {
    return invalid("privacy_level must be normal or sensitive.");
  }
  if (event.invalidated !== undefined && typeof event.invalidated !== "boolean") return invalid("invalidated must be a boolean.");
  if (typeof event.payload !== "object" || event.payload === null || Array.isArray(event.payload)) {
    return invalid("payload must be an object.");
  }

  const item: ParsedBatchItem = {
    eventId,
    ownerId: event.owner_id,
    eventType,
    schemaVersion: 1,
    revision: event.revision,
    // Health observations default to sensitive (per registered schema); the
    // contract envelope default of normal still applies to activity events.
    privacy: event.privacy_level === undefined
      ? defaultPrivacyLevel(eventType)
      : event.privacy_level === "sensitive" ? "sensitive" : "normal",
    startAt,
    endAt,
    source: { kind: sourceKind, recordId: sourceRecordId },
    deviceClaimed: { id: deviceId, platform },
    captureTimezone: event.capture_timezone,
    captureOffsetMinutes: event.capture_offset_minutes,
    finalizationState: event.finalization_state,
    provenance: { collector_version: collectorVersion, observed_at: observedAt.toISOString() },
    invalidated: event.invalidated === true,
    payload: event.payload as Record<string, unknown>,
  };
  const payloadError = validateRegisteredEvent({
    eventType: item.eventType,
    schemaVersion: item.schemaVersion,
    sourceKind: item.source.kind,
    startAt: item.startAt,
    endAt: item.endAt,
    finalizationState: item.finalizationState,
    payload: item.payload,
  });
  if (payloadError) return { error: { code: "invalid_event", message: payloadError }, eventId, revision: item.revision };
  return { item };
}

function rawEventId(raw: unknown): string {
  const eventId = (raw as Record<string, unknown> | null)?.event_id;
  return typeof eventId === "string" ? eventId : "";
}

function rawRevision(raw: unknown): number {
  const revision = (raw as Record<string, unknown> | null)?.revision;
  return typeof revision === "number" && Number.isInteger(revision) && revision >= 1 ? revision : 1;
}

function acknowledgement(ack: EventAcknowledgement): EventAcknowledgement {
  return ack.status === "rejected" ? { ...ack, error: ack.error ?? { code: "invalid_event", message: "The event was rejected." } } : ack;
}

/**
 * Upserts one batch of envelope events with per-item acknowledgements and
 * partial success. A revision is answered by comparing it with the stored
 * revision of the same logical event: a redelivery of the stored revision is a
 * `duplicate`, a lower revision is `stale_revision` and never overwrites, and
 * a higher revision atomically replaces the stored fact after its superseded
 * snapshot is archived (facts are never destroyed).
 */
export async function batchUpsertEvents(env: Env, credential: CredentialAuthContext, events: unknown[]): Promise<EventBatchResponse> {
  const results: EventAcknowledgement[] = [];
  for (const raw of events) {
    results.push(await ingestBatchItem(env, credential, raw));
  }
  return { results };
}

function buildEventDocument(item: ParsedBatchItem, credential: CredentialAuthContext, bucket: string, now: Date) {
  const classification = item.payload.classification as Record<string, unknown> | undefined;
  const confidence = typeof classification?.confidence === "number" && Number.isFinite(classification.confidence)
    ? Math.max(0, Math.min(1, classification.confidence))
    : 1;
  return {
    bucket_id: bucket,
    user_id: credential.userId,
    device_id: credential.id,
    source: item.source.kind,
    type: item.eventType,
    schema_version: item.schemaVersion,
    revision: item.revision,
    finalization_state: item.finalizationState,
    provenance: item.provenance,
    capture_timezone: item.captureTimezone,
    capture_offset_minutes: item.captureOffsetMinutes,
    invalidated: item.invalidated,
    source_kind: item.source.kind,
    source_record_id: item.source.recordId,
    device_platform: item.deviceClaimed.platform,
    start_at: item.startAt,
    end_at: item.endAt,
    duration_ms: item.endAt ? Math.max(0, item.endAt.getTime() - item.startAt.getTime()) : null,
    value: null,
    unit: null,
    data: item.payload,
    privacy_level: item.privacy,
    confidence,
    updated_at: now,
  };
}

async function ingestBatchItem(env: Env, credential: CredentialAuthContext, raw: unknown): Promise<EventAcknowledgement> {
  const parsed = parseBatchItem(raw);
  if ("error" in parsed) {
    return acknowledgement({ event_id: parsed.eventId, revision: parsed.revision, status: "rejected", error: parsed.error });
  }
  const item = parsed.item;
  if (item.ownerId !== credential.userId) {
    return acknowledgement({
      event_id: item.eventId, revision: item.revision, status: "rejected",
      error: { code: "invalid_event", message: "owner_id must match the credential owner." },
    });
  }
  // Domain scopes are enforced per item: activity items need events:write and
  // health items need health:write, so a single-domain credential gets a
  // diagnosable rejection for out-of-domain items instead of a batch failure.
  const requiredScope = requiredWriteScope(item.eventType);
  if (!credential.scopes.includes(requiredScope)) {
    return acknowledgement({
      event_id: item.eventId, revision: item.revision, status: "rejected",
      error: { code: "insufficient_scope", message: `The credential lacks the ${requiredScope} scope required for ${item.eventType}.` },
    });
  }
  if (credential.allowed_event_types.length > 0 && !credential.allowed_event_types.includes(item.eventType)) {
    return acknowledgement({
      event_id: item.eventId, revision: item.revision, status: "rejected",
      error: { code: "event_type_not_allowed", message: "The credential may not upload this event type." },
    });
  }
  if (PRIVACY_RANK[item.privacy] > PRIVACY_RANK[credential.privacy_ceiling]) {
    return acknowledgement({
      event_id: item.eventId, revision: item.revision, status: "rejected",
      error: { code: "privacy_ceiling_exceeded", message: "The event exceeds the credential privacy ceiling." },
    });
  }

  const rawHash = hmacText(env.HASH_SECRET, `event:${item.eventId}`);
  const bucket = `${item.deviceClaimed.platform}:${credential.id}:${item.eventType}`;
  await BucketModel.updateOne(
    { id: bucket },
    {
      $setOnInsert: {
        id: bucket, user_id: credential.userId, device_id: credential.id,
        source: item.source.kind, type: item.eventType, metadata: {}, created_at: new Date(),
      },
    },
    { upsert: true },
  );

  // Resolution compares revisions atomically; a lost race re-reads and
  // re-answers from the stored state instead of overwriting blindly.
  for (let attempt = 0; attempt < 5; attempt++) {
    const stored = await EventModel.findOne({ raw_hash: rawHash }).lean<EventRow | null>();
    if (!stored) {
      const now = new Date();
      try {
        await EventModel.create({ id: item.eventId, raw_hash: rawHash, created_at: now, ...buildEventDocument(item, credential, bucket, now) });
        return { event_id: item.eventId, revision: item.revision, status: "accepted" };
      } catch (error) {
        if (!isDuplicateKeyError(error)) throw error;
        continue; // A concurrent writer created the event; compare revisions next round.
      }
    }

    const storedRevision = stored.revision ?? 0;
    if (item.revision === storedRevision) {
      return { event_id: item.eventId, revision: item.revision, status: "duplicate" };
    }
    if (item.revision < storedRevision) {
      return { event_id: item.eventId, revision: item.revision, status: "stale_revision" };
    }

    // Higher revision: archive the superseded snapshot first (idempotent), then
    // replace only while the stored revision is still the one we compared.
    await EventRevisionModel.updateOne(
      { id: `${stored.id}:${storedRevision}` },
      {
        $setOnInsert: {
          id: `${stored.id}:${storedRevision}`,
          event_id: stored.id,
          user_id: stored.user_id,
          revision: storedRevision,
          archived_at: new Date(),
          document: stored,
        },
      },
      { upsert: true },
    );
    const replacement = await EventModel.updateOne(
      { raw_hash: rawHash, revision: storedRevision },
      { $set: buildEventDocument(item, credential, bucket, new Date()) },
    );
    if (replacement.modifiedCount === 1) {
      return { event_id: item.eventId, revision: item.revision, status: "accepted" };
    }
  }
  throw new AppError(500, "The event revision could not be resolved after repeated races.", "internal_error");
}
