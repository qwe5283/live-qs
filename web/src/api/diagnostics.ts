import { apiGet } from "./client";
import type { SyncDiagnosticList } from "../generated/contract-models";

/**
 * Latest sync-state snapshot of every device that pushed one. Snapshots are
 * collector reports on the sync cadence — counts, timestamps, and
 * stable-code errors only — and never historical events.
 */
export function fetchSyncDiagnostics() {
  return apiGet<SyncDiagnosticList>("/api/v1/diagnostics/sync");
}
