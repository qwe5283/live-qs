import { describe, expect, it } from "vitest";
import { eventFingerprint, shouldMergeHeartbeat } from "../src/shared/event-merge.js";

describe("shouldMergeHeartbeat", () => {
  it("merges matching state within the heartbeat window", () => {
    const fingerprint = eventFingerprint("app.foreground", { app_id: "code.exe", is_afk: false });
    expect(shouldMergeHeartbeat(
      { type: "app.foreground", startAt: new Date("2026-07-28T10:00:00Z"), endAt: new Date("2026-07-28T10:00:10Z"), fingerprint },
      { type: "app.foreground", timestamp: new Date("2026-07-28T10:00:20Z"), heartbeatIntervalMs: 10_000, fingerprint },
    )).toBe(true);
  });

  it("does not merge changed application state", () => {
    expect(shouldMergeHeartbeat(
      { type: "app.foreground", startAt: new Date(), endAt: null, fingerprint: "a" },
      { type: "app.foreground", timestamp: new Date(), heartbeatIntervalMs: 10_000, fingerprint: "b" },
    )).toBe(false);
  });
});
