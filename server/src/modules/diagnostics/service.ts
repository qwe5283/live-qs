import type {
  Platform,
  SyncDiagnostic,
  SyncDiagnosticError,
  SyncDiagnosticList,
  SyncDiagnosticsReport,
} from "../../generated/contract-models.js";
import { SyncDiagnosticModel } from "../../db/models.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import type { Clock } from "../../shared/clock.js";
import { AppError } from "../../shared/errors.js";

/** Capture instants more than this far in the future are rejected, like heartbeats. */
const MAX_FUTURE_SKEW_MS = 300_000;

interface SyncDiagnosticRow {
  device_key: string;
  platform: string;
  device_name: string | null;
  reported_at: Date;
  collected_at: Date;
  last_successful_upload_at: Date | null;
  oldest_pending_at: Date | null;
  pending_count: number;
  permanent_failure_count: number;
  recent_errors: unknown;
}

/**
 * Stores one device's sync-state snapshot, replacing any previous one. The
 * snapshot is a best-effort cadence report, never queued: last write wins,
 * and the server receive time decides the age the Owner sees.
 */
export async function recordSyncDiagnostics(credential: CredentialAuthContext, input: SyncDiagnosticsReport, clock: Clock): Promise<void> {
  const now = clock.now();
  await SyncDiagnosticModel.updateOne(
    { device_key: credential.id },
    {
      $set: {
        user_id: credential.userId,
        platform: input.platform,
        device_name: input.device_name ?? null,
        reported_at: now,
        collected_at: instantOrNull(input.collected_at, "collected_at", now),
        last_successful_upload_at: instantOrNull(input.last_successful_upload_at, "last_successful_upload_at", now),
        oldest_pending_at: instantOrNull(input.oldest_pending_at, "oldest_pending_at", now),
        pending_count: input.pending_count,
        permanent_failure_count: input.permanent_failure_count,
        recent_errors: input.recent_errors,
      },
    },
    { upsert: true },
  );
}

/**
 * Lists every device snapshot of the Owner, oldest to newest by report time.
 * Ages derive from the server receive instant at read time, so a device that
 * stops reporting grows stale instead of disappearing.
 */
export async function listSyncDiagnostics(userId: string, clock: Clock): Promise<SyncDiagnosticList> {
  const now = clock.now();
  const rows = await SyncDiagnosticModel.find({ user_id: userId }).sort({ reported_at: 1 }).lean<SyncDiagnosticRow[]>();
  return {
    server_time: now.toISOString(),
    devices: rows.map((row) => toSyncDiagnostic(row, now)),
  };
}

function assertNotFutureSkewed(instant: string, field: string, now: Date): void {
  if (new Date(instant).getTime() > now.getTime() + MAX_FUTURE_SKEW_MS) {
    throw new AppError(400, `${field} must not be more than five minutes in the future.`, "invalid_request");
  }
}

/** Absent and explicit-null both mean "never"; a present instant must not be future-skewed. */
function instantOrNull(instant: string | null | undefined, field: string, now: Date): Date | null {
  if (instant === null || instant === undefined) return null;
  assertNotFutureSkewed(instant, field, now);
  return new Date(instant);
}

function toSyncDiagnostic(row: SyncDiagnosticRow, now: Date): SyncDiagnostic {
  return {
    device_id: row.device_key,
    device_name: row.device_name ?? null,
    platform: row.platform as Platform,
    reported_at: row.reported_at.toISOString(),
    age_seconds: Math.max(0, Math.floor((now.getTime() - row.reported_at.getTime()) / 1000)),
    collected_at: row.collected_at?.toISOString() ?? null,
    last_successful_upload_at: row.last_successful_upload_at?.toISOString() ?? null,
    oldest_pending_at: row.oldest_pending_at?.toISOString() ?? null,
    pending_count: row.pending_count,
    permanent_failure_count: row.permanent_failure_count,
    recent_errors: row.recent_errors as SyncDiagnosticError[],
  };
}
