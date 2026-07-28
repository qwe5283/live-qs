import { timingSafeEqual } from "node:crypto";
import type { NextFunction, Request, Response } from "express";
import type { Env } from "../config/env.js";
import type { DeviceIdentity, Platform } from "../types/contracts.js";

function bearer(req: Request): string | null {
  return req.get("authorization")?.match(/^Bearer\s+(.+)$/i)?.[1] ?? null;
}

function equalToken(actual: string | null, expected: string): boolean {
  if (!actual) return false;
  const left = Buffer.from(actual);
  const right = Buffer.from(expected);
  return left.length === right.length && timingSafeEqual(left, right);
}

export function userAuth(env: Env) {
  return (req: Request, res: Response, next: NextFunction): void => {
    if (!equalToken(bearer(req), env.USER_TOKEN)) {
      res.status(401).json({ error: "Unauthorized" });
      return;
    }
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
      res.status(401).json({ error: "Unauthorized" });
      return;
    }
    res.locals.device = identity;
    next();
  };
}
