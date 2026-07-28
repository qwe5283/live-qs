import type { DaySummary, FocusBlocksReport, SleepReport } from "@ai-life/shared";
import { formatZonedIso } from "../../shared/date-utils.js";

export interface ReportEventInput {
  device_id?: string;
  type: string;
  start_at: string;
  end_at: string | null;
  duration_ms: number | null;
  value: number | null;
  unit?: string | null;
  data_json: string;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

function parseEventData(value: string): Record<string, unknown> {
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function durationMinutes(event: ReportEventInput): number {
  if (typeof event.duration_ms === "number" && event.duration_ms > 0) {
    return Math.round(event.duration_ms / 60000);
  }
  if (event.end_at) {
    const start = new Date(event.start_at).getTime();
    const end = new Date(event.end_at).getTime();
    if (!Number.isNaN(start) && !Number.isNaN(end) && end > start) {
      return Math.round((end - start) / 60000);
    }
  }
  return typeof event.value === "number" && event.value > 0 ? Math.round(event.value) : 0;
}

function overlapMinutes(event: ReportEventInput, rangeStart: Date, rangeEnd: Date): number {
  const start = Math.max(new Date(event.start_at).getTime(), rangeStart.getTime());
  const end = Math.min(new Date(event.end_at || event.start_at).getTime(), rangeEnd.getTime());
  return Math.max(0, Math.round((end - start) / 60000));
}

export function buildScreenSummary(
  events: ReportEventInput[],
  rangeStart: Date,
  rangeEnd: Date,
): DaySummary["screen"] {
  const usageDevices = new Set<string>();
  const usageAppMinutes = new Map<string, number>();
  const realtimeAppMinutes = new Map<string, number>();
  let usageScreenMinutes = 0;
  let realtimeScreenMinutes = 0;
  let focusMinutes = 0;

  for (const event of events) {
    if (event.type !== "usage.app_daily" || typeof event.value !== "number" || event.value <= 0) continue;
    if (new Date(event.start_at).getTime() >= rangeEnd.getTime() || new Date(event.end_at || event.start_at).getTime() < rangeStart.getTime()) {
      continue;
    }

    const data = parseEventData(event.data_json);
    const deviceId = event.device_id || "unknown";
    const appName =
      (typeof data.app_name === "string" && data.app_name) ||
      (typeof data.package_name === "string" && data.package_name) ||
      "Unknown";
    const minutes = Math.round(event.value);
    if (minutes <= 0) continue;

    usageDevices.add(deviceId);
    usageScreenMinutes += minutes;
    usageAppMinutes.set(appName, (usageAppMinutes.get(appName) ?? 0) + minutes);
  }

  for (const event of events) {
    if (event.type !== "app.foreground" && event.type !== "app.heartbeat") continue;
    const data = parseEventData(event.data_json);
    if (data.is_afk === true) continue;

    const minutes = overlapMinutes(event, rangeStart, rangeEnd);
    if (minutes <= 0) continue;

    const deviceId = event.device_id || "unknown";
    if (data.category === "coding") {
      focusMinutes += minutes;
    }
    if (usageDevices.has(deviceId)) continue;

    realtimeScreenMinutes += minutes;
    const appName = typeof data.app_name === "string" && data.app_name ? data.app_name : "Unknown";
    realtimeAppMinutes.set(appName, (realtimeAppMinutes.get(appName) ?? 0) + minutes);
  }

  const appMinutes = new Map<string, number>(usageAppMinutes);
  for (const [app, minutes] of realtimeAppMinutes) {
    appMinutes.set(app, (appMinutes.get(app) ?? 0) + minutes);
  }

  return {
    active_screen_minutes: usageScreenMinutes + realtimeScreenMinutes,
    focus_minutes: focusMinutes,
    top_apps: [...appMinutes.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 8)
      .map(([app, minutes]) => ({ app, minutes })),
  };
}

function average(values: number[]): number | null {
  if (values.length === 0) return null;
  return Math.round(values.reduce((sum, value) => sum + value, 0) / values.length);
}

function sleepAssessment(totalMinutes: number): SleepReport["assessment"] {
  if (totalMinutes <= 0) {
    return { status: "no_data", message: "No sleep session was recorded for this day." };
  }
  if (totalMinutes < 300) {
    return { status: "short", message: "Recorded sleep is shorter than a healthy baseline for most adults." };
  }
  if (totalMinutes > 600) {
    return { status: "long", message: "Recorded sleep is longer than a typical daily sleep window." };
  }
  return { status: "normal", message: "Recorded sleep duration is within the expected daily range." };
}

export function buildSleepReport(
  date: string,
  timezone: string,
  events: ReportEventInput[],
  baselineDays: DaySummary[],
): SleepReport {
  const sessions = events
    .filter((event) => event.type === "health.sleep")
    .map((event) => ({
      start_at: event.start_at,
      end_at: event.end_at,
      start_at_local: formatZonedIso(event.start_at, timezone),
      end_at_local: formatZonedIso(event.end_at, timezone),
      duration_minutes: durationMinutes(event),
    }))
    .filter((session) => session.duration_minutes > 0)
    .sort((a, b) => a.start_at.localeCompare(b.start_at));

  const totalSleepMinutes = sessions.reduce((sum, session) => sum + session.duration_minutes, 0);

  return {
    date,
    timezone,
    total_sleep_minutes: totalSleepMinutes,
    sessions,
    baseline: {
      days: baselineDays.length,
      average_sleep_minutes: average(baselineDays.map((day) => day.health.sleep_minutes)),
    },
    assessment: sleepAssessment(totalSleepMinutes),
  };
}

interface FocusSegment {
  start_at: string;
  end_at: string;
  duration_minutes: number;
  app_name: string;
}

function segmentEnd(event: ReportEventInput): string | null {
  if (event.end_at) return event.end_at;
  const start = new Date(event.start_at).getTime();
  if (Number.isNaN(start)) return null;
  const minutes = durationMinutes(event);
  return minutes > 0 ? new Date(start + minutes * 60000).toISOString() : null;
}

export function buildFocusBlocksReport(
  date: string,
  timezone: string,
  events: ReportEventInput[],
): FocusBlocksReport {
  let activeScreenMinutes = 0;
  const segments: FocusSegment[] = [];

  for (const event of events) {
    if (event.type !== "app.foreground" && event.type !== "app.heartbeat") continue;
    const data = parseEventData(event.data_json);
    if (data.is_afk === true) continue;

    const minutes = durationMinutes(event);
    if (minutes <= 0) continue;
    activeScreenMinutes += minutes;

    if (data.category !== "coding") continue;
    const endAt = segmentEnd(event);
    if (!endAt) continue;
    const appName = typeof data.app_name === "string" && data.app_name ? data.app_name : "Unknown";
    segments.push({
      start_at: event.start_at,
      end_at: endAt,
      duration_minutes: minutes,
      app_name: appName,
    });
  }

  segments.sort((a, b) => a.start_at.localeCompare(b.start_at));

  const blocks: FocusBlocksReport["blocks"] = [];
  for (const segment of segments) {
    const previous = blocks[blocks.length - 1];
    if (previous) {
      const previousEnd = new Date(previous.end_at).getTime();
      const currentStart = new Date(segment.start_at).getTime();
      const gapMinutes = Math.round((currentStart - previousEnd) / 60000);
      if (!Number.isNaN(gapMinutes) && gapMinutes >= 0 && gapMinutes <= 5) {
        previous.end_at = segment.end_at > previous.end_at ? segment.end_at : previous.end_at;
        previous.duration_minutes += segment.duration_minutes;
        if (!previous.app_names.includes(segment.app_name)) previous.app_names.push(segment.app_name);
        continue;
      }
    }

    blocks.push({
      start_at: segment.start_at,
      end_at: segment.end_at,
      duration_minutes: segment.duration_minutes,
      app_names: [segment.app_name],
    });
  }

  const totalFocusMinutes = blocks.reduce((sum, block) => sum + block.duration_minutes, 0);

  return {
    date,
    timezone,
    total_focus_minutes: totalFocusMinutes,
    active_screen_minutes: activeScreenMinutes,
    focus_ratio: activeScreenMinutes > 0 ? round(totalFocusMinutes / activeScreenMinutes) : null,
    longest_focus_block_minutes: blocks.reduce((max, block) => Math.max(max, block.duration_minutes), 0),
    blocks: blocks.sort((a, b) => b.duration_minutes - a.duration_minutes).slice(0, 10),
  };
}
