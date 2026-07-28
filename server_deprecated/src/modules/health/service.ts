import { healthEventsInRange, type EventRow } from "../../db";
import { healthSummary } from "../reports/service";

export interface HealthRecordResponse {
  type: string;
  start_at: string;
  end_at: string | null;
  value: number | null;
  unit: string | null;
  duration_minutes: number | null;
}

export interface HealthTimelineResponse {
  start: string;
  end: string;
  records: HealthRecordResponse[];
}

export function healthRangeSummary(start: Date, end: Date) {
  return healthSummary(start, end);
}

export function healthTimeline(start: Date, end: Date): HealthTimelineResponse {
  const rows = healthEventsInRange.all(end.toISOString(), start.toISOString()) as EventRow[];
  return {
    start: start.toISOString(),
    end: end.toISOString(),
    records: rows.map((row) => ({
      type: row.type,
      start_at: row.start_at,
      end_at: row.end_at,
      value: row.value,
      unit: row.unit,
      duration_minutes: row.duration_ms ? Math.round(row.duration_ms / 60000) : null,
    })),
  };
}
