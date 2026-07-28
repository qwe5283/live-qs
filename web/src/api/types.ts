export interface DeviceState {
  device_id: string;
  device_name: string;
  platform: "windows" | "android" | "macos";
  online: boolean;
  current_type: string | null;
  current_app?: string;
  current_category?: string;
  is_afk: boolean;
  last_seen_minutes_ago: number;
}

export interface CurrentContext {
  server_time: string;
  user_id: string;
  devices: DeviceState[];
  today: {
    steps: number;
    sleep_minutes: number;
    avg_heart_rate: number | null;
    active_screen_minutes: number;
    focus_minutes: number;
    spending_total: number;
    spending_currency: string;
    top_apps: Array<{ app: string; minutes: number }>;
  };
}

export interface UsageSummary {
  start: string;
  end: string;
  active_screen_minutes: number;
  focus_minutes: number;
  top_apps: Array<{ app: string; minutes: number }>;
}

export interface UsageAppSummary {
  app: string;
  app_id?: string;
  device_id: string;
  minutes: number;
  sessions: number;
  hourly_minutes: number[];
  source: "usage_events" | "realtime";
}

export interface UsageAppsResponse {
  start: string;
  end: string;
  apps: UsageAppSummary[];
}

export interface UsageTimelineResponse {
  start: string;
  end: string;
  segments: Array<{
    device_id: string;
    app: string;
    app_id?: string;
    start_at: string;
    end_at: string | null;
    minutes: number;
    is_afk: boolean;
    category?: string;
  }>;
}

export interface HealthSummary {
  start: string;
  end: string;
  steps: number;
  sleep_minutes: number;
  avg_heart_rate: number | null;
}

export interface HealthTimelineResponse {
  start: string;
  end: string;
  records: Array<{
    type: string;
    start_at: string;
    end_at: string | null;
    value: number | null;
    unit: string | null;
    duration_minutes: number | null;
  }>;
}
