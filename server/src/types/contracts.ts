export type Platform = "windows" | "android" | "macos";
export type PrivacyLevel = "normal" | "sensitive" | "private";

export interface DeviceIdentity {
  userId: string;
  deviceId: string;
  deviceName: string;
  platform: Platform;
}

export interface HeartbeatPayload {
  type?: unknown;
  bucket?: unknown;
  timestamp?: unknown;
  heartbeat_interval_ms?: unknown;
  data?: unknown;
}

export interface EventPayload {
  idempotency_key?: unknown;
  type?: unknown;
  bucket?: unknown;
  start_at?: unknown;
  end_at?: unknown;
  value?: unknown;
  unit?: unknown;
  data?: unknown;
  privacy_level?: unknown;
  confidence?: unknown;
}

export interface BatchEventPayload {
  events?: unknown;
}

export interface EventRow {
  id: string;
  bucket_id: string;
  user_id: string;
  device_id: string;
  source: string;
  type: string;
  start_at: Date;
  end_at: Date | null;
  duration_ms: number | null;
  value: number | null;
  unit: string | null;
  data: Record<string, unknown>;
  privacy_level: PrivacyLevel;
  confidence: number;
  raw_hash: string | null;
  created_at: Date;
  updated_at: Date;
}

export interface ScreenSummary {
  active_screen_minutes: number;
  focus_minutes: number;
  top_apps: Array<{ app: string; minutes: number }>;
}

export interface HealthMetrics {
  steps: number;
  sleep_minutes: number;
  avg_heart_rate: number | null;
}

export interface SpendingSummary {
  start: string;
  end: string;
  total_expense: number;
  total_income: number;
  net: number;
  currency: string;
  transaction_count: number;
  top_categories: Array<{ category: string; amount: number }>;
  top_merchants: Array<{ merchant: string; amount: number }>;
}

export interface DaySummary {
  date: string;
  timezone: string;
  health: HealthMetrics;
  screen: ScreenSummary;
  spending: SpendingSummary;
}
