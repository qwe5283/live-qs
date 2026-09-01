import { randomUUID } from "node:crypto";
import { AuditLogModel, BucketModel, DailyRollupModel, DeviceStateModel, EventModel, PrivacyRuleModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";
import { recordAuditLog } from "../../shared/audit.js";

const targetTypes = new Set(["app", "app_id", "app_name", "window_title", "event_type"]);
const actions = new Set(["allow_title", "hash_title", "hide_title", "drop_event", "category_only"]);

export async function exportUserData(userId: string, query: Record<string, unknown>) {
  const start = parseDate(query.start);
  const end = parseDate(query.end);
  const includeEvents = query.events !== "false";
  const eventFilter: Record<string, unknown> = { user_id: userId };
  if (start || end) eventFilter.start_at = { ...(start ? { $gte: start } : {}), ...(end ? { $lt: end } : {}) };
  const [buckets, deviceStates, rollups, privacyRules, auditLogs, events] = await Promise.all([
    BucketModel.find({ user_id: userId }).sort({ created_at: 1 }).lean().exec(),
    DeviceStateModel.find({ user_id: userId }).sort({ last_seen_at: -1 }).lean().exec(),
    DailyRollupModel.find({ user_id: userId }).sort({ date: 1 }).lean().exec(),
    PrivacyRuleModel.find({ user_id: userId }).sort({ created_at: 1 }).lean().exec(),
    AuditLogModel.find({ user_id: userId }).sort({ created_at: 1 }).lean().exec(),
    includeEvents ? EventModel.find(eventFilter).sort({ start_at: 1 }).lean().exec() : Promise.resolve([]),
  ]);
  await recordAuditLog({ userId, action: "data.export", details: { start, end, events: includeEvents, event_count: events.length } });
  return { exported_at: new Date().toISOString(), user_id: userId,
    filters: { start: start?.toISOString() ?? null, end: end?.toISOString() ?? null, events: includeEvents },
    buckets, device_states: deviceStates, daily_rollups: rollups, privacy_rules: privacyRules, audit_logs: auditLogs, events };
}

export async function listPrivacyRules(userId: string) {
  return { privacy_rules: await PrivacyRuleModel.find({ user_id: userId }).sort({ created_at: 1 }).lean().exec() };
}

export async function createPrivacyRule(userId: string, body: unknown) {
  if (!body || typeof body !== "object") throw new AppError(400, "Invalid JSON body");
  const input = body as Record<string, unknown>;
  const targetType = typeof input.target_type === "string" ? input.target_type.trim() : "";
  const pattern = typeof input.pattern === "string" ? input.pattern.trim().slice(0, 200) : "";
  const action = typeof input.action === "string" ? input.action.trim() : "";
  if (!targetTypes.has(targetType) || !pattern || !actions.has(action)) {
    throw new AppError(400, "target_type, pattern, and action are invalid");
  }
  const rule = { id: `rule_${randomUUID()}`, user_id: userId, target_type: targetType, pattern, action, created_at: new Date() };
  await PrivacyRuleModel.create(rule);
  await recordAuditLog({ userId, action: "privacy_rule.create", details: { target_type: targetType, action, pattern_present: true } });
  return { ok: true, privacy_rule: rule };
}

export async function deletePrivacyRule(userId: string, id: string) {
  if (!id.startsWith("rule_")) throw new AppError(400, "Invalid privacy rule id");
  const result = await PrivacyRuleModel.deleteOne({ user_id: userId, id });
  await recordAuditLog({ userId, action: "privacy_rule.delete", details: { id, deleted: result.deletedCount } });
  return { ok: true, deleted: result.deletedCount };
}

export async function deleteEvents(userId: string, body: unknown) {
  if (!body || typeof body !== "object") throw new AppError(400, "Invalid JSON body");
  const input = body as Record<string, unknown>;
  const start = parseDate(input.start);
  const end = parseDate(input.end);
  if (!start || !end || end <= start) throw new AppError(400, "start and end ISO timestamps required");
  const filter: Record<string, unknown> = { user_id: userId, start_at: { $gte: start, $lt: end } };
  if (typeof input.type === "string" && input.type) filter.type = input.type;
  if (typeof input.device_id === "string" && input.device_id) filter.device_id = input.device_id;
  const result = await EventModel.deleteMany(filter);
  const filters = { start: start.toISOString(), end: end.toISOString(),
    type: typeof input.type === "string" ? input.type : null, device_id: typeof input.device_id === "string" ? input.device_id : null };
  await recordAuditLog({ userId, action: "events.delete", details: { deleted_events: result.deletedCount, filters } });
  return { ok: true, deleted_events: result.deletedCount, filters };
}

export async function cleanupRetention(userId: string, query: Record<string, unknown>) {
  const now = new Date();
  const policy = { screenDays: bounded(query.screen_days, 90), healthDays: bounded(query.health_days, 365),
    paymentDays: bounded(query.payment_days, 3650), defaultDays: bounded(query.default_days, 365) };
  const cutoff = (days: number) => new Date(now.getTime() - days * 86_400_000);
  const screenTypes = ["app.foreground", "app.heartbeat", "usage.app_daily", "user.afk", "user.active"];
  const [screen, health, payment, other] = await Promise.all([
    EventModel.deleteMany({ user_id: userId, type: { $in: screenTypes }, start_at: { $lt: cutoff(policy.screenDays) } }),
    EventModel.deleteMany({ user_id: userId, type: /^health\./, start_at: { $lt: cutoff(policy.healthDays) } }),
    EventModel.deleteMany({ user_id: userId, type: "payment.transaction", start_at: { $lt: cutoff(policy.paymentDays) } }),
    EventModel.deleteMany({ user_id: userId, type: { $nin: [...screenTypes, "payment.transaction"], $not: /^health\./ }, start_at: { $lt: cutoff(policy.defaultDays) } }),
  ]);
  const deleted = { screen: screen.deletedCount, health: health.deletedCount, payment: payment.deletedCount, other: other.deletedCount };
  const total = Object.values(deleted).reduce((sum, value) => sum + value, 0);
  await recordAuditLog({ userId, action: "retention.cleanup", details: { policy, deleted, deleted_total: total } });
  return { ok: true, policy, deleted, deleted_total: total };
}

export async function listAuditLogs(userId: string, query: Record<string, unknown>) {
  const filter: Record<string, unknown> = { user_id: userId };
  const start = parseDate(query.start);
  const end = parseDate(query.end);
  if (start || end) filter.created_at = { ...(start ? { $gte: start } : {}), ...(end ? { $lt: end } : {}) };
  if (typeof query.action === "string" && query.action) filter.action = query.action;
  return { audit_logs: await AuditLogModel.find(filter).sort({ created_at: -1 }).limit(bounded(query.limit, 100, 1, 200)).lean().exec() };
}

function parseDate(value: unknown): Date | null {
  if (typeof value !== "string" || !value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}
function bounded(value: unknown, fallback: number, min = 1, max = 3650): number {
  const parsed = Number.parseInt(typeof value === "string" ? value : "", 10);
  return Number.isFinite(parsed) ? Math.max(min, Math.min(max, parsed)) : fallback;
}
