import { Router } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { credentialBearerAuth, ownerAuth } from "../../middleware/auth.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import type { Clock } from "../../shared/clock.js";
import { AppError } from "../../shared/errors.js";
import { listSyncDiagnostics, recordSyncDiagnostics } from "./service.js";

/**
 * A snapshot may only carry a stable lower-snake-case error code and a bounded
 * safe summary: the same code shape the server itself answers with. Free-form
 * error text has no field to travel in.
 */
/** Shared shape for the snapshot's optional and required UTC instants. */
const isoInstant = z.string().refine((value) => !Number.isNaN(new Date(value).getTime()), {
  message: "must be an ISO 8601 instant.",
});

const syncDiagnosticErrorSchema = z.strictObject({
  code: z.string().max(64).regex(/^[a-z][a-z0-9_]*$/, "code must be a stable snake_case error code."),
  message: z.string().min(1).max(300),
  occurred_at: isoInstant,
});

const syncDiagnosticsReportSchema = z.strictObject({
  platform: z.enum(["windows", "android"]),
  device_name: z.string().min(1).max(100).optional(),
  collected_at: isoInstant.optional(),
  last_successful_upload_at: isoInstant.optional(),
  oldest_pending_at: isoInstant.optional(),
  pending_count: z.number().int().min(0).max(1_000_000),
  permanent_failure_count: z.number().int().min(0).max(1_000_000),
  recent_errors: z.array(syncDiagnosticErrorSchema).max(10),
});

/**
 * Sync-diagnostics routes sit in front of the global Owner guard: pushing is
 * a Device-Token-only capability, while reading snapshots is
 * Owner-session-only. Diagnostics are operational visibility; they never
 * touch the events collection, rollups, or metrics.
 */
export function diagnosticsRouter(env: Env, clock: Clock): Router {
  const router = Router();

  router.post("/diagnostics/sync", credentialBearerAuth(env, { scope: "events:write" }), async (req, res) => {
    const credential = res.locals.credential as CredentialAuthContext;
    if (credential.kind !== "device_token") {
      throw new AppError(403, "Only device tokens may push sync diagnostics.", "insufficient_scope");
    }
    const parsed = syncDiagnosticsReportSchema.safeParse(req.body);
    if (!parsed.success) {
      const issue = parsed.error.issues.at(0);
      const detail = issue ? ` ${issue.path.join(".")}: ${issue.message}` : "";
      throw new AppError(400, "The diagnostics body is not a valid sync-state snapshot." + detail, "invalid_request");
    }
    const {
      platform, device_name: deviceName, collected_at: collectedAt,
      last_successful_upload_at: lastSuccessfulUploadAt, oldest_pending_at: oldestPendingAt,
      pending_count: pendingCount, permanent_failure_count: permanentFailureCount, recent_errors: recentErrors,
    } = parsed.data;
    await recordSyncDiagnostics(credential, {
      platform,
      pending_count: pendingCount,
      permanent_failure_count: permanentFailureCount,
      recent_errors: recentErrors,
      ...(deviceName !== undefined ? { device_name: deviceName } : {}),
      ...(collectedAt !== undefined ? { collected_at: collectedAt } : {}),
      ...(lastSuccessfulUploadAt !== undefined ? { last_successful_upload_at: lastSuccessfulUploadAt } : {}),
      ...(oldestPendingAt !== undefined ? { oldest_pending_at: oldestPendingAt } : {}),
    }, clock);
    res.status(204).end();
  });

  router.get("/diagnostics/sync", ownerAuth(), async (req, res) => {
    res.json(await listSyncDiagnostics(env.DEFAULT_USER_ID, clock));
  });

  return router;
}
