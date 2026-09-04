import { Router } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { ownerAuth, sessionOrCredentialAuth } from "../../middleware/auth.js";
import { AppError } from "../../shared/errors.js";
import {
  closeTask,
  createTask,
  estimateReclassification,
  readLatestTask,
  readTaskAssignment,
  recordDeviceReport,
} from "./service.js";

const createRequestSchema = z.strictObject({
  // Version plausibility (>= 1 and <= published) is a semantic rule of the
  // service and carries its own stable error code; the schema only checks shape.
  target_rule_set_version: z.number().int().optional(),
  from: z.string().optional(),
  to: z.string().optional(),
});

const deviceReportRequestSchema = z.strictObject({
  platform: z.enum(["windows", "android"]),
  scanned: z.number().int().min(0),
  reclassified: z.number().int().min(0),
  unchanged: z.number().int().min(0),
  failed: z.number().int().min(0),
});

/** Parses an optional UTC bound; malformed or inverted ranges are a 400. */
function parseBound(value: unknown): Date | null {
  if (value === undefined) return null;
  const parsed = new Date(typeof value === "string" ? value : "");
  if (Number.isNaN(parsed.getTime())) {
    throw new AppError(400, "The time range bounds must be ISO 8601 instants.", "invalid_time_range");
  }
  return parsed;
}

function parseRange(query: Record<string, unknown>): { from: Date | null; to: Date | null } {
  const from = parseBound(query.from);
  const to = parseBound(query.to);
  if (from && to && from.getTime() >= to.getTime()) {
    throw new AppError(400, "The time range end must be later than its start.", "invalid_time_range");
  }
  return { from, to };
}

/**
 * Owner control plane and device execution surface of explicit historical
 * reclassification. Management (estimate, start, progress, close) is
 * Owner-session-only; devices use their rules:read scope — the same
 * distribution scope as the rule set, because executing a task means
 * re-executing rules locally — to fetch their assignment and report outcome
 * counts. The server itself never reclassifies anything.
 */
export function reclassificationRouter(env: Env): Router {
  const router = Router();
  const owner = ownerAuth();
  const device = sessionOrCredentialAuth(env, { scope: "rules:read" });

  /** Devices only: a bearer credential identifies the reporting/asking device. */
  function requireDeviceId(res: { locals: { credential?: { id: string; userId: string } } }): { userId: string; deviceId: string } {
    const credential = res.locals.credential;
    if (!credential) {
      throw new AppError(401, "A device credential with the rules:read scope is required.", "unauthorized");
    }
    return { userId: credential.userId, deviceId: credential.id };
  }  router.get("/reclassification/estimate", owner, async (req, res) => {
    const range = parseRange(req.query as Record<string, unknown>);
    res.json(await estimateReclassification(env.DEFAULT_USER_ID, range.from, range.to));
  });

  router.post("/reclassification/tasks", owner, async (req, res) => {
    const parsed = createRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The task request must contain an optional target rule set version and time range.", "invalid_request");
    }
    const range = parseRange({ from: parsed.data.from, to: parsed.data.to });
    const { target_rule_set_version: targetRuleSetVersion } = parsed.data;
    res.json(await createTask(env.DEFAULT_USER_ID, res.locals.owner.sessionId, {
      ...(targetRuleSetVersion === undefined ? {} : { targetRuleSetVersion }),
      from: range.from,
      to: range.to,
    }));
  });

  router.get("/reclassification/tasks/current", owner, async (_req, res) => {
    const status = await readLatestTask(env.DEFAULT_USER_ID);
    if (!status) {
      res.status(204).end();
      return;
    }
    res.json(status);
  });

  router.get("/reclassification/tasks/assignment", device, async (_req, res) => {
    const { userId, deviceId } = requireDeviceId(res);
    const assignment = await readTaskAssignment(userId, deviceId);
    if (!assignment) {
      res.status(204).end();
      return;
    }
    res.json(assignment);
  });

  router.post("/reclassification/tasks/:task_id/device-reports", device, async (req, res) => {
    const { userId, deviceId } = requireDeviceId(res);
    const taskId = typeof req.params.task_id === "string" ? req.params.task_id : "";
    if (!taskId) throw new AppError(404, "The reclassification task does not exist.", "not_found");
    const parsed = deviceReportRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The device report must contain a platform and non-negative outcome counts.", "invalid_request");
    }
    await recordDeviceReport(userId, deviceId, taskId, parsed.data);
    res.status(204).end();
  });

  router.post("/reclassification/tasks/:task_id/close", owner, async (req, res) => {
    const taskId = typeof req.params.task_id === "string" ? req.params.task_id : "";
    if (!taskId) throw new AppError(404, "The reclassification task does not exist.", "not_found");
    res.json(await closeTask(env.DEFAULT_USER_ID, res.locals.owner.sessionId, taskId));
  });

  return router;
}
