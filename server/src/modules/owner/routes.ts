import { Router } from "express";
import type { Response } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { ownerAuth } from "../../middleware/auth.js";
import { recordAuditLog } from "../../shared/audit.js";
import { AppError } from "../../shared/errors.js";
import { getReportTimezone, setReportTimezone } from "./settings.js";
import {
  SESSION_COOKIE_NAME,
  createOwnerCredential,
  createOwnerSession,
  isOwnerInitialized,
  readSessionToken,
  revokeOwnerSession,
  verifyOwnerPassword,
} from "./service.js";

const passwordRequestSchema = z.strictObject({
  password: z.string().min(8, "Password must contain at least 8 characters.").max(256),
});

const settingsUpdateSchema = z.strictObject({
  report_timezone: z.string().min(1),
});

export function ownerRouter(env: Env): Router {
  const router = Router();
  const userId = env.DEFAULT_USER_ID;

  router.get("/status", async (_req, res) => {
    res.json({ initialized: await isOwnerInitialized(userId) });
  });

  router.post("/setup", async (req, res) => {
    const password = parsePassword(req.body);
    await createOwnerCredential(userId, password);
    await establishSession(res, env, userId);
    res.status(204).end();
  });

  router.post("/login", async (req, res) => {
    const password = parsePassword(req.body);
    if (!(await verifyOwnerPassword(userId, password))) {
      throw new AppError(401, "The password is incorrect.", "invalid_credentials");
    }
    await establishSession(res, env, userId);
    res.status(204).end();
  });

  router.post("/logout", ownerAuth(), async (req, res) => {
    await revokeOwnerSession(readSessionToken(req));
    clearSessionCookie(res, env);
    res.status(204).end();
  });

  router.get("/session", ownerAuth(), (_req, res) => {
    res.json({ authenticated: true });
  });

  router.get("/settings", ownerAuth(), async (_req, res) => {
    res.json({ report_timezone: await getReportTimezone(userId) });
  });

  router.post("/settings", ownerAuth(), async (req, res) => {
    const parsed = settingsUpdateSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The settings body must contain a report_timezone string.", "invalid_request");
    }
    const reportTimezone = await setReportTimezone(userId, parsed.data.report_timezone);
    await recordAuditLog({
      userId,
      actorType: "user",
      action: "owner.settings.update",
      details: { report_timezone: reportTimezone },
    });
    res.json({ report_timezone: reportTimezone });
  });

  return router;
}

function parsePassword(body: unknown): string {
  const result = passwordRequestSchema.safeParse(body);
  if (!result.success) {
    throw new AppError(400, "The password must contain between 8 and 256 characters.", "invalid_password");
  }
  return result.data.password;
}

async function establishSession(res: Response, env: Env, userId: string): Promise<void> {
  const token = await createOwnerSession(userId, env.SESSION_TTL_HOURS);
  res.cookie(SESSION_COOKIE_NAME, token, {
    httpOnly: true,
    sameSite: "lax",
    secure: env.COOKIE_SECURE,
    path: "/",
    maxAge: env.SESSION_TTL_HOURS * 3_600_000,
  });
}

function clearSessionCookie(res: Response, env: Env): void {
  res.clearCookie(SESSION_COOKIE_NAME, {
    httpOnly: true,
    sameSite: "lax",
    secure: env.COOKIE_SECURE,
    path: "/",
  });
}
