import { randomUUID } from "node:crypto";
import { db } from "../../db";
import { sanitizeAuditDetails } from "./audit-sanitize";

type AuditStatus = "ok" | "error";
type AuditActorType = "user" | "device" | "system";

export interface AuditLogInput {
  userId: string;
  actorType?: AuditActorType;
  actorId?: string | null;
  action: string;
  status?: AuditStatus;
  details?: Record<string, unknown>;
  createdAt?: string;
}

export interface AuditLogRow {
  id: string;
  user_id: string;
  actor_type: AuditActorType;
  actor_id: string | null;
  action: string;
  status: AuditStatus;
  details_json: string;
  created_at: string;
}

const insertAuditLog = db.prepare(`
  INSERT INTO audit_logs (id, user_id, actor_type, actor_id, action, status, details_json, created_at)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?)
`);

export function recordAuditLog(input: AuditLogInput): void {
  const entry = buildAuditLogEntry(input);
  insertAuditLog.run(
    entry.id,
    entry.user_id,
    entry.actor_type,
    entry.actor_id,
    entry.action,
    entry.status,
    entry.details_json,
    entry.created_at,
  );
}

export function buildAuditLogEntry(input: AuditLogInput): AuditLogRow {
  return {
    id: randomUUID(),
    user_id: input.userId,
    actor_type: input.actorType ?? "user",
    actor_id: input.actorId ?? null,
    action: input.action,
    status: input.status ?? "ok",
    details_json: JSON.stringify(sanitizeAuditDetails(input.details ?? {})),
    created_at: input.createdAt ?? new Date().toISOString(),
  };
}
