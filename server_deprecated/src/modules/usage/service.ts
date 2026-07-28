import { eventsInRange, type EventRow } from "../../db";
import { parseJsonString } from "../../shared/validation";
import { screenTimeSummary } from "../reports/service";

export interface UsageAppSummary {
  app: string;
  app_id?: string;
  device_id: string;
  minutes: number;
  sessions: number;
  hourly_minutes: number[];
  source: "usage_events" | "realtime";
}

export interface UsageAppsResponse {
  start: string;
  end: string;
  apps: UsageAppSummary[];
}

export interface UsageTimelineSegment {
  device_id: string;
  app: string;
  app_id?: string;
  start_at: string;
  end_at: string | null;
  minutes: number;
  is_afk: boolean;
  category?: string;
}

export interface UsageTimelineResponse {
  start: string;
  end: string;
  segments: UsageTimelineSegment[];
}

export function usageSummary(start: Date, end: Date) {
  return screenTimeSummary(start, end);
}

export function usageApps(start: Date, end: Date): UsageAppsResponse {
  const rows = eventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  const usageDevices = new Set<string>();
  const apps = new Map<string, UsageAppSummary>();

  for (const row of rows) {
    if (row.type !== "usage.app_daily" || typeof row.value !== "number" || row.value <= 0) continue;
    const data = parseJsonString(row.data_json);
    const appId = text(data.package_name) || text(data.app_id);
    const app = text(data.app_name) || appId || "Unknown";
    const key = `${row.device_id}:${appId || app}`;
    const current = apps.get(key) ?? emptyApp(app, row.device_id, appId, "usage_events");
    current.minutes += Math.round(row.value);
    current.sessions += numberValue(data.foreground_session_count);
    const hourly = Array.isArray(data.hourly_minutes) ? data.hourly_minutes : [];
    for (let i = 0; i < 24; i++) {
      current.hourly_minutes[i] += numberValue(hourly[i]);
    }
    apps.set(key, current);
    usageDevices.add(row.device_id);
  }

  for (const row of rows) {
    if (usageDevices.has(row.device_id)) continue;
    if (row.type !== "app.foreground" && row.type !== "app.heartbeat") continue;
    const data = parseJsonString(row.data_json);
    if (data.is_afk === true) continue;

    const minutes = overlapMinutes(row, start, end);
    if (minutes <= 0) continue;
    const appId = text(data.app_id);
    const app = text(data.app_name) || appId || "Unknown";
    const key = `${row.device_id}:${appId || app}`;
    const current = apps.get(key) ?? emptyApp(app, row.device_id, appId, "realtime");
    current.minutes += minutes;
    current.sessions += 1;
    const hour = new Date(row.start_at).getHours();
    current.hourly_minutes[hour] += minutes;
    apps.set(key, current);
  }

  return {
    start: start.toISOString(),
    end: end.toISOString(),
    apps: [...apps.values()].sort((a, b) => b.minutes - a.minutes),
  };
}

export function usageTimeline(start: Date, end: Date): UsageTimelineResponse {
  const rows = eventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  const segments = rows
    .filter((row) => row.type === "app.foreground" || row.type === "app.heartbeat")
    .map((row): UsageTimelineSegment | null => {
      const data = parseJsonString(row.data_json);
      const minutes = overlapMinutes(row, start, end);
      if (minutes <= 0) return null;
      const appId = text(data.app_id);
      return {
        device_id: row.device_id,
        app: text(data.app_name) || appId || "Unknown",
        app_id: appId || undefined,
        start_at: row.start_at,
        end_at: row.end_at,
        minutes,
        is_afk: data.is_afk === true,
        category: text(data.category) || undefined,
      };
    })
    .filter((segment): segment is UsageTimelineSegment => Boolean(segment))
    .sort((a, b) => a.start_at.localeCompare(b.start_at));

  return {
    start: start.toISOString(),
    end: end.toISOString(),
    segments,
  };
}

function emptyApp(app: string, deviceId: string, appId: string, source: UsageAppSummary["source"]): UsageAppSummary {
  return {
    app,
    app_id: appId || undefined,
    device_id: deviceId,
    minutes: 0,
    sessions: 0,
    hourly_minutes: Array.from({ length: 24 }, () => 0),
    source,
  };
}

function overlapMinutes(row: EventRow, start: Date, end: Date): number {
  const rowStart = Math.max(new Date(row.start_at).getTime(), start.getTime());
  const rowEnd = Math.min(new Date(row.end_at || row.start_at).getTime(), end.getTime());
  return Math.max(0, Math.round((rowEnd - rowStart) / 60000));
}

function text(value: unknown): string {
  return typeof value === "string" && value.trim() ? value.trim() : "";
}

function numberValue(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}
