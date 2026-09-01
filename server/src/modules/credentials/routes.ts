import { Router } from "express";
import { z } from "zod";
import type { Env } from "../../config/env.js";
import { ownerAuth } from "../../middleware/auth.js";
import { recordAuditLog } from "../../shared/audit.js";
import { AppError } from "../../shared/errors.js";
import { createCredential, listCredentials, revokeCredential } from "./service.js";

const EVENT_TYPE_PATTERN = /^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$/;

const createRequestSchema = z.strictObject({
  kind: z.enum(["device_token", "query_token"]),
  name: z.string().min(1).max(100),
  scopes: z.array(z.enum(["events:write", "events:read"])).min(1).max(8),
  allowed_event_types: z.array(z.string().regex(EVENT_TYPE_PATTERN)).max(64).default([]),
  privacy_ceiling: z.enum(["normal", "sensitive", "private"]).default("normal"),
  expires_at: z.union([z.string(), z.null()]).default(null),
});

export function credentialsRouter(_env: Env): Router {
  const router = Router();
  router.use(ownerAuth());

  router.post("/", async (req, res) => {
    const parsed = createRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The credential request is missing fields or has invalid values.", "invalid_credential");
    }
    const expiresAt = parsed.data.expires_at;
    const created = await createCredential(res.locals.owner.userId, {
      kind: parsed.data.kind,
      name: parsed.data.name,
      scopes: parsed.data.scopes,
      allowed_event_types: parsed.data.allowed_event_types,
      privacy_ceiling: parsed.data.privacy_ceiling,
      expires_at: expiresAt,
    });
    await recordAuditLog({
      userId: res.locals.owner.userId,
      actorId: res.locals.owner.sessionId,
      action: "credential.create",
      details: {
        credential_id: created.credential.credential_id,
        credential_kind: created.credential.kind,
        credential_name: created.credential.name,
        credential_prefix: created.credential.token_prefix,
        scopes: created.credential.scopes,
        privacy_ceiling: created.credential.privacy_ceiling,
        expires_at: created.credential.expires_at,
      },
    });
    res.status(201).json(created);
  });

  router.get("/", async (req, res) => {
    res.json({ credentials: await listCredentials(res.locals.owner.userId) });
  });

  router.post("/:credential_id/revoke", async (req, res) => {
    const credentialId = typeof req.params.credential_id === "string" ? req.params.credential_id : "";
    if (!credentialId) throw new AppError(404, "The credential does not exist.", "not_found");
    const revoked = await revokeCredential(res.locals.owner.userId, credentialId);
    if (!revoked) throw new AppError(404, "The credential does not exist.", "not_found");
    await recordAuditLog({
      userId: res.locals.owner.userId,
      actorId: res.locals.owner.sessionId,
      action: "credential.revoke",
      details: { credential_id: credentialId },
    });
    res.status(204).end();
  });

  return router;
}
