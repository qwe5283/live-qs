import { apiGet } from "./client";
import type { EventPage } from "../generated/contract-models";

/** Reads the contract event timeline; the browser timezone is the report timezone until an Owner setting exists. */
export function fetchEvents(start: Date, end: Date, params?: { cursor?: string }): Promise<EventPage> {
  const search: Record<string, string> = {
    from: start.toISOString(),
    to: end.toISOString(),
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
    page_size: "200",
  };
  if (params?.cursor) search.cursor = params.cursor;
  return apiGet<EventPage>("/api/v1/events", search);
}
