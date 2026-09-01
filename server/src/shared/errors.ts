import { randomUUID } from "node:crypto";
import type { Response } from "express";

export class AppError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly code: string = errorCodeForStatus(status),
  ) {
    super(message);
  }
}

export function requestIdOf(res: Response): string {
  return (res.locals.requestId as string | undefined) ?? randomUUID();
}

export function sendError(res: Response, status: number, code: string, message: string): void {
  res.status(status).json({ error: { code, message }, request_id: requestIdOf(res) });
}

function errorCodeForStatus(status: number): string {
  switch (status) {
    case 400: return "invalid_request";
    case 401: return "unauthorized";
    case 403: return "forbidden";
    case 404: return "not_found";
    case 409: return "conflict";
    case 429: return "rate_limited";
    default: return "internal_error";
  }
}
