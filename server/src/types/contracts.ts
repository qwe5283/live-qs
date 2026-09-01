export type PrivacyLevel = "normal" | "sensitive" | "private";

export interface EventRow {
  id: string;
  bucket_id: string;
  user_id: string;
  device_id: string;
  source: string;
  type: string;
  schema_version: number | null;
  revision: number | null;
  finalization_state: string | null;
  provenance: { collector_version: string; observed_at: string } | null;
  capture_timezone: string | null;
  capture_offset_minutes: number | null;
  invalidated: boolean | null;
  source_kind: string | null;
  source_record_id: string | null;
  device_platform: string | null;
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
