import { describe, expect, it } from "vitest";
import {
  DEFAULT_SOURCE_POLICY_ENTRIES,
  HEALTH_SLEEP_MINUTES,
  HEALTH_STEP_TOTAL,
  USAGE_APP_MINUTES,
  priorityFor,
  rankSources,
  selectActivityObservations,
  selectHealthObservations,
} from "../src/modules/source-policy/policy.js";
import type { ActivityObservation, HealthObservation } from "../src/modules/source-policy/policy.js";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY_START = Date.parse("2026-09-01T00:00:00.000Z");

/** Deterministic Android app-duration sample: UsageStats session plus accessibility observations of the same device. */
function activity(overrides: Partial<ActivityObservation> & Pick<ActivityObservation, "id" | "deviceId" | "sourceKind" | "startMs">): ActivityObservation {
  return { endMs: null, ...overrides };
}

function health(overrides: Partial<HealthObservation> & Pick<HealthObservation, "id" | "metric" | "origin" | "startMs" | "endMs">): HealthObservation {
  return { ...overrides };
}

describe("rankSources", () => {
  it("ranks listed sources first in policy order, then unlisted sources by name", () => {
    expect(rankSources(["b", "a"], ["c", "a", "b"])).toEqual(["b", "a", "c"]);
    expect(rankSources([], ["zeta", "alpha"])).toEqual(["alpha", "zeta"]);
    // A listed source that never observed anything does not appear in the ranking.
    expect(rankSources(["windows.foreground"], ["android.usagestats"])).toEqual(["android.usagestats"]);
  });
});

describe("selectActivityObservations", () => {
  const priority = DEFAULT_SOURCE_POLICY_ENTRIES.find((entry) => entry.metric === USAGE_APP_MINUTES)!.priority;

  it("selects the authoritative kind per device and withholds competing kinds without deleting them", () => {
    const usagestats = activity({ id: "u1", deviceId: "pixel-8", sourceKind: "android.usagestats", startMs: DAY_START + 10 * HOUR, endMs: DAY_START + 10.5 * HOUR });
    const accessibilityOverlap = activity({ id: "a1", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.1 * HOUR, endMs: DAY_START + 10.2 * HOUR });
    const accessibilityLater = activity({ id: "a2", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 11 * HOUR, endMs: DAY_START + 11.25 * HOUR });
    const windows = activity({ id: "w1", deviceId: "desktop", sourceKind: "windows.foreground", startMs: DAY_START + HOUR, endMs: DAY_START + 2 * HOUR });

    const { selected, conflicts } = selectActivityObservations([usagestats, accessibilityOverlap, accessibilityLater, windows], priority, 1);

    expect(selected.map((row) => row.id).sort()).toEqual(["u1", "w1"]);
    expect(conflicts).toHaveLength(2);
    expect(conflicts[0]).toMatchObject({
      metric: USAGE_APP_MINUTES,
      policy_version: 1,
      selected_source: "android.usagestats",
      competing_sources: ["android.accessibility"],
      selected_event_ids: ["u1"],
      competing_event_ids: ["a1"],
      from: new Date(DAY_START + 10.1 * HOUR).toISOString(),
      to: new Date(DAY_START + 10.2 * HOUR).toISOString(),
    });
    // The non-overlapping accessibility interval is still withheld from daily
    // totals (the policy is authoritative for the whole day) and reported.
    expect(conflicts[1]).toMatchObject({
      selected_event_ids: [],
      competing_event_ids: ["a2"],
      from: new Date(DAY_START + 11 * HOUR).toISOString(),
      to: new Date(DAY_START + 11.25 * HOUR).toISOString(),
    });
  });

  it("merges overlapping withheld intervals into one conflict window", () => {
    const usagestats = activity({ id: "u1", deviceId: "pixel-8", sourceKind: "android.usagestats", startMs: DAY_START + 10 * HOUR, endMs: DAY_START + 11 * HOUR });
    const first = activity({ id: "a1", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.1 * HOUR, endMs: DAY_START + 10.2 * HOUR });
    const second = activity({ id: "a2", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.15 * HOUR, endMs: DAY_START + 10.4 * HOUR });

    const { conflicts } = selectActivityObservations([usagestats, first, second], priority, 1);

    expect(conflicts).toHaveLength(1);
    expect(conflicts[0]).toMatchObject({
      competing_event_ids: ["a1", "a2"],
      selected_event_ids: ["u1"],
      from: new Date(DAY_START + 10.1 * HOUR).toISOString(),
      to: new Date(DAY_START + 10.4 * HOUR).toISOString(),
    });
  });

  it("ignores open checkpoints and zero-length intervals as conflict candidates", () => {
    const usagestats = activity({ id: "u1", deviceId: "pixel-8", sourceKind: "android.usagestats", startMs: DAY_START + 10 * HOUR, endMs: DAY_START + 10.5 * HOUR });
    const openAccessibility = activity({ id: "a1", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.1 * HOUR, endMs: null });
    const zeroLength = activity({ id: "a2", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.3 * HOUR, endMs: DAY_START + 10.3 * HOUR });

    const { selected, conflicts } = selectActivityObservations([usagestats, openAccessibility, zeroLength], priority, 1);

    expect(selected.map((row) => row.id)).toEqual(["u1"]);
    expect(conflicts).toEqual([]);
  });

  it("keeps devices independent: a policy flip on one device leaves the other lane intact", () => {
    const inverted = ["android.accessibility", "android.usagestats", "windows.foreground"];
    const androidUsagestats = activity({ id: "u1", deviceId: "pixel-8", sourceKind: "android.usagestats", startMs: DAY_START + 10 * HOUR, endMs: DAY_START + 10.5 * HOUR });
    const androidAccessibility = activity({ id: "a1", deviceId: "pixel-8", sourceKind: "android.accessibility", startMs: DAY_START + 10.1 * HOUR, endMs: DAY_START + 10.6 * HOUR });
    const windows = activity({ id: "w1", deviceId: "desktop", sourceKind: "windows.foreground", startMs: DAY_START + HOUR, endMs: DAY_START + 2 * HOUR });

    const { selected, conflicts } = selectActivityObservations([androidUsagestats, androidAccessibility, windows], inverted, 2);

    expect(selected.map((row) => row.id).sort()).toEqual(["a1", "w1"]);
    expect(conflicts).toHaveLength(1);
    expect(conflicts[0]).toMatchObject({
      policy_version: 2,
      selected_source: "android.accessibility",
      competing_sources: ["android.usagestats"],
      selected_event_ids: ["a1"],
      competing_event_ids: ["u1"],
    });
  });
});

describe("selectHealthObservations", () => {
  const sleep = (id: string, origin: string, startHour: number, endHour: number): HealthObservation =>
    health({ id, metric: HEALTH_SLEEP_MINUTES, origin, startMs: DAY_START + startHour * HOUR, endMs: DAY_START + endHour * HOUR });

  it("reports overlapping multi-origin sleep as one conflict and picks by deterministic origin rank", () => {
    const fit = sleep("s1", "com.google.android.apps.fitness", 23, 31);
    const mi = sleep("s2", "com.mi.health", 23.5, 30);

    const { withheldIds, conflicts } = selectHealthObservations([fit, mi], { [HEALTH_SLEEP_MINUTES]: [] }, 1);

    expect(conflicts).toHaveLength(1);
    expect(conflicts[0]).toMatchObject({
      metric: HEALTH_SLEEP_MINUTES,
      policy_version: 1,
      selected_source: "com.google.android.apps.fitness",
      competing_sources: ["com.mi.health"],
      selected_event_ids: ["s1"],
      competing_event_ids: ["s2"],
      from: new Date(DAY_START + 23.5 * HOUR).toISOString(),
      to: new Date(DAY_START + 30 * HOUR).toISOString(),
    });
    expect(withheldIds).toEqual(new Set(["s2"]));
  });

  it("honors an explicit origin priority over the name order", () => {
    const fit = sleep("s1", "com.google.android.apps.fitness", 23, 31);
    const mi = sleep("s2", "com.mi.health", 23.5, 30);

    const { withheldIds, conflicts } = selectHealthObservations([fit, mi], { [HEALTH_SLEEP_MINUTES]: ["com.mi.health"] }, 3);

    expect(conflicts[0]).toMatchObject({
      policy_version: 3,
      selected_source: "com.mi.health",
      competing_sources: ["com.google.android.apps.fitness"],
      selected_event_ids: ["s2"],
      competing_event_ids: ["s1"],
    });
    expect(withheldIds).toEqual(new Set(["s1"]));
  });

  it("keeps disjoint multi-origin observations out of conflict entirely", () => {
    const evening = sleep("s1", "com.google.android.apps.fitness", 23, 26);
    const nap = sleep("s2", "com.mi.health", 30, 31);

    const { withheldIds, conflicts } = selectHealthObservations([evening, nap], { [HEALTH_SLEEP_MINUTES]: [] }, 1);

    expect(conflicts).toEqual([]);
    expect(withheldIds.size).toBe(0);
  });

  it("flags instant heart-rate samples from different origins at the same instant", () => {
    const watch = health({ id: "h1", metric: HEALTH_STEP_TOTAL, origin: "com.mi.health", startMs: DAY_START + 5 * HOUR, endMs: DAY_START + 5 * HOUR });
    const phone = health({ id: "h2", metric: HEALTH_STEP_TOTAL, origin: "com.google.android.apps.fitness", startMs: DAY_START + 5 * HOUR, endMs: DAY_START + 5 * HOUR });
    const later = health({ id: "h3", metric: HEALTH_STEP_TOTAL, origin: "com.mi.health", startMs: DAY_START + 6 * HOUR, endMs: DAY_START + 6 * HOUR });

    const { conflicts } = selectHealthObservations([watch, phone, later], { [HEALTH_STEP_TOTAL]: [] }, 1);

    expect(conflicts).toHaveLength(1);
    expect(conflicts[0]).toMatchObject({
      metric: HEALTH_STEP_TOTAL,
      selected_source: "com.google.android.apps.fitness",
      selected_event_ids: ["h2"],
      competing_event_ids: ["h1"],
      from: new Date(DAY_START + 5 * HOUR).toISOString(),
      to: new Date(DAY_START + 5 * HOUR).toISOString(),
    });
  });
});

describe("priorityFor", () => {
  it("falls back to the default priority when the policy omits the metric", () => {
    const usageDefault = DEFAULT_SOURCE_POLICY_ENTRIES.find((entry) => entry.metric === USAGE_APP_MINUTES)!.priority;
    expect(priorityFor({ version: 1, entries: [], updatedAt: null }, USAGE_APP_MINUTES)).toEqual(usageDefault);
    expect(priorityFor({ version: 1, entries: [{ metric: HEALTH_SLEEP_MINUTES, priority: ["com.mi.health"] }], updatedAt: null }, HEALTH_SLEEP_MINUTES)).toEqual(["com.mi.health"]);
  });
});
