import { Router } from "express";
import { z } from "zod";
import type { Request } from "express";
import type { Env } from "../../config/env.js";
import { credentialBearerAuth, sessionOrCredentialAuth } from "../../middleware/auth.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import { AppError } from "../../shared/errors.js";
import { batchUpsertEvents, listEvents } from "./service.js";
import type { EventRangeQuery } from "./service.js";

function requiredInstant(value: unknown, name: string): Date {
  if (typeof value !== "string" || value.length === 0) {
    throw new AppError(400, `The query parameter ${name} is required.`, "invalid_request");
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    throw new AppError(400, `The query parameter ${name} must be an ISO 8601 instant.`, "invalid_request");
  }
  return parsed;
}

function optionalString(value: unknown, name: string): string | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== "string" || value.length === 0) {
    throw new AppError(400, `The query parameter ${name} must be a non-empty string.`, "invalid_request");
  }
  return value;
}

function parsePageSize(value: unknown): number {
  if (value === undefined) return 50;
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > 200) {
    throw new AppError(400, "The query parameter page_size must be an integer between 1 and 200.", "invalid_request");
  }
  return parsed;
}

function parseEventQuery(req: Request, userId: string): EventRangeQuery {
  const from = requiredInstant(req.query.from, "from");
  const to = requiredInstant(req.query.to, "to");
  const timezone = optionalString(req.query.timezone, "timezone");
  if (!timezone) throw new AppError(400, "The query parameter timezone is required.", "invalid_request");
  if (from >= to) {
    throw new AppError(400, "The query parameter to must be later than from.", "invalid_request");
  }
  const query: EventRangeQuery = {
    userId,
    from,
    to,
    timezone,
    pageSize: parsePageSize(req.query.page_size),
  };
  const eventType = optionalString(req.query.event_type, "event_type");
  if (eventType !== undefined) query.eventType = eventType;
  const cursor = optionalString(req.query.cursor, "cursor");
  if (cursor !== undefined) query.cursor = cursor;
  return query;
}

const batchRequestSchema = z.strictObject({
  events: z.array(z.unknown()).min(1).max(100),
});

export function eventsRouter(env: Env): Router {
  const router = Router();

  router.get("/", sessionOrCredentialAuth(env, { scope: "events:read" }), async (req, res) => {
    const query = parseEventQuery(req, env.DEFAULT_USER_ID);
    const credential = res.locals.credential as CredentialAuthContext | undefined;
    if (credential) {
      query.privacyCeiling = credential.privacy_ceiling;
      if (credential.allowed_event_types.length > 0) query.allowedEventTypes = credential.allowed_event_types;
    }
    res.json(await listEvents(query));
  });

  router.post("/batch", credentialBearerAuth(env, { scope: "events:write" }), async (req, res) => {
    const credential = res.locals.credential as CredentialAuthContext;
    if (credential.kind !== "device_token") {
      throw new AppError(403, "Only device tokens may upload events.", "insufficient_scope");
    }
    const parsed = batchRequestSchema.safeParse(req.body);
    if (!parsed.success) {
      throw new AppError(400, "The batch body must contain an events array of 1 to 100 items.", "invalid_request");
    }
    res.json(await batchUpsertEvents(env, credential, parsed.data.events));
  });

  return router;
}
