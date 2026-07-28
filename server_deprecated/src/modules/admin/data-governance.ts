import { db } from "../../db";
import { recordAuditLog } from "./audit";
import { DEFAULT_RETENTION_POLICY, parsePositiveInt, retentionCutoffs } from "../../shared/retention";

type SqlBinding = string | number | bigint | boolean | null | Uint8Array;

interface DeleteRange {
  start: Date;
  end: Date;
  type?: string;
  deviceId?: string;
}

const PRIVACY_TARGET_TYPES = new Set(["app", "app_id", "app_name", "window_title", "event_type"]);
const PRIVACY_ACTIONS = new Set(["allow_title", "hash_title", "hide_title", "drop_event", "category_only"]);

function tableAll(sql: string, ...params: SqlBinding[]) {
  return db.prepare(sql).all(...params);
}

export function exportUserData(userId: string, url: URL): Response {
  const start = parseDate(url.searchParams.get("start"));
  const end = parseDate(url.searchParams.get("end"));
  const includeEvents = url.searchParams.get("events") !== "false";

  const eventRows =
    includeEvents && start && end
      ? tableAll(
          `
            SELECT *
            FROM events
            WHERE user_id = ?
              AND start_at >= ?
              AND start_at < ?
            ORDER BY start_at ASC
          `,
          userId,
          start.toISOString(),
          end.toISOString(),
        )
      : includeEvents
        ? tableAll("SELECT * FROM events WHERE user_id = ? ORDER BY start_at ASC", userId)
      : [];

  recordAuditLog({
    userId,
    action: "data.export",
    details: {
      start: start?.toISOString() ?? null,
      end: end?.toISOString() ?? null,
      events: includeEvents,
      event_count: eventRows.length,
    },
  });

  return Response.json({
    exported_at: new Date().toISOString(),
    user_id: userId,
    filters: {
      start: start?.toISOString() ?? null,
      end: end?.toISOString() ?? null,
      events: includeEvents,
    },
    buckets: tableAll("SELECT * FROM buckets WHERE user_id = ? ORDER BY created_at ASC", userId),
    device_states: tableAll("SELECT * FROM device_states WHERE user_id = ? ORDER BY last_seen_at DESC", userId),
    daily_rollups: tableAll("SELECT * FROM daily_rollups WHERE user_id = ? ORDER BY date ASC", userId),
    privacy_rules: tableAll("SELECT * FROM privacy_rules WHERE user_id = ? ORDER BY created_at ASC", userId),
    audit_logs: tableAll("SELECT * FROM audit_logs WHERE user_id = ? ORDER BY created_at ASC", userId),
    events: eventRows,
  });
}

export function listPrivacyRules(userId: string): Response {
  return Response.json({
    privacy_rules: tableAll("SELECT * FROM privacy_rules WHERE user_id = ? ORDER BY created_at ASC", userId),
  });
}

export async function createPrivacyRule(userId: string, req: Request): Promise<Response> {
  const body = await req.json().catch(() => null);
  if (!body || typeof body !== "object") {
    return Response.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  const rule = parsePrivacyRuleBody(body as Record<string, unknown>);
  if (!rule) {
    return Response.json(
      {
        error:
          "target_type, pattern, and action required; target_type must be app/app_id/app_name/window_title/event_type; action must be allow_title/hash_title/hide_title/drop_event/category_only",
      },
      { status: 400 },
    );
  }

  const nowIso = new Date().toISOString();
  const id = `rule_${crypto.randomUUID()}`;
  db.prepare(
    `
      INSERT INTO privacy_rules (id, user_id, target_type, pattern, action, created_at)
      VALUES (?, ?, ?, ?, ?, ?)
    `,
  ).run(id, userId, rule.target_type, rule.pattern, rule.action, nowIso);

  recordAuditLog({
    userId,
    action: "privacy_rule.create",
    details: {
      target_type: rule.target_type,
      action: rule.action,
      pattern_present: true,
    },
  });

  return Response.json({
    ok: true,
    privacy_rule: {
      id,
      user_id: userId,
      ...rule,
      created_at: nowIso,
    },
  });
}

export function deletePrivacyRule(userId: string, id: string): Response {
  if (!id.startsWith("rule_")) {
    return Response.json({ error: "Invalid privacy rule id" }, { status: 400 });
  }

  const result = db.prepare("DELETE FROM privacy_rules WHERE user_id = ? AND id = ?").run(userId, id);
  recordAuditLog({
    userId,
    action: "privacy_rule.delete",
    details: { id, deleted: result.changes },
  });

  return Response.json({ ok: true, deleted: result.changes });
}

export async function deleteEvents(userId: string, req: Request): Promise<Response> {
  const body = await req.json().catch(() => null);
  if (!body || typeof body !== "object") {
    return Response.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  const range = parseDeleteRange(body as Record<string, unknown>);
  if (!range) {
    return Response.json({ error: "start and end ISO timestamps required" }, { status: 400 });
  }

  let sql = "DELETE FROM events WHERE user_id = ? AND start_at >= ? AND start_at < ?";
  const params: SqlBinding[] = [userId, range.start.toISOString(), range.end.toISOString()];
  if (range.type) {
    sql += " AND type = ?";
    params.push(range.type);
  }
  if (range.deviceId) {
    sql += " AND device_id = ?";
    params.push(range.deviceId);
  }

  const result = db.prepare(sql).run(...params);
  recordAuditLog({
    userId,
    action: "events.delete",
    details: {
      deleted_events: result.changes,
      filters: {
        start: range.start.toISOString(),
        end: range.end.toISOString(),
        type: range.type ?? null,
        device_id: range.deviceId ?? null,
      },
    },
  });

  return Response.json({
    ok: true,
    deleted_events: result.changes,
    filters: {
      start: range.start.toISOString(),
      end: range.end.toISOString(),
      type: range.type ?? null,
      device_id: range.deviceId ?? null,
    },
  });
}

export function cleanupRetention(userId: string, url: URL): Response {
  const now = new Date();
  const policy = {
    screenDays: parsePositiveInt(url.searchParams.get("screen_days"), DEFAULT_RETENTION_POLICY.screenDays, 1, 3650),
    healthDays: parsePositiveInt(url.searchParams.get("health_days"), DEFAULT_RETENTION_POLICY.healthDays, 1, 3650),
    paymentDays: parsePositiveInt(url.searchParams.get("payment_days"), DEFAULT_RETENTION_POLICY.paymentDays, 1, 3650),
    defaultDays: parsePositiveInt(url.searchParams.get("default_days"), DEFAULT_RETENTION_POLICY.defaultDays, 1, 3650),
  };
  const cutoffs = retentionCutoffs(now, policy);

  const tx = db.transaction(() => {
    const screen = db
      .prepare(
        `
          DELETE FROM events
          WHERE user_id = ?
            AND type IN ('app.foreground', 'app.heartbeat', 'usage.app_daily', 'user.afk', 'user.active')
            AND start_at < ?
        `,
      )
      .run(userId, cutoffs.screenBefore).changes;
    const health = db
      .prepare("DELETE FROM events WHERE user_id = ? AND type LIKE 'health.%' AND start_at < ?")
      .run(userId, cutoffs.healthBefore).changes;
    const payment = db
      .prepare("DELETE FROM events WHERE user_id = ? AND type = 'payment.transaction' AND start_at < ?")
      .run(userId, cutoffs.paymentBefore).changes;
    const other = db
      .prepare(
        `
          DELETE FROM events
          WHERE user_id = ?
            AND type NOT IN ('app.foreground', 'app.heartbeat', 'usage.app_daily', 'user.afk', 'user.active', 'payment.transaction')
            AND type NOT LIKE 'health.%'
            AND start_at < ?
        `,
      )
      .run(userId, cutoffs.defaultBefore).changes;
    return { screen, health, payment, other };
  });

  const deleted = tx();
  recordAuditLog({
    userId,
    action: "retention.cleanup",
    details: {
      policy,
      cutoffs,
      deleted,
      deleted_total: deleted.screen + deleted.health + deleted.payment + deleted.other,
    },
  });

  return Response.json({
    ok: true,
    policy,
    cutoffs,
    deleted,
    deleted_total: deleted.screen + deleted.health + deleted.payment + deleted.other,
  });
}

export function listAuditLogs(userId: string, url: URL): Response {
  const limit = parsePositiveInt(url.searchParams.get("limit"), 100, 1, 200);
  const start = parseDate(url.searchParams.get("start"));
  const end = parseDate(url.searchParams.get("end"));
  const action = url.searchParams.get("action");

  let sql = "SELECT * FROM audit_logs WHERE user_id = ?";
  const params: SqlBinding[] = [userId];

  if (start) {
    sql += " AND created_at >= ?";
    params.push(start.toISOString());
  }
  if (end) {
    sql += " AND created_at < ?";
    params.push(end.toISOString());
  }
  if (action) {
    sql += " AND action = ?";
    params.push(action);
  }

  sql += " ORDER BY created_at DESC LIMIT ?";
  params.push(limit);

  return Response.json({
    audit_logs: tableAll(sql, ...params).map((row) => auditResponseRow(row as { details_json?: string })),
  });
}

function parseDate(value: string | null): Date | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function parseDeleteRange(body: Record<string, unknown>): DeleteRange | null {
  const start = typeof body.start === "string" ? parseDate(body.start) : null;
  const end = typeof body.end === "string" ? parseDate(body.end) : null;
  if (!start || !end || end <= start) return null;

  return {
    start,
    end,
    type: typeof body.type === "string" && body.type ? body.type : undefined,
    deviceId: typeof body.device_id === "string" && body.device_id ? body.device_id : undefined,
  };
}

function parsePrivacyRuleBody(body: Record<string, unknown>): { target_type: string; pattern: string; action: string } | null {
  const targetType = typeof body.target_type === "string" ? body.target_type.trim() : "";
  const pattern = typeof body.pattern === "string" ? body.pattern.trim().slice(0, 200) : "";
  const action = typeof body.action === "string" ? body.action.trim() : "";
  if (!PRIVACY_TARGET_TYPES.has(targetType) || !pattern || !PRIVACY_ACTIONS.has(action)) return null;
  return { target_type: targetType, pattern, action };
}

function auditResponseRow(row: { details_json?: string }) {
  const { details_json, ...rest } = row;
  return {
    ...rest,
    details: parseDetails(details_json),
  };
}

function parseDetails(value: string | undefined): unknown {
  if (!value) return {};
  try {
    return JSON.parse(value);
  } catch {
    return {};
  }
}
