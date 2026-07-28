import { Database } from "bun:sqlite";

export interface EventRow {
  id: string;
  bucket_id: string;
  user_id: string;
  device_id: string;
  source: string;
  type: string;
  start_at: string;
  end_at: string | null;
  duration_ms: number | null;
  value: number | null;
  unit: string | null;
  data_json: string;
  privacy_level: string;
  confidence: number;
  raw_hash: string | null;
  created_at: string;
  updated_at: string;
}

export interface DeviceStateRow {
  device_id: string;
  user_id: string;
  device_name: string;
  platform: "windows" | "android" | "macos";
  current_type: string | null;
  current_data_json: string;
  last_seen_at: string;
  is_online: number;
}

export interface DailyRollupRow {
  user_id: string;
  date: string;
  timezone: string;
  summary_json: string;
  created_at: string;
  updated_at: string;
}

export interface AuditLogRow {
  id: string;
  user_id: string;
  actor_type: string;
  actor_id: string | null;
  action: string;
  status: string;
  details_json: string;
  created_at: string;
}

export interface PrivacyRuleRow {
  id: string;
  user_id: string;
  target_type: string;
  pattern: string;
  action: string;
  created_at: string;
}

const DB_PATH = process.env.DB_PATH || "./data/ai-life.db";

export const db = new Database(DB_PATH, { create: true });
db.run("PRAGMA journal_mode = WAL");
db.run("PRAGMA busy_timeout = 5000");
db.run("PRAGMA synchronous = NORMAL");

db.run(`
  CREATE TABLE IF NOT EXISTS buckets (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    source TEXT NOT NULL,
    type TEXT NOT NULL,
    created_at TEXT NOT NULL,
    metadata_json TEXT NOT NULL DEFAULT '{}'
  )
`);

db.run(`
  CREATE TABLE IF NOT EXISTS events (
    id TEXT PRIMARY KEY,
    bucket_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    source TEXT NOT NULL,
    type TEXT NOT NULL,
    start_at TEXT NOT NULL,
    end_at TEXT,
    duration_ms INTEGER,
    value REAL,
    unit TEXT,
    data_json TEXT NOT NULL DEFAULT '{}',
    privacy_level TEXT NOT NULL DEFAULT 'normal',
    confidence REAL NOT NULL DEFAULT 1.0,
    raw_hash TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  )
`);

db.run("CREATE INDEX IF NOT EXISTS idx_events_bucket_start ON events(bucket_id, start_at DESC)");
db.run("CREATE INDEX IF NOT EXISTS idx_events_type_start ON events(type, start_at DESC)");
db.run("CREATE INDEX IF NOT EXISTS idx_events_range ON events(start_at, end_at)");
db.run(`
  CREATE UNIQUE INDEX IF NOT EXISTS idx_events_raw_hash
  ON events(raw_hash)
  WHERE raw_hash IS NOT NULL AND raw_hash != ''
`);

db.run(`
  CREATE TABLE IF NOT EXISTS device_states (
    device_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    device_name TEXT NOT NULL,
    platform TEXT NOT NULL,
    current_type TEXT,
    current_data_json TEXT NOT NULL DEFAULT '{}',
    last_seen_at TEXT NOT NULL,
    is_online INTEGER NOT NULL DEFAULT 1
  )
`);

db.run(`
  CREATE TABLE IF NOT EXISTS daily_rollups (
    user_id TEXT NOT NULL,
    date TEXT NOT NULL,
    timezone TEXT NOT NULL,
    summary_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (user_id, date, timezone)
  )
`);

db.run(`
  CREATE TABLE IF NOT EXISTS audit_logs (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    actor_type TEXT NOT NULL,
    actor_id TEXT,
    action TEXT NOT NULL,
    status TEXT NOT NULL,
    details_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL
  )
`);

db.run("CREATE INDEX IF NOT EXISTS idx_audit_logs_user_created ON audit_logs(user_id, created_at DESC)");
db.run("CREATE INDEX IF NOT EXISTS idx_audit_logs_action_created ON audit_logs(action, created_at DESC)");

db.run(`
  CREATE TABLE IF NOT EXISTS privacy_rules (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    target_type TEXT NOT NULL,
    pattern TEXT NOT NULL,
    action TEXT NOT NULL,
    created_at TEXT NOT NULL
  )
`);

db.run("CREATE INDEX IF NOT EXISTS idx_privacy_rules_user_created ON privacy_rules(user_id, created_at DESC)");

export const upsertBucket = db.prepare(`
  INSERT INTO buckets (id, user_id, device_id, source, type, created_at, metadata_json)
  VALUES (?, ?, ?, ?, ?, ?, ?)
  ON CONFLICT(id) DO NOTHING
`);

export const latestEventByBucket = db.prepare(`
  SELECT * FROM events WHERE bucket_id = ? ORDER BY start_at DESC LIMIT 1
`);

export const insertEvent = db.prepare(`
  INSERT OR IGNORE INTO events (
    id, bucket_id, user_id, device_id, source, type, start_at, end_at, duration_ms,
    value, unit, data_json, privacy_level, confidence, raw_hash, created_at, updated_at
  )
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`);

export const upsertAggregateEvent = db.prepare(`
  INSERT INTO events (
    id, bucket_id, user_id, device_id, source, type, start_at, end_at, duration_ms,
    value, unit, data_json, privacy_level, confidence, raw_hash, created_at, updated_at
  )
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  ON CONFLICT(raw_hash) WHERE raw_hash IS NOT NULL AND raw_hash != '' DO UPDATE SET
    bucket_id = excluded.bucket_id,
    user_id = excluded.user_id,
    device_id = excluded.device_id,
    source = excluded.source,
    type = excluded.type,
    start_at = excluded.start_at,
    end_at = excluded.end_at,
    duration_ms = excluded.duration_ms,
    value = excluded.value,
    unit = excluded.unit,
    data_json = excluded.data_json,
    privacy_level = excluded.privacy_level,
    confidence = excluded.confidence,
    updated_at = excluded.updated_at
`);

export const updateEventEnd = db.prepare(`
  UPDATE events SET end_at = ?, duration_ms = ?, updated_at = ? WHERE id = ?
`);

export const upsertDeviceState = db.prepare(`
  INSERT INTO device_states (
    device_id, user_id, device_name, platform, current_type, current_data_json, last_seen_at, is_online
  )
  VALUES (?, ?, ?, ?, ?, ?, ?, 1)
  ON CONFLICT(device_id) DO UPDATE SET
    user_id = excluded.user_id,
    device_name = excluded.device_name,
    platform = excluded.platform,
    current_type = excluded.current_type,
    current_data_json = excluded.current_data_json,
    last_seen_at = excluded.last_seen_at,
    is_online = 1
`);

export const allDeviceStates = db.prepare("SELECT * FROM device_states ORDER BY last_seen_at DESC");

export const eventsInRange = db.prepare(`
  SELECT * FROM events
  WHERE start_at < ? AND COALESCE(end_at, start_at) >= ?
  ORDER BY start_at ASC
`);

export const healthEventsInRange = db.prepare(`
  SELECT * FROM events
  WHERE type IN ('health.steps', 'health.heart_rate', 'health.sleep', 'health.exercise')
    AND start_at < ? AND COALESCE(end_at, start_at) >= ?
  ORDER BY start_at ASC
`);

export const upsertDailyRollup = db.prepare(`
  INSERT INTO daily_rollups (user_id, date, timezone, summary_json, created_at, updated_at)
  VALUES (?, ?, ?, ?, ?, ?)
  ON CONFLICT(user_id, date, timezone) DO UPDATE SET
    summary_json = excluded.summary_json,
    updated_at = excluded.updated_at
`);

export const getDailyRollup = db.prepare(`
  SELECT * FROM daily_rollups
  WHERE user_id = ? AND date = ? AND timezone = ?
`);

export const privacyRulesByUser = db.prepare(`
  SELECT * FROM privacy_rules
  WHERE user_id = ?
  ORDER BY created_at ASC
`);
