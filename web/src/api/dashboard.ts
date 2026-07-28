import { apiGet, isoRange } from "./client";
import type {
  CurrentContext,
  HealthSummary,
  HealthTimelineResponse,
  UsageAppsResponse,
  UsageSummary,
  UsageTimelineResponse,
} from "./types";

export function fetchCurrent() {
  return apiGet<CurrentContext>("/api/v1/context/current");
}

export function fetchUsageSummary(start: Date, end: Date) {
  return apiGet<UsageSummary>("/api/v1/usage/summary", isoRange(start, end));
}

export function fetchUsageApps(start: Date, end: Date) {
  return apiGet<UsageAppsResponse>("/api/v1/usage/apps", isoRange(start, end));
}

export function fetchUsageTimeline(start: Date, end: Date) {
  return apiGet<UsageTimelineResponse>("/api/v1/usage/timeline", isoRange(start, end));
}

export function fetchHealthSummary(start: Date, end: Date) {
  return apiGet<HealthSummary>("/api/v1/health/summary", isoRange(start, end));
}

export function fetchHealthTimeline(start: Date, end: Date) {
  return apiGet<HealthTimelineResponse>("/api/v1/health/timeline", isoRange(start, end));
}

export function fetchHealthProbe(apiBase: string) {
  return fetch(new URL("/health", `${apiBase.replace(/\/+$/, "")}/`)).then((response) => response.json());
}
