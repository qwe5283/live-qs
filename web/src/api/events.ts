import { apiGet } from "./client";
import type { EventPage } from "../generated/contract-models";

/**
 * Reads the contract event timeline. The timezone is the Owner report timezone
 * resolved by the caller: report boundaries never follow the browser timezone.
 */
export function fetchEvents(start: Date, end: Date, timezone: string, params?: { cursor?: string }): Promise<EventPage> {
  const search: Record<string, string> = {
    from: start.toISOString(),
    to: end.toISOString(),
    timezone,
    page_size: "200",
  };
  if (params?.cursor) search.cursor = params.cursor;
  return apiGet<EventPage>("/api/v1/events", search);
}
