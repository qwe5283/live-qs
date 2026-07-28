import type { DaySummary } from "@ai-life/shared";
import { getDailyRollup, upsertDailyRollup, type DailyRollupRow } from "../../db";
import { datesBetweenInclusive } from "../../shared/date-utils";
import { daySummary } from "./service";

export interface RollupResult {
  date: string;
  timezone: string;
  created: boolean;
  summary: DaySummary;
}

export function persistDayRollup(userId: string, date: string, timezone: string): RollupResult | null {
  const summary = daySummary(date, timezone);
  if (!summary) return null;

  const existing = getDailyRollup.get(userId, date, timezone) as DailyRollupRow | undefined;
  const now = new Date().toISOString();
  upsertDailyRollup.run(userId, date, timezone, JSON.stringify(summary), existing?.created_at ?? now, now);

  return {
    date,
    timezone,
    created: !existing,
    summary,
  };
}

export function backfillDayRollups(
  userId: string,
  startDate: string,
  endDate: string,
  timezone: string,
): { start_date: string; end_date: string; timezone: string; upserted: number; dates: string[] } | null {
  const dates = datesBetweenInclusive(startDate, endDate, 366);
  if (!dates) return null;

  const upsertedDates: string[] = [];
  for (const date of dates) {
    const result = persistDayRollup(userId, date, timezone);
    if (!result) return null;
    upsertedDates.push(date);
  }

  return {
    start_date: startDate,
    end_date: endDate,
    timezone,
    upserted: upsertedDates.length,
    dates: upsertedDates,
  };
}
