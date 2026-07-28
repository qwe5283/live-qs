import { DailyRollupModel, DeviceStateModel } from "../../db/models.js";
import { eventsInRange, healthEventTypes } from "../../db/queries.js";
import { addUtcDays, datesBetweenInclusive, datesEndingOn, zonedDayRange } from "../../shared/date-utils.js";
import type { DaySummary, EventRow } from "../../types/contracts.js";
import { buildAnomalies, buildFocusBlocks, buildSleep, summarizeHealth, summarizeScreen, summarizeSpending } from "./analytics.js";

export async function currentContext(userId: string) {
  const now = new Date();
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  const [devices, events, health] = await Promise.all([
    DeviceStateModel.find({ user_id: userId }).sort({ last_seen_at: -1 }).lean().exec(),
    eventsInRange(userId, start, now),
    eventsInRange(userId, start, now, healthEventTypes),
  ]);
  return {
    server_time: now.toISOString(), user_id: userId,
    devices: devices.map((device) => {
      const ago = Math.max(0, Math.round((now.getTime() - device.last_seen_at.getTime()) / 60_000));
      const data = device.current_data as Record<string, unknown>;
      return { device_id: device.device_id, device_name: device.device_name, platform: device.platform,
        online: device.is_online && ago <= 2, current_type: device.current_type,
        ...(text(data.app_name) ? { current_app: text(data.app_name) } : {}),
        ...(text(data.category) ? { current_category: text(data.category) } : {}),
        is_afk: data.is_afk === true, last_seen_minutes_ago: ago };
    }),
    today: {
      ...summarizeHealth(health),
      active_screen_minutes: summarizeScreen(events, start, now).active_screen_minutes,
      focus_minutes: summarizeScreen(events, start, now).focus_minutes,
      spending_total: summarizeSpending(events, start, now).total_expense,
      spending_currency: summarizeSpending(events, start, now).currency,
      top_apps: summarizeScreen(events, start, now).top_apps.slice(0, 5),
    },
  };
}

export async function daySummary(userId: string, date: string, timezone: string): Promise<DaySummary | null> {
  const range = zonedDayRange(date, timezone);
  if (!range) return null;
  const [events, health] = await Promise.all([
    eventsInRange(userId, range.start, range.end),
    eventsInRange(userId, range.start, range.end, healthEventTypes),
  ]);
  return { date, timezone, health: summarizeHealth(health), screen: summarizeScreen(events, range.start, range.end),
    spending: summarizeSpending(events, range.start, range.end) };
}

export async function weekSummary(userId: string, endDate: string, timezone: string) {
  const dates = datesEndingOn(endDate, 7);
  if (!dates) return null;
  const days = await Promise.all(dates.map((date) => daySummary(userId, date, timezone)));
  if (days.some((day) => !day)) return null;
  const validDays = days as DaySummary[];
  const first = zonedDayRange(dates[0]!, timezone);
  const last = zonedDayRange(dates.at(-1)!, timezone);
  if (!first || !last) return null;
  const [events, healthEvents] = await Promise.all([
    eventsInRange(userId, first.start, last.end), eventsInRange(userId, first.start, last.end, healthEventTypes),
  ]);
  const health = summarizeHealth(healthEvents);
  return { start_date: dates[0], end_date: dates.at(-1), timezone,
    health: { steps: health.steps, average_daily_steps: Math.round(health.steps / validDays.length),
      sleep_minutes: health.sleep_minutes, avg_heart_rate: health.avg_heart_rate },
    screen: summarizeScreen(events, first.start, last.end), spending: summarizeSpending(events, first.start, last.end), days: validDays };
}

async function baselineDays(userId: string, date: string, timezone: string): Promise<DaySummary[] | null> {
  const previous = addUtcDays(date, -1);
  const dates = previous ? datesEndingOn(previous, 7) : null;
  if (!dates) return null;
  const days = await Promise.all(dates.map((day) => daySummary(userId, day, timezone)));
  return days.some((day) => !day) ? null : days as DaySummary[];
}

export async function lifestyleAnomalies(userId: string, date: string, timezone: string) {
  const [today, baseline] = await Promise.all([daySummary(userId, date, timezone), baselineDays(userId, date, timezone)]);
  return today && baseline ? buildAnomalies(date, timezone, today, baseline) : null;
}

export async function sleepReport(userId: string, date: string, timezone: string) {
  const range = zonedDayRange(date, timezone);
  const baseline = await baselineDays(userId, date, timezone);
  if (!range || !baseline) return null;
  return buildSleep(date, timezone, await eventsInRange(userId, range.start, range.end, healthEventTypes), baseline);
}

export async function focusBlocksReport(userId: string, date: string, timezone: string) {
  const range = zonedDayRange(date, timezone);
  return range ? buildFocusBlocks(date, timezone, await eventsInRange(userId, range.start, range.end)) : null;
}

export async function usageSummary(userId: string, start: Date, end: Date) {
  return { start: start.toISOString(), end: end.toISOString(), ...summarizeScreen(await eventsInRange(userId, start, end), start, end) };
}

export async function usageApps(userId: string, start: Date, end: Date) {
  const events = await eventsInRange(userId, start, end);
  const aggregateDevices = new Set<string>();
  const apps = new Map<string, { app: string; app_id?: string; device_id: string; minutes: number; sessions: number; hourly_minutes: number[]; source: "usage_events" | "realtime" }>();
  for (const event of events) {
    if (event.type !== "usage.app_daily" || !event.value || event.value <= 0) continue;
    const appId = text(event.data.package_name) || text(event.data.app_id);
    const app = text(event.data.app_name) || appId || "Unknown";
    const key = `${event.device_id}:${appId || app}`;
    const current = apps.get(key) ?? appItem(app, event.device_id, appId, "usage_events");
    current.minutes += Math.round(event.value);
    current.sessions += number(event.data.foreground_session_count);
    const hourly = Array.isArray(event.data.hourly_minutes) ? event.data.hourly_minutes : [];
    for (let index = 0; index < 24; index++) current.hourly_minutes[index] = (current.hourly_minutes[index] ?? 0) + number(hourly[index]);
    apps.set(key, current);
    aggregateDevices.add(event.device_id);
  }
  for (const event of events) {
    if (aggregateDevices.has(event.device_id) || !["app.foreground", "app.heartbeat"].includes(event.type) || event.data.is_afk === true) continue;
    const minutes = overlap(event, start, end);
    if (minutes <= 0) continue;
    const appId = text(event.data.app_id);
    const app = text(event.data.app_name) || appId || "Unknown";
    const key = `${event.device_id}:${appId || app}`;
    const current = apps.get(key) ?? appItem(app, event.device_id, appId, "realtime");
    current.minutes += minutes;
    current.sessions++;
    current.hourly_minutes[event.start_at.getHours()] = (current.hourly_minutes[event.start_at.getHours()] ?? 0) + minutes;
    apps.set(key, current);
  }
  return { start: start.toISOString(), end: end.toISOString(), apps: [...apps.values()].sort((a, b) => b.minutes - a.minutes) };
}

export async function usageTimeline(userId: string, start: Date, end: Date) {
  const segments = (await eventsInRange(userId, start, end)).filter((event) => ["app.foreground", "app.heartbeat"].includes(event.type))
    .map((event) => {
      const minutes = overlap(event, start, end);
      const appId = text(event.data.app_id);
      return minutes > 0 ? { device_id: event.device_id, app: text(event.data.app_name) || appId || "Unknown",
        ...(appId ? { app_id: appId } : {}), start_at: event.start_at.toISOString(), end_at: event.end_at?.toISOString() ?? null,
        minutes, is_afk: event.data.is_afk === true, ...(text(event.data.category) ? { category: text(event.data.category) } : {}) } : null;
    }).filter((item): item is NonNullable<typeof item> => Boolean(item));
  return { start: start.toISOString(), end: end.toISOString(), segments };
}

export async function healthSummary(userId: string, start: Date, end: Date) {
  return { start: start.toISOString(), end: end.toISOString(),
    ...summarizeHealth(await eventsInRange(userId, start, end, healthEventTypes)) };
}

export async function healthTimeline(userId: string, start: Date, end: Date) {
  const events = await eventsInRange(userId, start, end, healthEventTypes);
  return { start: start.toISOString(), end: end.toISOString(), records: events.map((event) => ({
    type: event.type, start_at: event.start_at.toISOString(), end_at: event.end_at?.toISOString() ?? null,
    value: event.value, unit: event.unit, duration_minutes: event.duration_ms ? Math.round(event.duration_ms / 60_000) : null,
  })) };
}

export async function spendingSummary(userId: string, start: Date, end: Date) {
  return summarizeSpending(await eventsInRange(userId, start, end), start, end);
}

export async function persistDayRollup(userId: string, date: string, timezone: string) {
  const summary = await daySummary(userId, date, timezone);
  if (!summary) return null;
  const now = new Date();
  const existing = await DailyRollupModel.exists({ user_id: userId, date, timezone });
  await DailyRollupModel.updateOne({ user_id: userId, date, timezone },
    { $set: { summary, updated_at: now }, $setOnInsert: { created_at: now } }, { upsert: true });
  return { date, timezone, created: !existing, summary };
}

export async function backfillDayRollups(userId: string, startDate: string, endDate: string, timezone: string) {
  const dates = datesBetweenInclusive(startDate, endDate, 366);
  if (!dates) return null;
  for (const date of dates) if (!await persistDayRollup(userId, date, timezone)) return null;
  return { start_date: startDate, end_date: endDate, timezone, upserted: dates.length, dates };
}

function appItem(app: string, deviceId: string, appId: string, source: "usage_events" | "realtime") {
  return { app, ...(appId ? { app_id: appId } : {}), device_id: deviceId, minutes: 0, sessions: 0,
    hourly_minutes: Array.from({ length: 24 }, () => 0), source };
}
function overlap(event: EventRow, start: Date, end: Date): number {
  return Math.max(0, Math.round((Math.min((event.end_at ?? event.start_at).getTime(), end.getTime()) - Math.max(event.start_at.getTime(), start.getTime())) / 60_000));
}
function text(value: unknown): string { return typeof value === "string" && value.trim() ? value.trim() : ""; }
function number(value: unknown): number { return typeof value === "number" && Number.isFinite(value) ? value : 0; }
