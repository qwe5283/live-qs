import { z } from "zod";
import type {
  CorrectionImpact,
  EventCorrectionRequest,
  EventCorrectionResult,
} from "../../generated/contract-models.js";
import { EventModel, EventRevisionModel } from "../../db/models.js";
import type { EventRow } from "../../types/contracts.js";
import { AppError } from "../../shared/errors.js";
import { recordAuditLog } from "../../shared/audit.js";
import { datesBetweenInclusive, localDateInTimezone } from "../../shared/date-utils.js";
import { getReportTimezone } from "../owner/settings.js";
import {
  HEALTH_METRIC_FOR_EVENT_TYPE,
  PAYMENT_TRANSACTION_TOTALS,
  USAGE_APP_MINUTES,
} from "../source-policy/policy.js";
import {
  CORRECTION_REVISION_BASE,
  REGISTERED_EVENT_TYPES,
  correctablePathsFor,
  validateRegisteredEvent,
} from "../events/payload-registry.js";
import type { RegisteredEventType } from "../events/payload-registry.js";
import { toEnvelopeEvent } from "../events/service.js";

const correctionRequestSchema = z.strictObject({
  fields: z
    .array(z.strictObject({
      path: z.string().regex(/^[a-zA-Z][a-zA-Z0-9_.]*$/, "path must be a dotted field path."),
      value: z.unknown(),
    }))
    .max(20)
    .optional(),
  reason: z.string().max(500).nullable().optional(),
  invalidate: z.boolean().optional(),
});

/** Envelope instant fields are corrected by path; payload paths address the stored structured payload. */
const ENVELOPE_INSTANT_PATHS = new Set(["start_at", "end_at"]);

function isRegisteredEventType(type: string | null): type is RegisteredEventType {
  return type !== null && (REGISTERED_EVENT_TYPES as readonly string[]).includes(type);
}

/** The derived metric a corrected event type feeds, so the impact statement names what rebuilds. */
function metricForEventType(eventType: string): string | null {
  if (eventType === "activity.interval") return USAGE_APP_MINUTES;
  if (eventType === "payment.transaction") return PAYMENT_TRANSACTION_TOTALS;
  return HEALTH_METRIC_FOR_EVENT_TYPE[eventType] ?? null;
}

/** Report days an instant or interval touches: local start date through the local last instant. */
function daysOfRange(startAt: Date, endAt: Date | null, timezone: string): string[] {
  const startDay = localDateInTimezone(startAt, timezone);
  if (!startDay) return [];
  if (!endAt) return [startDay];
  const endDay = localDateInTimezone(new Date(endAt.getTime() - 1), timezone);
  if (!endDay || endDay < startDay) return [startDay];
  return datesBetweenInclusive(startDay, endDay) ?? [startDay];
}

/**
 * Report days whose derived summaries the correction touches: the union of the
 * days the observation occupied before and after (a time correction moves the
 * contribution off one day and onto another), attributed in the Owner report
 * timezone so the statement is reproducible. Derived summaries rebuild on read
 * (compute-on-read), so the impact is a statement, not a write.
 */
function correctionImpact(eventType: string, timezone: string, before: { startAt: Date; endAt: Date | null }, after: { startAt: Date; endAt: Date | null }): CorrectionImpact[] {
  const metric = metricForEventType(eventType);
  if (!metric) return [];
  const days = [...new Set([...daysOfRange(before.startAt, before.endAt, timezone), ...daysOfRange(after.startAt, after.endAt, timezone)])].sort();
  return [{ metric, timezone, affected_ranges: days.map((day) => ({ from: day, to: day })), result_count: days.length }];
}

function parseInstant(value: unknown, path: string): Date {
  if (typeof value !== "string") {
    throw new AppError(400, `The correction field ${path} requires an ISO 8601 instant.`, "invalid_correction");
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    throw new AppError(400, `The correction field ${path} requires an ISO 8601 instant.`, "invalid_correction");
  }
  return parsed;
}

/** Replaces one dotted path inside the payload, creating intermediate objects. */
function setPayloadPath(payload: Record<string, unknown>, leafPath: string, value: unknown): void {
  const segments = leafPath.split(".");
  const last = segments.pop()!;
  let cursor = payload;
  for (const segment of segments) {
    const existing = cursor[segment];
    if (typeof existing !== "object" || existing === null || Array.isArray(existing)) {
      const created: Record<string, unknown> = {};
      cursor[segment] = created;
      cursor = created;
    } else {
      cursor = existing as Record<string, unknown>;
    }
  }
  cursor[last] = value;
}

function valueEquals(left: unknown, right: unknown): boolean {
  return JSON.stringify(left ?? null) === JSON.stringify(right ?? null);
}

interface AppliedCorrection {
  payload: Record<string, unknown>;
  startAt: Date;
  endAt: Date | null;
  changedFields: string[];
}

/**
 * Applies the requested fields to the stored row and re-derives a declared
 * duration from the corrected bounds. Values are compared against the stored
 * subtree so a request that changes nothing is refused instead of burning an
 * audit-bearing revision; the merged result is validated against the
 * event's registered schema so only contract-approved shapes can result.
 */
function applyCorrection(stored: EventRow, fields: NonNullable<EventCorrectionRequest["fields"]>): AppliedCorrection {
  const eventType = stored.type;
  if (!isRegisteredEventType(eventType)) {
    throw new AppError(400, "Only registered event types can be corrected.", "invalid_correction");
  }
  const allowlist = correctablePathsFor(eventType);

  const payload: Record<string, unknown> = structuredClone(stored.data ?? {});
  let startAt = new Date(stored.start_at);
  let endAt = stored.end_at ? new Date(stored.end_at) : null;
  const changedFields: string[] = [];

  for (const field of fields) {
    if (!allowlist.includes(field.path)) {
      throw new AppError(400, `The correction field ${field.path} is not correctable for ${eventType}.`, "field_not_correctable");
    }
    if (ENVELOPE_INSTANT_PATHS.has(field.path)) {
      const parsed = parseInstant(field.value, field.path);
      const current = field.path === "start_at" ? startAt : endAt;
      if (current && parsed.getTime() === current.getTime()) continue;
      if (field.path === "start_at") startAt = parsed;
      else endAt = parsed;
    } else {
      const leafPath = field.path.replace(/^payload\./, "");
      const previous = leafPath.split(".").reduce<unknown>(
        (cursor, segment) => (typeof cursor === "object" && cursor !== null ? (cursor as Record<string, unknown>)[segment] : undefined),
        payload,
      );
      if (valueEquals(field.value, previous)) continue;
      setPayloadPath(payload, leafPath, field.value);
    }
    changedFields.push(field.path);
  }

  // A declared duration always follows the corrected bounds; it is derived,
  // never hand-edited, keeping the registry's bounds/duration invariant true.
  const duration = payload.duration as { value?: unknown; unit?: unknown } | undefined;
  if (endAt && duration && typeof duration === "object") {
    duration.value = endAt.getTime() - startAt.getTime();
  }

  const payloadError = validateRegisteredEvent({
    eventType,
    schemaVersion: stored.schema_version ?? 1,
    sourceKind: stored.source_kind ?? stored.source,
    startAt,
    endAt,
    finalizationState: stored.finalization_state === "final" ? "final" : "checkpoint",
    payload,
  });
  if (payloadError) {
    throw new AppError(400, `The corrected event is not contract-valid: ${payloadError}`, "invalid_correction");
  }
  return { payload, startAt, endAt, changedFields };
}

/**
 * Applies an Owner correction to one logical event: the same event identity
 * receives a higher revision allocated in the reserved manual-correction
 * space, the superseded snapshot is archived first (facts are never
 * destroyed), and the replacement is conditional on the revision it was
 * computed from so a racing writer cannot be lost. Device revisions stay in
 * the low space, so every later device re-upload answers stale_revision under
 * the unchanged batch compare and can never overwrite the human
 * interpretation or resurrect an invalidated fact.
 */
export async function correctEvent(userId: string, eventId: string, request: EventCorrectionRequest): Promise<EventCorrectionResult> {
  const parsed = correctionRequestSchema.safeParse(request);
  if (!parsed.success) {
    throw new AppError(400, "The correction body must contain a fields array, an optional reason, and an optional invalidate flag.", "invalid_request");
  }
  const { reason, invalidate } = parsed.data;
  const fields = parsed.data.fields ?? [];
  const timezone = await getReportTimezone(userId);

  for (let attempt = 0; attempt < 5; attempt++) {
    const stored = await EventModel.findOne({ id: eventId, user_id: userId }).lean<EventRow | null>();
    if (!stored) throw new AppError(404, "The event does not exist.", "not_found");

    const applied = applyCorrection(stored, fields);
    const currentInvalidated = stored.invalidated === true;
    const nextInvalidated = invalidate === undefined ? currentInvalidated : invalidate;
    if (applied.changedFields.length === 0 && nextInvalidated === currentInvalidated) {
      throw new AppError(400, "The correction changes nothing: supply a field change or an invalidation change.", "invalid_correction");
    }

    const storedRevision = stored.revision ?? 0;
    const nextRevision = Math.max(storedRevision, CORRECTION_REVISION_BASE) + 1;
    const now = new Date();
    const impact = correctionImpact(stored.type, timezone, { startAt: stored.start_at, endAt: stored.end_at }, { startAt: applied.startAt, endAt: applied.endAt });

    await EventRevisionModel.updateOne(
      { id: `${stored.id}:${storedRevision}` },
      {
        $setOnInsert: {
          id: `${stored.id}:${storedRevision}`,
          event_id: stored.id,
          user_id: stored.user_id,
          revision: storedRevision,
          archived_at: now,
          document: stored,
        },
      },
      { upsert: true },
    );
    const replacement = await EventModel.updateOne(
      { id: stored.id, revision: storedRevision },
      {
        $set: {
          data: applied.payload,
          start_at: applied.startAt,
          end_at: applied.endAt,
          duration_ms: applied.endAt ? Math.max(0, applied.endAt.getTime() - applied.startAt.getTime()) : null,
          revision: nextRevision,
          invalidated: nextInvalidated,
          correction: { corrected_at: now.toISOString(), reason: reason ?? null },
          updated_at: now,
        },
      },
    );
    if (replacement.modifiedCount !== 1) continue; // A racing writer moved the row; re-read and re-apply.

    await recordAuditLog({
      userId,
      actorType: "user",
      action: "event.corrected",
      details: {
        event_id: stored.id,
        event_type: stored.type,
        from_revision: storedRevision,
        to_revision: nextRevision,
        changed_fields: applied.changedFields,
        reason: reason ?? null,
        invalidated: nextInvalidated,
        timezone,
        metrics_changed: impact.map((entry) => entry.metric),
        affected_ranges: impact.flatMap((entry) => entry.affected_ranges),
        result_count: impact.reduce((total, entry) => total + entry.result_count, 0),
      },
    });

    const correctedRow: EventRow = {
      ...stored,
      data: applied.payload,
      start_at: applied.startAt,
      end_at: applied.endAt,
      duration_ms: applied.endAt ? Math.max(0, applied.endAt.getTime() - applied.startAt.getTime()) : null,
      revision: nextRevision,
      invalidated: nextInvalidated,
      correction: { corrected_at: now.toISOString(), reason: reason ?? null },
      updated_at: now,
    };
    return {
      event: toEnvelopeEvent(correctedRow),
      changed_fields: applied.changedFields,
      reason: reason ?? null,
      invalidated: nextInvalidated,
      corrected_at: now.toISOString(),
      revision: nextRevision,
      impact,
    };
  }
  throw new AppError(500, "The correction could not be applied after repeated races.", "internal_error");
}
