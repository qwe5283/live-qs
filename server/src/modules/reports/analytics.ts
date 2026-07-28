import { formatZonedIso } from "../../shared/date-utils.js";
import type { DaySummary, EventRow, HealthMetrics, ScreenSummary, SpendingSummary } from "../../types/contracts.js";

function overlapMinutes(event: EventRow, start: Date, end: Date): number {
  const eventStart = Math.max(event.start_at.getTime(), start.getTime());
  const eventEnd = Math.min((event.end_at ?? event.start_at).getTime(), end.getTime());
  return Math.max(0, Math.round((eventEnd - eventStart) / 60_000));
}

function durationMinutes(event: EventRow): number {
  if (event.duration_ms && event.duration_ms > 0) return Math.round(event.duration_ms / 60_000);
  if (event.end_at) return Math.max(0, Math.round((event.end_at.getTime() - event.start_at.getTime()) / 60_000));
  return event.value && event.value > 0 ? Math.round(event.value) : 0;
}

export function summarizeScreen(events: EventRow[], rangeStart: Date, rangeEnd: Date): ScreenSummary {
  const aggregateDevices = new Set<string>();
  const aggregateApps = new Map<string, number>();
  const realtimeApps = new Map<string, number>();
  let aggregateMinutes = 0;
  let realtimeMinutes = 0;
  let focusMinutes = 0;

  for (const event of events) {
    if (event.type !== "usage.app_daily" || !event.value || event.value <= 0) continue;
    const app = text(event.data.app_name) || text(event.data.package_name) || "Unknown";
    const minutes = Math.round(event.value);
    aggregateDevices.add(event.device_id);
    aggregateMinutes += minutes;
    aggregateApps.set(app, (aggregateApps.get(app) ?? 0) + minutes);
  }

  for (const event of events) {
    if (!["app.foreground", "app.heartbeat"].includes(event.type) || event.data.is_afk === true) continue;
    const minutes = overlapMinutes(event, rangeStart, rangeEnd);
    if (minutes <= 0) continue;
    if (event.data.category === "coding") focusMinutes += minutes;
    if (aggregateDevices.has(event.device_id)) continue;
    const app = text(event.data.app_name) || "Unknown";
    realtimeMinutes += minutes;
    realtimeApps.set(app, (realtimeApps.get(app) ?? 0) + minutes);
  }

  const apps = new Map(aggregateApps);
  for (const [app, minutes] of realtimeApps) apps.set(app, (apps.get(app) ?? 0) + minutes);
  return {
    active_screen_minutes: aggregateMinutes + realtimeMinutes,
    focus_minutes: focusMinutes,
    top_apps: [...apps].sort((a, b) => b[1] - a[1]).slice(0, 8).map(([app, minutes]) => ({ app, minutes })),
  };
}

export function summarizeHealth(events: EventRow[]): HealthMetrics {
  let steps = 0;
  let sleepMinutes = 0;
  const rates: number[] = [];
  for (const event of events) {
    if (event.type === "health.steps" && event.value !== null) steps += event.value;
    else if (event.type === "health.sleep") sleepMinutes += durationMinutes(event);
    else if (event.type === "health.heart_rate" && event.value !== null) rates.push(event.value);
  }
  return {
    steps: Math.round(steps),
    sleep_minutes: sleepMinutes,
    avg_heart_rate: rates.length ? Math.round(rates.reduce((sum, value) => sum + value, 0) / rates.length) : null,
  };
}

export function summarizeSpending(events: EventRow[], start: Date, end: Date): SpendingSummary {
  let expense = 0;
  let income = 0;
  let count = 0;
  let currency = "CNY";
  const categories = new Map<string, number>();
  const merchants = new Map<string, number>();
  for (const event of events) {
    if (event.type !== "payment.transaction" || event.value === null || event.start_at < start || event.start_at >= end) continue;
    const amount = event.value;
    const absolute = Math.abs(amount);
    const direction = text(event.data.direction) || (amount < 0 ? "expense" : "income");
    currency = event.unit || currency;
    count++;
    if (direction === "income" || amount > 0) income += absolute;
    else {
      expense += absolute;
      const merchant = text(event.data.merchant) || "unknown";
      const category = text(event.data.category) || categorize(merchant, text(event.data.product));
      categories.set(category, (categories.get(category) ?? 0) + absolute);
      merchants.set(merchant, (merchants.get(merchant) ?? 0) + absolute);
    }
  }
  const top = (items: Map<string, number>, key: "category" | "merchant") => [...items]
    .sort((a, b) => b[1] - a[1]).slice(0, 8).map(([name, amount]) => ({ [key]: name, amount: money(amount) }));
  return {
    start: start.toISOString(), end: end.toISOString(), total_expense: money(expense), total_income: money(income),
    net: money(income - expense), currency, transaction_count: count,
    top_categories: top(categories, "category") as Array<{ category: string; amount: number }>,
    top_merchants: top(merchants, "merchant") as Array<{ merchant: string; amount: number }>,
  };
}

export function buildAnomalies(date: string, timezone: string, today: DaySummary, baseline: DaySummary[]) {
  const average = (values: number[]) => values.length ? round(values.reduce((sum, value) => sum + value, 0) / values.length) : null;
  const metrics = {
    steps: average(baseline.map((day) => day.health.steps)),
    sleep: average(baseline.map((day) => day.health.sleep_minutes)),
    screen: average(baseline.map((day) => day.screen.active_screen_minutes)),
    focus: average(baseline.map((day) => day.screen.focus_minutes)),
    spending: average(baseline.map((day) => day.spending.total_expense)),
  };
  const anomalies: Array<Record<string, unknown>> = [];
  const add = (type: string, severity: string, metric: string, value: number, base: number, unit: string, message: string) =>
    anomalies.push({ type, severity, metric, value: round(value), baseline: round(base), unit, message });
  if (metrics.steps !== null && metrics.steps >= 2000 && today.health.steps < metrics.steps * 0.6)
    add("low_steps", today.health.steps < metrics.steps * 0.35 ? "critical" : "warning", "steps", today.health.steps, metrics.steps, "count", "Step count is materially below the recent baseline.");
  if (metrics.sleep !== null && metrics.sleep >= 240 && today.health.sleep_minutes < Math.min(360, metrics.sleep * 0.75))
    add("short_sleep", today.health.sleep_minutes < 300 ? "critical" : "warning", "sleep_minutes", today.health.sleep_minutes, metrics.sleep, "minutes", "Sleep duration is below the recent baseline.");
  if (metrics.screen !== null && metrics.screen >= 60 && today.screen.active_screen_minutes > metrics.screen * 1.4)
    add("high_screen_time", today.screen.active_screen_minutes > metrics.screen * 2 ? "critical" : "warning", "active_screen_minutes", today.screen.active_screen_minutes, metrics.screen, "minutes", "Active screen time is above the recent baseline.");
  if (metrics.spending !== null && metrics.spending >= 20 && today.spending.total_expense > metrics.spending * 1.8)
    add("high_spending", today.spending.total_expense > metrics.spending * 3 ? "critical" : "warning", "spending_expense", today.spending.total_expense, metrics.spending, today.spending.currency, "Spending is above the recent baseline.");
  if (!anomalies.length) add("no_major_anomaly", "info", "overall", 0, 0, "none", "No major lifestyle anomaly was detected against the recent baseline.");
  return {
    date, timezone,
    baseline: { start_date: baseline[0]?.date ?? null, end_date: baseline.at(-1)?.date ?? null, days: baseline.length,
      average_steps: metrics.steps, average_sleep_minutes: metrics.sleep, average_active_screen_minutes: metrics.screen,
      average_focus_minutes: metrics.focus, average_spending_expense: metrics.spending },
    metrics: { steps: today.health.steps, sleep_minutes: today.health.sleep_minutes,
      active_screen_minutes: today.screen.active_screen_minutes, focus_minutes: today.screen.focus_minutes,
      focus_ratio: today.screen.active_screen_minutes ? round(today.screen.focus_minutes / today.screen.active_screen_minutes) : null,
      spending_expense: today.spending.total_expense, currency: today.spending.currency },
    anomalies,
  };
}

export function buildSleep(date: string, timezone: string, events: EventRow[], baseline: DaySummary[]) {
  const sessions = events.filter((event) => event.type === "health.sleep").map((event) => ({
    start_at: event.start_at.toISOString(), end_at: event.end_at?.toISOString() ?? null,
    start_at_local: formatZonedIso(event.start_at, timezone), end_at_local: formatZonedIso(event.end_at, timezone),
    duration_minutes: durationMinutes(event),
  })).filter((session) => session.duration_minutes > 0).sort((a, b) => a.start_at.localeCompare(b.start_at));
  const total = sessions.reduce((sum, session) => sum + session.duration_minutes, 0);
  const average = baseline.length ? Math.round(baseline.reduce((sum, day) => sum + day.health.sleep_minutes, 0) / baseline.length) : null;
  const assessment = total <= 0 ? ["no_data", "No sleep session was recorded for this day."]
    : total < 300 ? ["short", "Recorded sleep is shorter than a healthy baseline for most adults."]
    : total > 600 ? ["long", "Recorded sleep is longer than a typical daily sleep window."]
    : ["normal", "Recorded sleep duration is within the expected daily range."];
  return { date, timezone, total_sleep_minutes: total, sessions, baseline: { days: baseline.length, average_sleep_minutes: average },
    assessment: { status: assessment[0], message: assessment[1] } };
}

export function buildFocusBlocks(date: string, timezone: string, events: EventRow[]) {
  const segments = events.filter((event) => ["app.foreground", "app.heartbeat"].includes(event.type) && event.data.is_afk !== true)
    .map((event) => ({ event, minutes: durationMinutes(event) })).filter(({ minutes }) => minutes > 0);
  const focus = segments.filter(({ event }) => event.data.category === "coding").map(({ event, minutes }) => ({
    start_at: event.start_at.toISOString(), end_at: (event.end_at ?? new Date(event.start_at.getTime() + minutes * 60_000)).toISOString(),
    duration_minutes: minutes, app_name: text(event.data.app_name) || "Unknown",
  })).sort((a, b) => a.start_at.localeCompare(b.start_at));
  const blocks: Array<{ start_at: string; end_at: string; duration_minutes: number; app_names: string[] }> = [];
  for (const segment of focus) {
    const previous = blocks.at(-1);
    const gap = previous ? (Date.parse(segment.start_at) - Date.parse(previous.end_at)) / 60_000 : Infinity;
    if (previous && gap >= 0 && gap <= 5) {
      if (segment.end_at > previous.end_at) previous.end_at = segment.end_at;
      previous.duration_minutes += segment.duration_minutes;
      if (!previous.app_names.includes(segment.app_name)) previous.app_names.push(segment.app_name);
    } else blocks.push({ start_at: segment.start_at, end_at: segment.end_at, duration_minutes: segment.duration_minutes, app_names: [segment.app_name] });
  }
  const total = blocks.reduce((sum, block) => sum + block.duration_minutes, 0);
  const active = segments.reduce((sum, segment) => sum + segment.minutes, 0);
  return { date, timezone, total_focus_minutes: total, active_screen_minutes: active,
    focus_ratio: active ? round(total / active) : null, longest_focus_block_minutes: Math.max(0, ...blocks.map((block) => block.duration_minutes)),
    blocks: blocks.sort((a, b) => b.duration_minutes - a.duration_minutes).slice(0, 10) };
}

function categorize(merchant: string, product: string): string {
  const value = `${merchant} ${product}`.toLowerCase();
  const rules: Array<[string, string[]]> = [
    ["food", ["restaurant", "coffee", "cafe", "food", "餐", "咖啡", "美团"]],
    ["transport", ["taxi", "metro", "rail", "滴滴", "地铁", "铁路"]],
    ["shopping", ["shop", "mall", "淘宝", "京东", "商场"]],
    ["utilities", ["electric", "water", "mobile", "电费", "水费", "话费"]],
  ];
  return rules.find(([, hints]) => hints.some((hint) => value.includes(hint)))?.[0] ?? "uncategorized";
}

function text(value: unknown): string { return typeof value === "string" && value.trim() ? value.trim() : ""; }
function money(value: number): number { return Math.round(value * 100) / 100; }
function round(value: number): number { return Math.round(value * 100) / 100; }
