import { Router } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { ownerAuth } from "../../middleware/auth.js";
import { AppError } from "../../shared/errors.js";
import { readPolicyDocument, updatePolicyDocument } from "./service.js";

const updateRequestSchema = z.strictObject({
  entries: z.array(z.strictObject({
    metric: z.string().min(1),
    priority: z.array(z.string()),
  })),
});

/**
 * Owner-only management of the versioned source-priority policy. Query Tokens
 * see the applied version in every query context; the document itself and the
 * right to change it stay with the Owner, and every change is audited with
 * its affected ranges and result counts.
 */
export function sourcePolicyRouter(env: Env): Router {
  const router = Router();
  const auth = ownerAuth();

  router.get("/", auth, async (_req, res) => {
    res.json(await readPolicyDocument(env.DEFAULT_USER_ID));
  });

  router.put("/", auth, async (req, res) => {
    const parsed = updateRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The request body must contain an entries array of { metric, priority } objects.", "invalid_request");
    }
    res.json(await updatePolicyDocument(env.DEFAULT_USER_ID, parsed.data.entries));
  });

  return router;
}
