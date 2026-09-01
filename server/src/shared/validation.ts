import type { Request } from "express";
import { AppError } from "./errors.js";

export function isoTime(value: unknown): Date | null {
  if (typeof value !== "string" || !value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function parseDateRange(req: Request): { start: Date; end: Date } {
  const start = isoTime(req.query.start);
  const end = isoTime(req.query.end);
  if (!start || !end || end <= start) throw new AppError(400, "start and end ISO timestamps required");
  return { start, end };
}
