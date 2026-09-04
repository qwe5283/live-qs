import { Router } from "express";
import type { Request } from "express";
import type { Env } from "../../config/env.js";
import { ownerAuth } from "../../middleware/auth.js";
import { AppError } from "../../shared/errors.js";
import { correctEvent } from "./service.js";

/**
 * Owner-session-only auditable corrections. The endpoint lives under the
 * events path because it reinterprets one logical event, but unlike the event
 * reads it accepts no bearer credential at all: Device Tokens and Query
 * Tokens have no cookie and cannot authenticate here, so no credential type
 * can ever perform a correction. Every applied correction is audited with
 * actor, time, changed fields, and the optional reason.
 */
export function correctionsRouter(env: Env): Router {
  const router = Router();
  const auth = ownerAuth();

  router.post("/:eventId/corrections", auth, async (req: Request, res) => {
    const eventId = req.params.eventId;
    if (typeof eventId !== "string" || eventId.length === 0) {
      throw new AppError(400, "The path parameter event_id is required.", "invalid_request");
    }
    res.json(await correctEvent(env.DEFAULT_USER_ID, eventId, req.body));
  });

  return router;
}
