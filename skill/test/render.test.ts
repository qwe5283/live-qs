import { describe, expect, it } from "vitest";
import type {
  DeviceStatusList,
  EventPage,
  QueryContext,
  SourceConflict,
  SyncDiagnosticList,
  UsageDayReport,
} from "../src/generated/contract-models.js";
import {
  describeDeviceStatus,
  describeEventPage,
  describeQueryContext,
  describeSyncDiagnostics,
  describeUsageDayReport,
} from "../src/render.js";

/**
 * Presentation-layer contract tests (ticket 16, checklist 7): fixtures with
 * missing, partial, zero, and conflicting data must render in a way that
 * never presents incomplete data as certain fact.
 */

function context(overrides: Partial<QueryContext> = {}): QueryContext {
  return {
    from: "2026-09-01T00:00:00.000Z",
    to: "2026-09-02T00:00:00.000Z",
    timezone: "Asia/Shanghai",
    provenance: [],
    completeness: "complete",
    ...overrides,
  };
}

const conflict: SourceConflict = {
  metric: "usage.app_minutes",
  policy_version: 2,
  selected_source: "android.usagestats",
  selected_event_ids: ["sel-1"],
  competing_sources: ["android.accessibility"],
  competing_event_ids: ["comp-1", "comp-2"],
  from: "2026-09-01T02:00:00.000Z",
  to: "2026-09-01T04:00:00.000Z",
};

describe("query context honesty", () => {
  it("renders no_data as missing, never as zero", () => {
    const lines = describeQueryContext(context({ data_state: "no_data" }));
    const text = lines.join("\n");
    expect(text).toMatch(/no observations at all/);
    expect(text).toMatch(/MISSING, never as zero/);
  });

  it("renders zero as a measured zero, distinct from missing data", () => {
    const lines = describeQueryContext(context({ data_state: "zero" }));
    const text = lines.join("\n");
    expect(text).toMatch(/explicitly 0/);
    expect(text).toMatch(/measured zero, not missing data/);
  });

  it("renders partial completeness as an undercount with its cause", () => {
    const lines = describeQueryContext(context({ completeness: "partial", data_state: "observed" }));
    const text = lines.join("\n");
    expect(text).toMatch(/partial/);
    expect(text).toMatch(/undercount/);
    expect(text).toMatch(/scope or privacy ceiling/);
  });

  it("carries conflicts verbatim, naming selected and retained competing observations", () => {
    const lines = describeQueryContext(context({ data_state: "observed", source_policy_version: 2, source_conflicts: [conflict] }));
    const text = lines.join("\n");
    expect(text).toContain("android.usagestats");
    expect(text).toContain("android.accessibility");
    expect(text).toContain("comp-1");
    expect(text).toContain("comp-2");
    expect(text).toMatch(/remain retained but are excluded/);
    expect(text).toMatch(/Do not present the selected value as the only observation/);
    expect(text).toContain("source policy version 2");
  });

  it("reports pending payment confirmations instead of hiding them", () => {
    const lines = describeQueryContext(context({ data_state: "observed", pending_confirmation_count: 3 }));
    expect(lines.join("\n")).toMatch(/3 transaction candidate\(s\).*pending Owner confirmation/);
  });

  it("always states the report timezone and time range", () => {
    const lines = describeQueryContext(context());
    expect(lines.join("\n")).toContain("report timezone Asia/Shanghai");
    expect(lines.join("\n")).toContain("2026-09-01T00:00:00.000Z to 2026-09-02T00:00:00.000Z");
  });
});

describe("usage report honesty", () => {
  const baseReport: UsageDayReport = {
    date: "2026-09-01",
    timezone: "Asia/Shanghai",
    metrics: { device_minutes: 0, active_minutes: 0 },
    devices: [{ device_id: "cred_device", platform: "windows", device_minutes: 0, active_minutes: 0 }],
    context: context({ data_state: "no_data" }),
  };

  it("never prints the server's 0 as a duration when the range has no data", () => {
    const text = describeUsageDayReport(baseReport).join("\n");
    expect(text).toMatch(/Device minutes: unavailable/);
    expect(text).toMatch(/missing data, not zero/);
    expect(text).not.toMatch(/Device minutes: 0/);
    expect(text).not.toMatch(/device cred_device/);
  });

  it("prints an explicit zero only when observations exist and normalize to 0", () => {
    const text = describeUsageDayReport({
      ...baseReport,
      context: context({ data_state: "zero", provenance: ["windows.foreground"] }),
    }).join("\n");
    expect(text).toMatch(/Device minutes: 0 — observations exist/);
    expect(text).toMatch(/measured zero/);
  });

  it("passes the server's numbers through untouched when observed", () => {
    const text = describeUsageDayReport({
      ...baseReport,
      metrics: { device_minutes: 120, active_minutes: 45 },
      devices: [{ device_id: "cred_device", platform: "windows", device_minutes: 120, active_minutes: 45 }],
      context: context({ data_state: "observed", provenance: ["windows.foreground"] }),
    }).join("\n");
    expect(text).toContain("Device minutes: 120");
    expect(text).toContain("Active minutes: 45");
    expect(text).toContain("concurrent device use can legitimately exceed elapsed time");
    expect(text).toContain("overlapping time counts once");
  });
});

describe("event page honesty", () => {
  it("marks manual corrections as human interpretations", () => {
    const page: EventPage = {
      data: [{
        event_id: "e1",
        event_type: "activity.interval",
        schema_version: 1,
        owner_id: "test-user",
        source: { kind: "windows.foreground", record_id: "r1" },
        device: { id: "cred_device", platform: "windows" },
        start_at: "2026-09-01T01:00:00.000Z",
        end_at: "2026-09-01T01:30:00.000Z",
        capture_timezone: "Asia/Shanghai",
        capture_offset_minutes: 480,
        privacy_level: "normal",
        revision: 1000000001,
        finalization_state: "final",
        provenance: { collector_version: "0.1.0", observed_at: "2026-09-01T01:35:00.000Z" },
        invalidated: false,
        correction: { corrected_at: "2026-09-01T12:00:00.000Z", reason: "wrong app" },
        payload: { application_id: "idea64.exe", is_afk: false, duration: { value: 1_800_000, unit: "ms" } },
      }],
      page: { page_size: 50, next_cursor: null },
      context: context({ data_state: "observed", provenance: ["windows.foreground"] }),
    };
    const text = describeEventPage("Activity events", page).join("\n");
    expect(text).toMatch(/manual Owner correction/);
    expect(text).toMatch(/wrong app/);
    expect(text).toMatch(/human interpretations, not raw device output/);
  });

  it("does not invent durations for open intervals", () => {
    const page: EventPage = {
      data: [{
        event_id: "e2",
        event_type: "activity.interval",
        schema_version: 1,
        owner_id: "test-user",
        source: { kind: "windows.foreground", record_id: "r2" },
        device: { id: "cred_device", platform: "windows" },
        start_at: "2026-09-01T08:00:00.000Z",
        capture_timezone: "Asia/Shanghai",
        capture_offset_minutes: 480,
        privacy_level: "normal",
        revision: 7,
        finalization_state: "checkpoint",
        provenance: { collector_version: "0.1.0", observed_at: "2026-09-01T08:05:00.000Z" },
        invalidated: false,
        payload: { application_id: "Code.exe", is_afk: false, duration: { value: 0, unit: "ms" } },
      }],
      page: { page_size: 50, next_cursor: null },
      context: context({ data_state: "observed", provenance: ["windows.foreground"] }),
    };
    const text = describeEventPage("Activity events", page).join("\n");
    expect(text).toMatch(/open interval, duration unknown/);
  });
});

describe("current context honesty", () => {
  it("keeps concurrent device states independent and marks staleness", () => {
    const list: DeviceStatusList = {
      server_time: "2026-09-01T09:00:00.000Z",
      devices: [
        { device_id: "a", device_name: "Desk", platform: "windows", online: true, age_seconds: 5, captured_at: "2026-09-01T08:59:55.000Z", activity: { application_id: "Code.exe", application_label: "VS Code", is_afk: false } },
        { device_id: "b", device_name: "Phone", platform: "android", online: false, age_seconds: 3600, captured_at: "2026-09-01T08:00:00.000Z", activity: { is_afk: true } },
      ],
    };
    const text = describeDeviceStatus(list).join("\n");
    expect(text).toContain("Desk");
    expect(text).toContain("Phone");
    expect(text).toContain("offline, last heartbeat 3600s ago");
    expect(text).toMatch(/never merges|no single focus|expirable current states/);
    expect(text).toMatch(/not historical evidence/);
  });

  it("explains missing data through sync diagnostics instead of guessing", () => {
    const list: SyncDiagnosticList = {
      server_time: "2026-09-01T09:00:00.000Z",
      devices: [
        { device_id: "a", device_name: "Desk", platform: "windows", reported_at: "2026-09-01T08:59:00.000Z", age_seconds: 60, collected_at: null, last_successful_upload_at: null, oldest_pending_at: "2026-09-01T05:00:00.000Z", pending_count: 12, permanent_failure_count: 2, recent_errors: [{ code: "invalid_event", message: "payload failed validation", occurred_at: "2026-09-01T08:00:00.000Z" }] },
      ],
    };
    const text = describeSyncDiagnostics(list).join("\n");
    expect(text).toMatch(/uncollected backlog exists/);
    expect(text).toMatch(/may still arrive/);
    expect(text).toMatch(/2 observation\(s\) were permanently rejected/);
    expect(text).toMatch(/will remain missing/);
    expect(text).toContain("invalid_event");
  });
});
