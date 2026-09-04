import { Router } from "express";
import { z } from "zod";
import type { Request } from "express";
import type { Env } from "../../config/env.js";
import type { CredentialScope } from "../../generated/contract-models.js";
import { credentialBearerAuth, sessionOrCredentialAuth } from "../../middleware/auth.js";
import { recordQueryAudit } from "../../shared/audit.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import { AppError } from "../../shared/errors.js";
import { batchUpsertEvents, enrichDomainPage, eventTypesForReadScopes, listEvents } from "./service.js";
import type { EventDomain, EventRangeQuery } from "./service.js";
import { HEALTH_EVENT_TYPES, PAYMENT_EVENT_TYPES } from "./payload-registry.js";
import type { RegisteredEventType } from "./payload-registry.js";

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

/**
 * Bounded time range for credential reads (SPEC implementation decision 25):
 * a query token may only request a span up to the configured maximum, so one
 * Agent query can never sweep the whole history in a single request. Owner
 * sessions are unrestricted; the metrics endpoints are inherently day- or
 * week-bounded and need no check here.
 */
function assertBoundedCredentialRange(query: EventRangeQuery, env: Env, credential: CredentialAuthContext | undefined): void {
  if (!credential) return;
  const maxMs = env.QUERY_TOKEN_MAX_RANGE_DAYS * 86_400_000;
  if (query.to.getTime() - query.from.getTime() > maxMs) {
    throw new AppError(
      400,
      `The query range must not exceed ${env.QUERY_TOKEN_MAX_RANGE_DAYS} days for this credential.`,
      "range_too_large",
    );
  }
}

const batchRequestSchema = z.strictObject({
  events: z.array(z.unknown()).min(1).max(100),
});

export function eventsRouter(env: Env): Router {
  const router = Router();

  router.get("/", sessionOrCredentialAuth(env, { scope: "events:read" }), async (req, res) => {
    const query = parseEventQuery(req, env.DEFAULT_USER_ID);
    const credential = res.locals.credential as CredentialAuthContext | undefined;
    assertBoundedCredentialRange(query, env, credential);
    if (credential) {
      query.privacyCeiling = credential.privacy_ceiling;
      // Domain scopes bound what a credential read may return: a query token
      // without health:read never sees health observations, even when the
      // event_type filter would otherwise select them.
      query.scopeGrantedEventTypes = eventTypesForReadScopes(credential.scopes);
      if (credential.allowed_event_types.length > 0) query.allowedEventTypes = credential.allowed_event_types;
    }
    const page = await listEvents(query);
    await recordQueryAudit({
      userId: env.DEFAULT_USER_ID,
      credential,
      path: req.path,
      from: query.from.toISOString(),
      to: query.to.toISOString(),
      timezone: query.timezone,
      dataTypes: query.eventType ? [query.eventType] : (query.scopeGrantedEventTypes ?? []),
      resultCount: page.data.length,
      completeness: page.context.completeness,
    });
    res.json(page);
  });

  router.post("/batch", credentialBearerAuth(env, { anyScope: ["events:write", "health:write", "payment:write"] }), async (req, res) => {
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

/**
 * Domain-scoped read of one event domain's latest revisions, guarded by the
 * domain's read scope for credentials. Completeness is relative to the domain
 * this endpoint promises, so out-of-domain data never marks a page partial;
 * credential reads are further bounded by their privacy ceiling and
 * allowed-event-type list. Sleep appears only as source-provided intervals;
 * payments only as extracted transaction facts.
 */
function domainEventsRouter(env: Env, domainTypes: RegisteredEventType[], readScope: CredentialScope, domain: EventDomain): Router {
  const router = Router();
  const domainTypeStrings: string[] = [...domainTypes];

  router.get("/events", sessionOrCredentialAuth(env, { scope: readScope }), async (req, res) => {
    const query = parseEventQuery(req, env.DEFAULT_USER_ID);
    const credential = res.locals.credential as CredentialAuthContext | undefined;
    assertBoundedCredentialRange(query, env, credential);
    query.scopeGrantedEventTypes = domainTypeStrings;
    query.completenessBaseline = domainTypeStrings;
    if (credential) {
      query.privacyCeiling = credential.privacy_ceiling;
      query.scopeGrantedEventTypes = eventTypesForReadScopes(credential.scopes).filter(
        (type) => domainTypeStrings.includes(type),
      );
      if (credential.allowed_event_types.length > 0) query.allowedEventTypes = credential.allowed_event_types;
    }
    const page = await enrichDomainPage(query, await listEvents(query), domain);
    await recordQueryAudit({
      userId: env.DEFAULT_USER_ID,
      credential,
      path: req.path,
      from: query.from.toISOString(),
      to: query.to.toISOString(),
      timezone: query.timezone,
      dataTypes: query.eventType ? [query.eventType] : (query.scopeGrantedEventTypes ?? []),
      resultCount: page.data.length,
      completeness: page.context.completeness,
    });
    res.json(page);
  });

  return router;
}

/**
 * Health-domain read: every registered health event type, guarded by the
 * health:read scope for credentials. The context carries the applied source
 * policy version and multi-origin conflict entries.
 */
export function healthEventsRouter(env: Env): Router {
  return domainEventsRouter(env, HEALTH_EVENT_TYPES, "health:read", "health");
}

/**
 * Payment-domain read: extracted transaction facts, guarded by the
 * payment:read scope for credentials. Payment facts are sensitive by default,
 * so a credential also needs an adequate privacy ceiling to see them. The
 * context carries the policy version and the pending-confirmation count.
 */
export function paymentEventsRouter(env: Env): Router {
  return domainEventsRouter(env, PAYMENT_EVENT_TYPES, "payment:read", "payment");
}
