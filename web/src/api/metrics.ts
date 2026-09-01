import { apiGet } from "./client";
import type { UsageDayReport, UsageWeekReport } from "../generated/contract-models";

/** Day usage metrics; boundaries resolve in the request or Owner report timezone. */
export function fetchUsageDayReport(date: string, timezone?: string): Promise<UsageDayReport> {
  return apiGet<UsageDayReport>("/api/v1/metrics/usage/day", timezone ? { date, timezone } : { date });
}

/** Week usage metrics for the Monday-start week containing the requested local day. */
export function fetchUsageWeekReport(date: string, timezone?: string): Promise<UsageWeekReport> {
  return apiGet<UsageWeekReport>("/api/v1/metrics/usage/week", timezone ? { date, timezone } : { date });
}
