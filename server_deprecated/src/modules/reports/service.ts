import type {
  CurrentContext,
  DaySummary,
  FocusBlocksReport,
  HealthSummary,
  LifestyleAnomalyReport,
  ScreenTimeSummary,
  SleepReport,
  SpendingSummary,
  WeeklySummary,
} from "@ai-life/shared";
import { buildLifestyleAnomalyReport } from "./anomalies";
import { allDeviceStates, eventsInRange, healthEventsInRange, type DeviceStateRow, type EventRow } from "../../db";
import { addUtcDays, datesEndingOn, zonedDayRange } from "../../shared/date-utils";
import { buildFocusBlocksReport, buildScreenSummary, buildSleepReport } from "./derived-reports";
import { spendingCategory } from "./spending-categories";
import { parseJsonString } from "../../shared/validation";

function summarizeScreen(events: EventRow[], rangeStart: Date, rangeEnd: Date): DaySummary["screen"] {
  return buildScreenSummary(events, rangeStart, rangeEnd);
}

function summarizeHealth(events: EventRow[]): DaySummary["health"] {
  let steps = 0;
  let sleepMinutes = 0;
  const heartRates: number[] = [];

  for (const event of events) {
    if (event.type === "health.steps" && typeof event.value === "number") {
      steps += event.value;
    } else if (event.type === "health.sleep") {
      if (event.duration_ms) sleepMinutes += Math.round(event.duration_ms / 60000);
      else if (typeof event.value === "number") sleepMinutes += event.value;
    } else if (event.type === "health.heart_rate" && typeof event.value === "number") {
      heartRates.push(event.value);
    }
  }

  return {
    steps: Math.round(steps),
    sleep_minutes: sleepMinutes,
    avg_heart_rate:
      heartRates.length > 0 ? Math.round(heartRates.reduce((sum, value) => sum + value, 0) / heartRates.length) : null,
  };
}

function roundMoney(value: number): number {
  return Math.round(value * 100) / 100;
}

function summarizeSpending(events: EventRow[], rangeStart: Date, rangeEnd: Date): SpendingSummary {
  const categoryTotals = new Map<string, number>();
  const merchantTotals = new Map<string, number>();
  let totalExpense = 0;
  let totalIncome = 0;
  let transactionCount = 0;
  let currency = "CNY";

  for (const event of events) {
    if (event.type !== "payment.transaction" || typeof event.value !== "number") continue;
    const timestamp = new Date(event.start_at).getTime();
    if (timestamp < rangeStart.getTime() || timestamp >= rangeEnd.getTime()) continue;

    const data = parseJsonString(event.data_json);
    const amount = event.value;
    const absAmount = Math.abs(amount);
    const direction = typeof data.direction === "string" ? data.direction : amount < 0 ? "expense" : "income";
    currency = typeof event.unit === "string" && event.unit ? event.unit : currency;
    transactionCount++;

    if (direction === "income" || amount > 0) {
      totalIncome += absAmount;
    } else {
      totalExpense += absAmount;
      const merchant = typeof data.merchant === "string" && data.merchant ? data.merchant : "unknown";
      const product = typeof data.product === "string" && data.product ? data.product : "";
      const category = spendingCategory(merchant, product, data.category);
      categoryTotals.set(category, (categoryTotals.get(category) ?? 0) + absAmount);
      merchantTotals.set(merchant, (merchantTotals.get(merchant) ?? 0) + absAmount);
    }
  }

  return {
    start: rangeStart.toISOString(),
    end: rangeEnd.toISOString(),
    total_expense: roundMoney(totalExpense),
    total_income: roundMoney(totalIncome),
    net: roundMoney(totalIncome - totalExpense),
    currency,
    transaction_count: transactionCount,
    top_categories: [...categoryTotals.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 8)
      .map(([category, amount]) => ({ category, amount: roundMoney(amount) })),
    top_merchants: [...merchantTotals.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 8)
      .map(([merchant, amount]) => ({ merchant, amount: roundMoney(amount) })),
  };
}

export function currentContext(userId: string): CurrentContext {
  const now = new Date();
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);

  const devices = (allDeviceStates.all() as DeviceStateRow[]).map((device) => {
    const data = parseJsonString(device.current_data_json);
    const lastSeenMinutesAgo = Math.max(0, Math.round((now.getTime() - new Date(device.last_seen_at).getTime()) / 60000));
    return {
      device_id: device.device_id,
      device_name: device.device_name,
      platform: device.platform,
      online: device.is_online === 1 && lastSeenMinutesAgo <= 2,
      current_type: device.current_type,
      current_app: typeof data.app_name === "string" ? data.app_name : undefined,
      current_category: typeof data.category === "string" ? data.category : undefined,
      is_afk: data.is_afk === true,
      last_seen_minutes_ago: lastSeenMinutesAgo,
    };
  });

  const screenEvents = eventsInRange.all(now.toISOString(), start.toISOString()) as EventRow[];
  const healthEvents = healthEventsInRange.all(now.toISOString(), start.toISOString()) as EventRow[];
  const screen = summarizeScreen(screenEvents, start, now);
  const health = summarizeHealth(healthEvents);
  const spending = summarizeSpending(screenEvents, start, now);

  return {
    server_time: now.toISOString(),
    user_id: userId,
    devices,
    today: {
      ...health,
      active_screen_minutes: screen.active_screen_minutes,
      focus_minutes: screen.focus_minutes,
      spending_total: spending.total_expense,
      spending_currency: spending.currency,
      top_apps: screen.top_apps.slice(0, 5),
    },
  };
}

function dateRange(date: string, timezone: string): { start: Date; end: Date } | null {
  return zonedDayRange(date, timezone);
}

export function daySummary(date: string, timezone: string): DaySummary | null {
  const range = dateRange(date, timezone);
  if (!range) return null;

  const screenEvents = eventsInRange.all(range.end.toISOString(), range.start.toISOString()) as EventRow[];
  const healthEvents = healthEventsInRange.all(range.end.toISOString(), range.start.toISOString()) as EventRow[];
  return {
    date,
    timezone,
    health: summarizeHealth(healthEvents),
    screen: summarizeScreen(screenEvents, range.start, range.end),
    spending: summarizeSpending(screenEvents, range.start, range.end),
  };
}

export function weekSummary(endDate: string, timezone: string): WeeklySummary | null {
  const dates = datesEndingOn(endDate, 7);
  if (!dates) return null;

  const startDate = dates[0];
  const finalDate = dates[dates.length - 1];
  if (!startDate || !finalDate) return null;

  const startRange = dateRange(startDate, timezone);
  const endRange = dateRange(finalDate, timezone);
  if (!startRange || !endRange) return null;

  const days: DaySummary[] = [];
  for (const date of dates) {
    const summary = daySummary(date, timezone);
    if (!summary) return null;
    days.push(summary);
  }

  const screenEvents = eventsInRange.all(endRange.end.toISOString(), startRange.start.toISOString()) as EventRow[];
  const healthEvents = healthEventsInRange.all(endRange.end.toISOString(), startRange.start.toISOString()) as EventRow[];
  const health = summarizeHealth(healthEvents);
  const screen = summarizeScreen(screenEvents, startRange.start, endRange.end);

  return {
    start_date: startDate,
    end_date: finalDate,
    timezone,
    health: {
      steps: health.steps,
      average_daily_steps: Math.round(health.steps / days.length),
      sleep_minutes: health.sleep_minutes,
      avg_heart_rate: health.avg_heart_rate,
    },
    screen,
    spending: summarizeSpending(screenEvents, startRange.start, endRange.end),
    days,
  };
}

export function lifestyleAnomalies(date: string, timezone: string): LifestyleAnomalyReport | null {
  const today = daySummary(date, timezone);
  const baselineEndDate = addUtcDays(date, -1);
  const baselineDates = baselineEndDate ? datesEndingOn(baselineEndDate, 7) : null;
  if (!today || !baselineDates) return null;

  const baselineDays: DaySummary[] = [];
  for (const baselineDate of baselineDates) {
    const summary = daySummary(baselineDate, timezone);
    if (!summary) return null;
    baselineDays.push(summary);
  }

  return buildLifestyleAnomalyReport(date, timezone, today, baselineDays);
}

function baselineDaySummaries(date: string, timezone: string): DaySummary[] | null {
  const baselineEndDate = addUtcDays(date, -1);
  const baselineDates = baselineEndDate ? datesEndingOn(baselineEndDate, 7) : null;
  if (!baselineDates) return null;

  const baselineDays: DaySummary[] = [];
  for (const baselineDate of baselineDates) {
    const summary = daySummary(baselineDate, timezone);
    if (!summary) return null;
    baselineDays.push(summary);
  }
  return baselineDays;
}

export function sleepReport(date: string, timezone: string): SleepReport | null {
  const range = dateRange(date, timezone);
  const baselineDays = baselineDaySummaries(date, timezone);
  if (!range || !baselineDays) return null;

  const events = healthEventsInRange.all(range.end.toISOString(), range.start.toISOString()) as EventRow[];
  return buildSleepReport(date, timezone, events, baselineDays);
}

export function focusBlocksReport(date: string, timezone: string): FocusBlocksReport | null {
  const range = dateRange(date, timezone);
  if (!range) return null;

  const events = eventsInRange.all(range.end.toISOString(), range.start.toISOString()) as EventRow[];
  return buildFocusBlocksReport(date, timezone, events);
}

export function spendingSummary(start: Date, end: Date): SpendingSummary {
  const events = eventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  return summarizeSpending(events, start, end);
}

export function healthSummary(start: Date, end: Date): HealthSummary {
  const events = healthEventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  return {
    start: start.toISOString(),
    end: end.toISOString(),
    ...summarizeHealth(events),
  };
}

export function screenTimeSummary(start: Date, end: Date): ScreenTimeSummary {
  const events = eventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  return {
    start: start.toISOString(),
    end: end.toISOString(),
    ...summarizeScreen(events, start, end),
  };
}

export function parseDateRange(url: URL): { start: Date; end: Date } | null {
  const startParam = url.searchParams.get("start");
  const endParam = url.searchParams.get("end");
  if (!startParam || !endParam) return null;

  const start = new Date(startParam);
  const end = new Date(endParam);
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime()) || end <= start) {
    return null;
  }
  return { start, end };
}
