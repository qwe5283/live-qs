import { apiGet } from "./client";
import type { EventPage } from "../generated/contract-models";

/**
 * Reads the contract payment domain: structured transaction facts extracted
 * on-device from payment notifications (amount, direction, approved merchant
 * label, category, pending-confirmation flag). Day boundaries are supplied by
 * the caller and resolve in the Owner report timezone; completeness is
 * reported in the query context so missing coverage is never rendered as zero.
 * Notification text never existed anywhere in this data domain.
 */
export function fetchPaymentEvents(start: Date, end: Date, timezone: string, params?: { cursor?: string }): Promise<EventPage> {
  const search: Record<string, string> = {
    from: start.toISOString(),
    to: end.toISOString(),
    timezone,
    page_size: "200",
  };
  if (params?.cursor) search.cursor = params.cursor;
  return apiGet<EventPage>("/api/v1/payment/events", search);
}
