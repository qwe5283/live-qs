import type {
  CredentialPrivacyCeiling,
  QueryContext,
  UsageDayReport,
  UsageDeviceMetrics,
  UsageMetrics,
  UsageWeekReport,
} from "../../generated/contract-models.js";
import { EventModel } from "../../db/models.js";
import type { EventRow } from "../../types/contracts.js";
import { zonedDayRange, zonedWeekRange } from "../../shared/date-utils.js";
import { privacyLevelsForRead, readableEventTypes } from "../events/service.js";
import { ACTIVITY_EVENT_TYPES } from "../events/payload-registry.js";
import { clipInterval, minutesFromMs, summedDurationMs, unionedDurationMs } from "./interval-metrics.js";
import type { ClippedInterval } from "./interval-metrics.js";

/**
 * Read restrictions for aggregate metrics. Aggregates respect the same
 * privacy ceiling and allowed event types as raw event reads: a credential
 * never learns aggregate numbers derived from data it may not read, and the
 * query context reports `partial` completeness when data was withheld.
 * Usage metrics only ever consume activity intervals; health observations
 * (steps, heart rate, sleep) never enter device or active minutes.
 */
export interface MetricsReadOptions {
  privacyCeiling?: CredentialPrivacyCeiling;
  allowedEventTypes?: string[];
}

/** The registered types usage metrics are computed from. */
const METRIC_EVENT_TYPES = ACTIVITY_EVENT_TYPES;

interface IntervalRow {
  deviceId: string;
  platform: "windows" | "android";
  isAfk: boolean;
  startAt: Date;
  endAt: Date | null;
  sourceKind: string | null;
}

/**
 * Fetches the latest non-invalidated activity intervals overlapping
 * [from, to). Intervals are selected by overlap rather than by start date so
 * an interval crossing a day or week boundary contributes to both sides.
 */
async function fetchIntervalRows(userId: string, from: Date, to: Date, options: MetricsReadOptions): Promise<IntervalRow[]> {
  const filter: Record<string, unknown> = {
    user_id: userId,
    type: { $in: readableEventTypes(options.allowedEventTypes, METRIC_EVENT_TYPES) },
    privacy_level: { $in: privacyLevelsForRead(options.privacyCeiling) },
    invalidated: { $ne: true },
    start_at: { $lt: to },
    end_at: { $gt: from },
  };
  const rows = await EventModel.find(filter).lean<EventRow[]>();
  return rows.map((row) => ({
    deviceId: row.device_id,
    platform: row.device_platform === "android" ? "android" : "windows",
    isAfk: (row.data as { is_afk?: unknown } | null)?.is_afk === true,
    startAt: row.start_at,
    endAt: row.end_at,
    sourceKind: row.source_kind ?? row.source ?? null,
  }));
}

function clippedIntervals(rows: IntervalRow[], rangeStartMs: number, rangeEndMs: number): Array<{ row: IntervalRow; clipped: ClippedInterval }> {
  const clipped: Array<{ row: IntervalRow; clipped: ClippedInterval }> = [];
  for (const row of rows) {
    if (!row.endAt) continue; // An open checkpoint has no known duration yet.
    const interval = clipInterval(row.startAt.getTime(), row.endAt.getTime(), rangeStartMs, rangeEndMs);
    if (interval) clipped.push({ row, clipped: interval });
  }
  return clipped;
}

function deviceMetrics(clipped: Array<{ row: IntervalRow; clipped: ClippedInterval }>): UsageDeviceMetrics[] {
  const lanes = new Map<string, { platform: "windows" | "android"; deviceIntervals: ClippedInterval[]; activeIntervals: ClippedInterval[] }>();
  for (const { row, clipped: interval } of clipped) {
    const lane = lanes.get(row.deviceId) ?? { platform: row.platform, deviceIntervals: [], activeIntervals: [] };
    lane.deviceIntervals.push(interval);
    if (!row.isAfk) lane.activeIntervals.push(interval);
    lanes.set(row.deviceId, lane);
  }
  return [...lanes]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([deviceId, lane]) => ({
      device_id: deviceId,
      platform: lane.platform,
      device_minutes: minutesFromMs(summedDurationMs(lane.deviceIntervals)),
      active_minutes: minutesFromMs(unionedDurationMs(lane.activeIntervals)),
    }));
}

/** Sums every clipped interval as device time and unions the non-AFK ones as active time. */
function summarizeUsage(clipped: Array<{ row: IntervalRow; clipped: ClippedInterval }>): UsageMetrics {
  return {
    device_minutes: minutesFromMs(summedDurationMs(clipped.map(({ clipped: interval }) => interval))),
    active_minutes: minutesFromMs(unionedDurationMs(clipped.filter(({ row }) => !row.isAfk).map(({ clipped: interval }) => interval))),
  };
}

function provenanceOf(rows: IntervalRow[]): string[] {
  return [...new Set(rows.map((row) => row.sourceKind).filter((kind): kind is string => Boolean(kind)))].sort();
}

/**
 * Builds the report context. When a credential's ceiling or event-type
 * restriction withheld in-range data, completeness reports `partial` instead
 * of `complete`.
 */
async function buildContext(
  userId: string,
  from: Date,
  to: Date,
  timezone: string,
  options: MetricsReadOptions,
  provenance: string[],
): Promise<QueryContext> {
  const credentialRestricted = options.privacyCeiling !== undefined
    || (options.allowedEventTypes !== undefined && options.allowedEventTypes.length > 0);
  let completeness: QueryContext["completeness"] = "complete";
  if (credentialRestricted) {
    const baseFilter = {
      user_id: userId,
      invalidated: { $ne: true },
      start_at: { $lt: to },
      end_at: { $gt: from },
    };
    const unrestricted = await EventModel.countDocuments({ ...baseFilter, type: { $in: readableEventTypes(undefined, METRIC_EVENT_TYPES) }, privacy_level: { $in: privacyLevelsForRead() } });
    const restricted = await EventModel.countDocuments({
      ...baseFilter,
      type: { $in: readableEventTypes(options.allowedEventTypes, METRIC_EVENT_TYPES) },
      privacy_level: { $in: privacyLevelsForRead(options.privacyCeiling) },
    });
    if (unrestricted > restricted) completeness = "partial";
  }
  return { from: from.toISOString(), to: to.toISOString(), timezone, provenance, completeness };
}

export async function usageDayReport(userId: string, date: string, timezone: string, options: MetricsReadOptions = {}): Promise<UsageDayReport | null> {
  const range = zonedDayRange(date, timezone);
  if (!range) return null;
  const rows = await fetchIntervalRows(userId, range.start, range.end, options);
  const clipped = clippedIntervals(rows, range.start.getTime(), range.end.getTime());
  const context = await buildContext(userId, range.start, range.end, timezone, options, provenanceOf(rows));
  return { date, timezone, metrics: summarizeUsage(clipped), devices: deviceMetrics(clipped), context };
}

export async function usageWeekReport(userId: string, date: string, timezone: string, options: MetricsReadOptions = {}): Promise<UsageWeekReport | null> {
  const week = zonedWeekRange(date, timezone);
  if (!week) return null;
  const rows = await fetchIntervalRows(userId, week.start, week.end, options);
  const weekIntervals = clippedIntervals(rows, week.start.getTime(), week.end.getTime());

  // Per-day attribution re-clips each interval against every local day so a
  // midnight crossover appears on both sides with its in-day portion.
  const days = week.dates.map((date) => {
    const dayRange = zonedDayRange(date, timezone);
    const dayIntervals = dayRange ? clippedIntervals(rows, dayRange.start.getTime(), dayRange.end.getTime()) : [];
    return { date, ...summarizeUsage(dayIntervals) };
  });

  const context = await buildContext(userId, week.start, week.end, timezone, options, provenanceOf(rows));
  return {
    week_start_date: week.dates[0]!,
    week_end_date: week.dates[week.dates.length - 1]!,
    timezone,
    metrics: summarizeUsage(weekIntervals),
    days,
    context,
  };
}
