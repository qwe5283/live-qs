import { Router } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { credentialBearerAuth, sessionOrCredentialAuth } from "../../middleware/auth.js";
import { recordQueryAudit } from "../../shared/audit.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import type { Clock } from "../../shared/clock.js";
import { AppError } from "../../shared/errors.js";
import { listDeviceStatuses, recordHeartbeat } from "./service.js";

/**
 * The contract forbids full executable paths in `application_id`; it must stay
 * an opaque executable or package name. Path separators and drive-letter
 * prefixes are the rejected shapes (same rule as the event payload registry).
 */
function isOpaqueApplicationId(value: string): boolean {
  return !value.includes("/") && !value.includes("\\") && !/^[a-zA-Z]:/.test(value);
}

const heartbeatActivitySchema = z.strictObject({
  application_id: z.string().min(1).max(200).refine(isOpaqueApplicationId, {
    message: "application_id must be an executable or package name, never a path.",
  }).optional(),
  application_label: z.string().min(1).max(200).optional(),
  is_afk: z.boolean(),
});

const heartbeatRequestSchema = z.strictObject({
  platform: z.enum(["windows", "android"]),
  device_name: z.string().min(1).max(100).optional(),
  captured_at: z.string().refine((value) => !Number.isNaN(new Date(value).getTime()), {
    message: "captured_at must be an ISO 8601 instant.",
  }),
  activity: heartbeatActivitySchema,
});

/**
 * Heartbeat routes sit in front of the global Owner guard: posting is a
 * Device-Token-only capability, while reading statuses is an Owner-session or
 * context:read query-token capability (the current-context read of the
 * read-only AI Skill). Heartbeats are ephemeral projections; they never touch
 * the events collection, rollups, or metrics.
 */
export function heartbeatsRouter(env: Env, clock: Clock): Router {
  const router = Router();

  router.post("/heartbeats", credentialBearerAuth(env, { scope: "events:write" }), async (req, res) => {
    const credential = res.locals.credential as CredentialAuthContext;
    if (credential.kind !== "device_token") {
      throw new AppError(403, "Only device tokens may send heartbeats.", "insufficient_scope");
    }
    const parsed = heartbeatRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      const issue = parsed.error.issues.at(0);
      const detail = issue ? ` ${issue.path.join(".")}: ${issue.message}` : "";
      throw new AppError(400, "The heartbeat body is not a valid heartbeat request." + detail, "invalid_request");
    }
    const { application_id: applicationId, application_label: applicationLabel, is_afk: isAfk } = parsed.data.activity;
    await recordHeartbeat(
      credential,
      {
        platform: parsed.data.platform,
        deviceName: parsed.data.device_name ?? null,
        capturedAt: new Date(parsed.data.captured_at),
        activity: {
          ...(applicationId !== undefined ? { application_id: applicationId } : {}),
          ...(applicationLabel !== undefined ? { application_label: applicationLabel } : {}),
          is_afk: isAfk,
        },
      },
      clock,
    );
    res.status(204).end();
  });

  router.get("/status", sessionOrCredentialAuth(env, { scope: "context:read" }), async (req, res) => {
    const statuses = await listDeviceStatuses(env.DEFAULT_USER_ID, clock);
    await recordQueryAudit({
      userId: env.DEFAULT_USER_ID,
      credential: res.locals.credential as CredentialAuthContext | undefined,
      path: req.path,
      dataTypes: ["device_status"],
      resultCount: statuses.devices.length,
    });
    res.json(statuses);
  });

  return router;
}
