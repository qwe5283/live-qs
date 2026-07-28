import type { Request } from "express";
import { AppError } from "./errors.js";

const MAX_DATA_JSON_BYTES = 8 * 1024;

export function jsonObject(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

export function safeData(data: Record<string, unknown>): Record<string, unknown> {
  if (Buffer.byteLength(JSON.stringify(data), "utf8") > MAX_DATA_JSON_BYTES) throw new AppError(400, "data too large");
  return data;
}

export function eventType(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  return /^[a-z][a-z0-9_.-]{1,80}$/.test(trimmed) ? trimmed : null;
}

export function bucketId(value: unknown, fallback: string): string {
  if (typeof value !== "string") return fallback;
  const trimmed = value.trim();
  return trimmed && trimmed.length <= 160 ? trimmed : fallback;
}

export function isoTime(value: unknown): Date | null {
  if (typeof value !== "string" || !value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function recentIsoTime(value: unknown): Date {
  const parsed = isoTime(value);
  const now = new Date();
  return !parsed || Math.abs(parsed.getTime() - now.getTime()) > 5 * 60_000 ? now : parsed;
}

export function bucketParts(bucket: string, fallbackType: string): { source: string; bucketType: string } {
  const parts = bucket.split(":").filter(Boolean);
  return { source: parts[0] || "unknown", bucketType: parts.length >= 3 ? parts.slice(2).join(":") : fallbackType };
}

export function parseDateRange(req: Request): { start: Date; end: Date } {
  const start = isoTime(req.query.start);
  const end = isoTime(req.query.end);
  if (!start || !end || end <= start) throw new AppError(400, "start and end ISO timestamps required");
  return { start, end };
}

export function positiveInt(value: unknown, fallback: number, min: number, max: number): number {
  const parsed = Number.parseInt(typeof value === "string" ? value : "", 10);
  return Number.isFinite(parsed) ? Math.max(min, Math.min(max, parsed)) : fallback;
}
