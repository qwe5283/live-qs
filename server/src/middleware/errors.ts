import type { ErrorRequestHandler, RequestHandler } from "express";
import { ZodError } from "zod";
import { AppError, sendError } from "../shared/errors.js";

export const notFound: RequestHandler = (_req, res) => {
  sendError(res, 404, "not_found", "The requested resource does not exist.");
};

export const errorHandler: ErrorRequestHandler = (error: unknown, _req, res, _next) => {
  if (error instanceof AppError) {
    sendError(res, error.status, error.code, error.message);
    return;
  }
  if (error instanceof ZodError) {
    sendError(res, 400, "invalid_request", "Request validation failed.");
    return;
  }
  if (error instanceof SyntaxError && "body" in error) {
    sendError(res, 400, "invalid_request", "Invalid JSON body.");
    return;
  }
  console.error("[server] Unhandled error", error);
  sendError(res, 500, "internal_error", "Internal server error.");
};
