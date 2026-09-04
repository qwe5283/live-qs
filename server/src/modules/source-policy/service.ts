import type { SourcePolicyDocument, SourcePolicyEntry, SourcePolicyImpact } from "../../generated/contract-models.js";
import { EventModel, SourcePolicyModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";
import { datesBetweenInclusive, localDateInTimezone } from "../../shared/date-utils.js";
import { recordAuditLog } from "../../shared/audit.js";
import { getReportTimezone } from "../owner/settings.js";
import { isOpaqueApplicationId, LEGAL_SOURCE_KINDS } from "../events/payload-registry.js";
import {
  HEALTH_METRIC_FOR_EVENT_TYPE,
  HEALTH_METRICS,
  PAYMENT_TRANSACTION_TOTALS,
  POLICY_METRICS,
  USAGE_APP_MINUTES,
  defaultPolicyState,
  priorityFor,
  selectActivityObservations,
  selectHealthObservations,
} from "./policy.js";
import type { ActivityObservation, HealthObservation, SourcePolicyState } from "./policy.js";

interface SourcePolicyRecord {
  user_id: string;
  version: number;
  entries: SourcePolicyEntry[];
  updated_at: Date;
}

/**
 * The effective policy state: the stored document, or the documented default
 * (version 1) until the Owner changes it. The default keeps live reports
 * stable — Windows foreground and Android UsageStats stay authoritative per
 * device and no competing kinds exist in current data.
 */
export async function effectivePolicyState(userId: string): Promise<SourcePolicyState> {
  const record = await SourcePolicyModel.findOne({ user_id: userId }).lean<SourcePolicyRecord | null>();
  if (!record) return defaultPolicyState();
  return { version: record.version, entries: record.entries ?? [], updatedAt: record.updated_at };
}

function toDocument(state: SourcePolicyState, impact?: SourcePolicyImpact[]): SourcePolicyDocument {
  return {
    version: state.version,
    entries: state.entries,
    updated_at: state.updatedAt ? state.updatedAt.toISOString() : null,
    ...(impact ? { impact } : {}),
  };
}

/** Reads the current policy document for the Owner UI. */
export async function readPolicyDocument(userId: string): Promise<SourcePolicyDocument> {
  return toDocument(await effectivePolicyState(userId));
}

function validateEntries(entries: SourcePolicyEntry[]): void {
  for (const entry of entries) {
    if (!(POLICY_METRICS as readonly string[]).includes(entry.metric)) {
      throw new AppError(400, `The metric ${entry.metric} has no registered source policy.`, "invalid_request");
    }
    if (!Array.isArray(entry.priority) || entry.priority.some((source) => typeof source !== "string")) {
      throw new AppError(400, "Each policy entry needs a priority array of source identifiers.", "invalid_request");
    }
    if (HEALTH_METRICS.includes(entry.metric)) {
      const invalid = entry.priority.find((origin) => !isOpaqueApplicationId(origin));
      if (invalid !== undefined) {
        throw new AppError(400, `Health policy priorities must be package-shaped origins, never paths: ${invalid}.`, "invalid_request");
      }
    } else {
      const invalid = entry.priority.find((kind) => !(LEGAL_SOURCE_KINDS as readonly string[]).includes(kind));
      if (invalid !== undefined) {
        throw new AppError(400, `Policy priorities for ${entry.metric} must be registered source kinds: ${invalid}.`, "invalid_request");
      }
    }
  }
}

/** Requested entries override the defaults; omitted metrics keep their default priority. */
function mergeEntries(entries: SourcePolicyEntry[]): SourcePolicyEntry[] {
  const overrides = new Map(entries.map((entry) => [entry.metric, entry.priority]));
  return defaultPolicyState().entries.map((entry) => {
    const priority = overrides.get(entry.metric);
    return priority ? { metric: entry.metric, priority: [...priority] } : entry;
  });
}

/**
 * Reports, per changed metric, which report-day ranges and how many
 * normalized day results a policy change affects. Days are attributed in the
 * Owner report timezone so the audit statement is reproducible. Payment
 * totals deliberately cover every observation, so their priority records a
 * preference without ever changing a result.
 */
export async function policyImpact(userId: string, timezone: string, before: SourcePolicyState, after: SourcePolicyState): Promise<SourcePolicyImpact[]> {
  const impacts: SourcePolicyImpact[] = [];
  for (const metric of POLICY_METRICS) {
    const beforePriority = priorityFor(before, metric);
    const afterPriority = priorityFor(after, metric);
    if (JSON.stringify(beforePriority) === JSON.stringify(afterPriority)) continue;
    if (metric === PAYMENT_TRANSACTION_TOTALS) {
      impacts.push({ metric, from_version: before.version, to_version: after.version, timezone, affected_ranges: [], result_count: 0 });
      continue;
    }
    const affectedDays = metric === USAGE_APP_MINUTES
      ? await affectedUsageDays(userId, timezone, beforePriority, afterPriority)
      : await affectedHealthDays(userId, timezone, metric, beforePriority, afterPriority);
    impacts.push({
      metric,
      from_version: before.version,
      to_version: after.version,
      timezone,
      affected_ranges: affectedDays.map((day) => ({ from: day, to: day })),
      result_count: affectedDays.length,
    });
  }
  return impacts;
}

/** Report days an interval touches: its local start date through its local last instant. */
function daysOfInterval(startAt: Date, endAt: Date | null, timezone: string): string[] {
  const startDay = localDateInTimezone(startAt, timezone);
  if (!startDay) return [];
  if (!endAt) return [startDay];
  const endDay = localDateInTimezone(new Date(endAt.getTime() - 1), timezone);
  if (!endDay || endDay < startDay) return [startDay];
  return datesBetweenInclusive(startDay, endDay) ?? [startDay];
}

/** Report days whose selection changes when an observation's contribution flips. */
function daysOfFlippedObservations(
  observations: Array<{ id: string; startAt: Date; endAt: Date | null }>,
  beforeSelected: Set<string>,
  afterSelected: Set<string>,
  timezone: string,
): string[] {
  const days = new Set<string>();
  for (const observation of observations) {
    if (beforeSelected.has(observation.id) === afterSelected.has(observation.id)) continue;
    for (const day of daysOfInterval(observation.startAt, observation.endAt, timezone)) days.add(day);
  }
  return [...days].sort();
}

async function affectedUsageDays(userId: string, timezone: string, beforePriority: string[], afterPriority: string[]): Promise<string[]> {
  const rows = await EventModel.find(
    { user_id: userId, type: "activity.interval", invalidated: { $ne: true } },
    { id: 1, device_id: 1, source_kind: 1, source: 1, start_at: 1, end_at: 1 },
  ).lean<{ id: string; device_id: string; source_kind: string | null; source: string; start_at: Date; end_at: Date | null }[]>();
  const observations: ActivityObservation[] = rows.map((row) => ({
    id: row.id,
    deviceId: row.device_id,
    sourceKind: row.source_kind ?? row.source,
    startMs: row.start_at.getTime(),
    endMs: row.end_at ? row.end_at.getTime() : null,
  }));
  const beforeSelected = new Set(selectActivityObservations(observations, beforePriority, 1).selected.map((row) => row.id));
  const afterSelected = new Set(selectActivityObservations(observations, afterPriority, 1).selected.map((row) => row.id));
  return daysOfFlippedObservations(
    rows.map((row) => ({ id: row.id, startAt: row.start_at, endAt: row.end_at })),
    beforeSelected,
    afterSelected,
    timezone,
  );
}

async function affectedHealthDays(userId: string, timezone: string, metric: string, beforePriority: string[], afterPriority: string[]): Promise<string[]> {
  const eventType = Object.entries(HEALTH_METRIC_FOR_EVENT_TYPE).find(([, metricKey]) => metricKey === metric)?.[0];
  const rows = await EventModel.find(
    { user_id: userId, type: eventType, invalidated: { $ne: true } },
    { id: 1, start_at: 1, end_at: 1, data: 1 },
  ).lean<{ id: string; start_at: Date; end_at: Date | null; data: { data_origin?: unknown } | null }[]>();
  const observations: HealthObservation[] = rows.map((row) => ({
    id: row.id,
    metric,
    origin: typeof row.data?.data_origin === "string" ? row.data.data_origin : "unknown",
    startMs: row.start_at.getTime(),
    endMs: (row.end_at ?? row.start_at).getTime(),
  }));
  const beforeWithheld = selectHealthObservations(observations, { [metric]: beforePriority }, 1).withheldIds;
  const afterWithheld = selectHealthObservations(observations, { [metric]: afterPriority }, 1).withheldIds;
  return daysOfFlippedObservations(
    rows.map((row) => ({ id: row.id, startAt: row.start_at, endAt: row.end_at })),
    beforeWithheld,
    afterWithheld,
    timezone,
  );
}

/**
 * Replaces the policy entries, bumps the version, and appends an audit record
 * stating which report-day ranges and how many normalized results the change
 * affected. Derived results rebuild on read, so nothing else is written: raw
 * observations are never modified by a policy change.
 */
export async function updatePolicyDocument(userId: string, entries: SourcePolicyEntry[]): Promise<SourcePolicyDocument> {
  validateEntries(entries);
  const before = await effectivePolicyState(userId);
  const timezone = await getReportTimezone(userId);
  const after: SourcePolicyState = { version: before.version + 1, entries: mergeEntries(entries), updatedAt: new Date() };
  const impact = await policyImpact(userId, timezone, before, after);

  await SourcePolicyModel.updateOne(
    { user_id: userId },
    { $set: { user_id: userId, version: after.version, entries: after.entries, updated_at: after.updatedAt } },
    { upsert: true },
  );
  await recordAuditLog({
    userId,
    actorType: "user",
    action: "source_policy.update",
    details: {
      from_version: before.version,
      to_version: after.version,
      timezone,
      metrics_changed: impact.map((entry) => entry.metric),
      affected_ranges: impact.flatMap((entry) => entry.affected_ranges),
      result_count: impact.reduce((total, entry) => total + entry.result_count, 0),
    },
  });
  return toDocument(after, impact);
}
