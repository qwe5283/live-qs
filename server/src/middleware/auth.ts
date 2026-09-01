import { timingSafeEqual } from "node:crypto";
import mongoose from "mongoose";
import type { NextFunction, Request, Response } from "express";
import type { Env } from "../config/env.js";
import type { DeviceIdentity, Platform } from "../types/contracts.js";
import { readSessionToken, resolveOwnerSession } from "../modules/owner/service.js";
import { sendError } from "../shared/errors.js";

function bearer(req: Request): string | null {
  return req.get("authorization")?.match(/^Bearer\s+(.+)$/i)?.[1] ?? null;
}

function equalToken(actual: string | null, expected: string): boolean {
  if (!actual) return false;
  const left = Buffer.from(actual);
  const right = Buffer.from(expected);
  return left.length === right.length && timingSafeEqual(left, right);
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

export function deviceAuth(env: Env) {
  const platforms = new Set<Platform>(["windows", "android", "macos"]);
  const identities = new Map<string, DeviceIdentity>();
  for (const value of Object.values(env.deviceTokens)) {
    const parts = value.split(":");
    const token = parts.shift() ?? "";
    const deviceId = parts.shift() ?? "";
    const platform = parts.pop() as Platform | undefined;
    const deviceName = parts.join(":");
    if (token && deviceId && deviceName && platform && platforms.has(platform)) {
      identities.set(token, { userId: env.DEFAULT_USER_ID, deviceId, deviceName, platform });
    }
  }

  return (req: Request, res: Response, next: NextFunction): void => {
    const identity = identities.get(bearer(req) ?? "");
    if (!identity) {
      sendError(res, 401, "unauthorized", "A valid device token is required.");
      return;
    }
    res.locals.device = identity;
    next();
  };
}
