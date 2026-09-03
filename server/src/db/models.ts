import { Schema, model } from "mongoose";

/** TTL backstop purging projections hours after their device went silent; expiry semantics live in the status service. */
const DEVICE_STATUS_TTL_SECONDS = 21600;

const commonOptions = { versionKey: false, id: false } as const;

const bucketSchema = new Schema({
  id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true, index: true },
  device_id: { type: String, required: true },
  source: { type: String, required: true },
  type: { type: String, required: true },
  metadata: { type: Schema.Types.Mixed, default: {} },
  created_at: { type: Date, required: true },
}, commonOptions);

const eventSchema = new Schema({
  id: { type: String, required: true, unique: true },
  bucket_id: { type: String, required: true },
  user_id: { type: String, required: true },
  device_id: { type: String, required: true },
  source: { type: String, required: true },
  type: { type: String, required: true },
  schema_version: { type: Number, default: null },
  revision: { type: Number, default: null },
  finalization_state: { type: String, default: null },
  provenance: { type: Schema.Types.Mixed, default: null },
  capture_timezone: { type: String, default: null },
  capture_offset_minutes: { type: Number, default: null },
  invalidated: { type: Boolean, default: null },
  source_kind: { type: String, default: null },
  source_record_id: { type: String, default: null },
  device_platform: { type: String, default: null },
  start_at: { type: Date, required: true },
  end_at: { type: Date, default: null },
  duration_ms: { type: Number, default: null },
  value: { type: Number, default: null },
  unit: { type: String, default: null },
  data: { type: Schema.Types.Mixed, default: {} },
  privacy_level: { type: String, enum: ["normal", "sensitive", "private"], default: "normal" },
  confidence: { type: Number, min: 0, max: 1, default: 1 },
  raw_hash: { type: String, default: null },
  created_at: { type: Date, required: true },
  updated_at: { type: Date, required: true },
}, commonOptions);
eventSchema.index({ bucket_id: 1, start_at: -1 });
eventSchema.index({ user_id: 1, type: 1, start_at: -1 });
eventSchema.index({ user_id: 1, start_at: 1, end_at: 1 });
eventSchema.index(
  { raw_hash: 1 },
  { unique: true, partialFilterExpression: { raw_hash: { $type: "string" } } },
);

const eventRevisionSchema = new Schema({
  id: { type: String, required: true, unique: true },
  event_id: { type: String, required: true },
  user_id: { type: String, required: true, index: true },
  revision: { type: Number, required: true },
  archived_at: { type: Date, required: true },
  document: { type: Schema.Types.Mixed, required: true },
}, commonOptions);

const deviceStateSchema = new Schema({
  device_id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true, index: true },
  device_name: { type: String, required: true },
  platform: { type: String, enum: ["windows", "android", "macos"], required: true },
  current_type: { type: String, default: null },
  current_data: { type: Schema.Types.Mixed, default: {} },
  last_seen_at: { type: Date, required: true },
  is_online: { type: Boolean, default: true },
}, commonOptions);

const deviceStatusSchema = new Schema({
  // Server-bound device identity: the Device Token credential that reported the heartbeat.
  device_key: { type: String, required: true, unique: true },
  user_id: { type: String, required: true, index: true },
  platform: { type: String, enum: ["windows", "android"], required: true },
  device_name: { type: String, default: null },
  captured_at: { type: Date, required: true },
  activity: { type: Schema.Types.Mixed, default: null },
}, commonOptions);
// Heartbeats are ephemeral projections: freshness and offline expiry are
// computed from captured_at at read time so devices keep showing as offline.
// This TTL index only bounds collection growth long after a device stops.
deviceStatusSchema.index({ captured_at: 1 }, { expireAfterSeconds: DEVICE_STATUS_TTL_SECONDS });

const dailyRollupSchema = new Schema({
  user_id: { type: String, required: true },
  date: { type: String, required: true },
  timezone: { type: String, required: true },
  summary: { type: Schema.Types.Mixed, required: true },
  created_at: { type: Date, required: true },
  updated_at: { type: Date, required: true },
}, commonOptions);
dailyRollupSchema.index({ user_id: 1, date: 1, timezone: 1 }, { unique: true });

const auditLogSchema = new Schema({
  id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true },
  actor_type: { type: String, enum: ["user", "device", "query", "system"], required: true },
  actor_id: { type: String, default: null },
  action: { type: String, required: true },
  status: { type: String, enum: ["ok", "error"], required: true },
  details: { type: Schema.Types.Mixed, default: {} },
  created_at: { type: Date, required: true },
}, commonOptions);
auditLogSchema.index({ user_id: 1, created_at: -1 });
auditLogSchema.index({ user_id: 1, action: 1, created_at: -1 });

const credentialSchema = new Schema({
  id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true, index: true },
  kind: { type: String, enum: ["device_token", "query_token"], required: true },
  name: { type: String, required: true },
  token_hash: { type: String, required: true, unique: true },
  token_prefix: { type: String, required: true },
  scopes: { type: [String], required: true },
  allowed_event_types: { type: [String], default: [] },
  privacy_ceiling: { type: String, enum: ["normal", "sensitive", "private"], default: "normal" },
  created_at: { type: Date, required: true },
  expires_at: { type: Date, default: null },
  last_used_at: { type: Date, default: null },
  revoked_at: { type: Date, default: null },
}, commonOptions);

const privacyRuleSchema = new Schema({
  id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true },
  target_type: { type: String, required: true },
  pattern: { type: String, required: true },
  action: { type: String, required: true },
  created_at: { type: Date, required: true },
}, commonOptions);
privacyRuleSchema.index({ user_id: 1, created_at: 1 });

const ownerCredentialSchema = new Schema({
  user_id: { type: String, required: true, unique: true },
  kdf: { type: String, required: true },
  kdf_params: { type: Schema.Types.Mixed, required: true },
  salt: { type: String, required: true },
  password_hash: { type: String, required: true },
  created_at: { type: Date, required: true },
  updated_at: { type: Date, required: true },
}, commonOptions);

const ownerSessionSchema = new Schema({
  id: { type: String, required: true, unique: true },
  user_id: { type: String, required: true, index: true },
  created_at: { type: Date, required: true },
  expires_at: { type: Date, required: true },
  revoked_at: { type: Date, default: null },
}, commonOptions);
ownerSessionSchema.index({ expires_at: 1 }, { expireAfterSeconds: 0 });

const ownerSettingsSchema = new Schema({
  user_id: { type: String, required: true, unique: true },
  report_timezone: { type: String, required: true },
  updated_at: { type: Date, required: true },
}, commonOptions);

export const BucketModel = model("Bucket", bucketSchema);
export const EventModel = model("Event", eventSchema);
export const EventRevisionModel = model("EventRevision", eventRevisionSchema);
export const DeviceStateModel = model("DeviceState", deviceStateSchema);
export const DeviceStatusModel = model("DeviceStatus", deviceStatusSchema);
export const DailyRollupModel = model("DailyRollup", dailyRollupSchema);
export const AuditLogModel = model("AuditLog", auditLogSchema);
export const PrivacyRuleModel = model("PrivacyRule", privacyRuleSchema);
export const OwnerCredentialModel = model("OwnerCredential", ownerCredentialSchema);
export const OwnerSessionModel = model("OwnerSession", ownerSessionSchema);
export const OwnerSettingsModel = model("OwnerSettings", ownerSettingsSchema);
export const CredentialModel = model("Credential", credentialSchema);
