import { randomUUID } from "node:crypto";
import { AuditLogModel } from "../db/models.js";

const sensitiveKey = /(authorization|token|secret|password|window_title|notification_body|raw|payload|body)/i;

export type AuditActorType = "user" | "device" | "query" | "system";

export function sanitizeAuditDetails(value: unknown): unknown {
  if (value === null || ["string", "number", "boolean"].includes(typeof value)) return value;
  if (Array.isArray(value)) return value.slice(0, 50).map(sanitizeAuditDetails);
  if (typeof value !== "object") return null;
  return Object.fromEntries(Object.entries(value as Record<string, unknown>)
    .map(([key, child]) => [key, sensitiveKey.test(key) ? "[redacted]" : sanitizeAuditDetails(child)]));
}

export async function recordAuditLog(input: {
  userId: string;
  actorType?: AuditActorType;
  actorId?: string | null;
  action: string;
  status?: "ok" | "error";
  details?: Record<string, unknown>;
}): Promise<void> {
  await AuditLogModel.create({
    id: randomUUID(), user_id: input.userId, actor_type: input.actorType ?? "user", actor_id: input.actorId ?? null,
    action: input.action, status: input.status ?? "ok", details: sanitizeAuditDetails(input.details ?? {}), created_at: new Date(),
  });
}

/** Credential facts a query audit records; structural to avoid a shared→modules import cycle. */
export interface QueryAuditCredential {
  id: string;
  kind: string;
  scopes: string[];
}

/**
 * Records one read of Owner data by a bearer credential (SPEC story 10 and
 * ticket 16): the access subject, its scopes, the requested time range, the
 * data types, and the result count. Prompt bodies are structurally
 * impossible here: the read-only Skill sends only structured query
 * parameters, so no prompt text ever reaches the server, and
 * sanitizeAuditDetails redacts payload-like keys as a backstop. Current-
 * context reads carry no historical range, so from/to are omitted for them.
 * Owner session reads pass an undefined credential and record nothing: the
 * query audit exists to make AI credential access observable.
 */
export async function recordQueryAudit(input: {
  userId: string;
  credential: QueryAuditCredential | undefined;
  path: string;
  from?: string;
  to?: string;
  timezone?: string;
  dataTypes: string[];
  resultCount: number;
  completeness?: string;
}): Promise<void> {
  if (input.credential === undefined) return;
  await recordAuditLog({
    userId: input.userId,
    actorType: input.credential.kind === "device_token" ? "device" : "query",
    actorId: input.credential.id,
    action: "query.read",
    details: {
      credential_id: input.credential.id,
      credential_kind: input.credential.kind,
      scopes: input.credential.scopes,
      path: input.path,
      ...(input.from !== undefined ? { from: input.from } : {}),
      ...(input.to !== undefined ? { to: input.to } : {}),
      ...(input.timezone !== undefined ? { timezone: input.timezone } : {}),
      data_types: input.dataTypes,
      result_count: input.resultCount,
      ...(input.completeness !== undefined ? { completeness: input.completeness } : {}),
    },
  });
}
