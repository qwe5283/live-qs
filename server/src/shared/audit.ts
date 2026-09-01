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
