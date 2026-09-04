import { apiGet } from "./client";
import type { EventPage } from "../generated/contract-models";

/**
 * Reads the contract health domain (steps, heart rate, sleep) with their
 * Health Connect data origins. Day boundaries are supplied by the caller and
 * resolve in the Owner report timezone; completeness is reported in the query
 * context so missing coverage is never rendered as zero.
 */
export function fetchHealthEvents(start: Date, end: Date, timezone: string, params?: { cursor?: string }): Promise<EventPage> {
  const search: Record<string, string> = {
    from: start.toISOString(),
    to: end.toISOString(),
    timezone,
    page_size: "200",
  };
  if (params?.cursor) search.cursor = params.cursor;
  return apiGet<EventPage>("/api/v1/health/events", search);
}
