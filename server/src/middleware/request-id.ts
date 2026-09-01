import { randomUUID } from "node:crypto";
import type { RequestHandler } from "express";

export function assignRequestId(): RequestHandler {
  return (_req, res, next) => {
    const requestId = randomUUID();
    res.locals.requestId = requestId;
    res.setHeader("x-request-id", requestId);
    next();
  };
}
