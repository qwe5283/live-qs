import type { Response } from "express";
import { sendError } from "../shared/errors.js";

/**
 * Per-credential fixed-window rate limiter for bearer-credential paths
 * (SPEC implementation decision 25: Query Tokens have rate limits; the
 * OpenAPI contract declares 429 with Retry-After on every endpoint).
 *
 * The counter is keyed by credential ID and held in process memory: V1 runs
 * as a single LAN service, so no shared store exists to keep in sync. Owner
 * browser sessions and public endpoints are deliberately not throttled —
 * public-deployment hardening is a later security gate (SPEC out of scope).
 */
const WINDOW_MS = 60_000;
const counters = new Map<string, { window: number; count: number }>();

export type RateLimitDecision = { allowed: true } | { allowed: false; retryAfterSeconds: number };

export function rateLimitDecision(credentialId: string, limitPerMinute: number, now: number = Date.now()): RateLimitDecision {
  const window = Math.floor(now / WINDOW_MS);
  const entry = counters.get(credentialId);
  if (!entry || entry.window !== window) {
    counters.set(credentialId, { window, count: 1 });
    return { allowed: true };
  }
  entry.count += 1;
  if (entry.count > limitPerMinute) {
    return { allowed: false, retryAfterSeconds: Math.max(1, Math.ceil(((window + 1) * WINDOW_MS - now) / 1000)) };
  }
  return { allowed: true };
}

/** Sends the contract's 429 shape: a stable `rate_limited` code and Retry-After. */
export function sendRateLimited(res: Response, retryAfterSeconds: number): void {
  res.set("Retry-After", String(retryAfterSeconds));
  sendError(res, 429, "rate_limited", "The credential has exceeded its request rate limit. Retry after the interval in Retry-After.");
}
