import mongoose from "mongoose";
import type { CredentialScope } from "../generated/contract-models.js";
import type { NextFunction, Request, Response } from "express";
import type { Env } from "../config/env.js";
import {
  resolveBearerCredential,
  touchCredentialLastUsed,
} from "../modules/credentials/service.js";
import { readSessionToken, resolveOwnerSession } from "../modules/owner/service.js";
import { recordAuditLog } from "../shared/audit.js";
import { sendError } from "../shared/errors.js";
import { rateLimitDecision, sendRateLimited } from "./rate-limit.js";

function bearer(req: Request): string | null {
  return req.get("authorization")?.match(/^Bearer\s+(.+)$/i)?.[1] ?? null;
}

export function ownerAuth() {
  return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    if (mongoose.connection.readyState !== 1) {
      sendError(res, 503, "service_unavailable", "Authentication is unavailable because the database is not connected.");
      return;
    }
    const session = await resolveOwnerSession(readSessionToken(req));
    if (!session) {
      sendError(res, 401, "unauthorized", "Owner authentication is required.");
      return;
    }
    res.locals.owner = session;
    next();
  };
}

/**
 * Authenticates a Device or Query Token bearer credential and enforces the
 * requested scope. `scope` demands one exact scope; `anyScope` accepts when
 * the credential holds at least one of the listed scopes (finer per-item
 * boundaries stay with the endpoint). Denials record a `credential.deny`
 * audit entry; successful authentications record `credential.use` and
 * throttle-update last_used_at.
 */
export function credentialBearerAuth(env: Env, options: { scope?: CredentialScope; anyScope?: CredentialScope[] } = {}) {
  return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    if (mongoose.connection.readyState !== 1) {
      sendError(res, 503, "service_unavailable", "Authentication is unavailable because the database is not connected.");
      return;
    }
    const token = bearer(req);
    if (!token) {
      sendError(res, 401, "unauthorized", "A bearer credential is required.");
      return;
    }
    const resolved = await resolveBearerCredential(token);
    if ("denial" in resolved) {
      await recordAuditLog({
        userId: resolved.userId ?? env.DEFAULT_USER_ID,
        actorType: resolved.kind === "query_token" ? "query" : "system",
        action: "credential.deny",
        status: "error",
        details: { reason: resolved.denial, credential_prefix: resolved.prefix, path: req.path },
      });
      const messages: Record<string, string> = {
        unknown_token: "The bearer credential is unknown.",
        token_revoked: "The bearer credential has been revoked.",
        token_expired: "The bearer credential has expired.",
      };
      sendError(res, 401, resolved.denial, messages[resolved.denial] ?? "The bearer credential was rejected.");
      return;
    }
    // Rate limiting is per credential and applies to every use, so a
    // hammering credential is throttled even when its requests would be
    // denied for another reason.
    const decision = rateLimitDecision(resolved.id, env.RATE_LIMIT_PER_MINUTE);
    if (!decision.allowed) {
      await recordAuditLog({
        userId: resolved.userId,
        actorType: resolved.kind === "device_token" ? "device" : "query",
        actorId: resolved.id,
        action: "credential.deny",
        status: "error",
        details: {
          reason: "rate_limited",
          credential_id: resolved.id,
          path: req.path,
        },
      });
      sendRateLimited(res, decision.retryAfterSeconds);
      return;
    }
    const requiredScopes: string[] = options.anyScope ?? (options.scope ? [options.scope] : []);
    if (requiredScopes.length > 0 && !requiredScopes.some((scope) => resolved.scopes.includes(scope))) {
      await recordAuditLog({
        userId: resolved.userId,
        actorType: resolved.kind === "device_token" ? "device" : "query",
        actorId: resolved.id,
        action: "credential.deny",
        status: "error",
        details: {
          reason: "insufficient_scope",
          required_scope: requiredScopes.join("|"),
          credential_id: resolved.id,
          path: req.path,
        },
      });
      sendError(res, 403, "insufficient_scope", "The credential lacks the required scope.");
      return;
    }
    res.locals.credential = resolved;
    await touchCredentialLastUsed(resolved.id);
    await recordAuditLog({
      userId: resolved.userId,
      actorType: resolved.kind === "device_token" ? "device" : "query",
      actorId: resolved.id,
      action: "credential.use",
      details: { path: req.path, method: req.method },
    });
    next();
  };
}

/**
 * Guard for endpoints that accept an Owner session or a Query Token: a bearer
 * credential takes precedence, otherwise the browser session is used.
 */
export function sessionOrCredentialAuth(env: Env, options: { scope?: CredentialScope; anyScope?: CredentialScope[] } = {}) {
  const bearerAuth = credentialBearerAuth(env, options);
  const sessionAuth = ownerAuth();
  return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    if (bearer(req)) {
      await bearerAuth(req, res, next);
      return;
    }
    await sessionAuth(req, res, next);
  };
}
