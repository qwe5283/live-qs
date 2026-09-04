import { Router } from "express";
import type { Env } from "../../config/env.js";
import { ownerAuth, sessionOrCredentialAuth } from "../../middleware/auth.js";
import { readRuleSet, updateRuleSet } from "./service.js";

/**
 * Distribution and management of the versioned classification rule set.
 * GET serves the active document to devices holding the device-only
 * rules:read scope (and to the Owner session for management); PUT is the
 * Owner-session control plane. Devices cache the last successful GET result
 * and keep classifying offline, so this endpoint is the only rules traffic a
 * collector needs.
 */
export function classificationRouter(env: Env): Router {
  const router = Router();

  router.get("/ruleset", sessionOrCredentialAuth(env, { scope: "rules:read" }), async (_req, res) => {
    res.json(await readRuleSet(env.DEFAULT_USER_ID));
  });

  router.put("/ruleset", ownerAuth(), async (req, res) => {
    res.json(await updateRuleSet(env.DEFAULT_USER_ID, res.locals.owner.sessionId, req.body));
  });

  return router;
}
