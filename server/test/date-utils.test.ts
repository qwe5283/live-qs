import { describe, expect, it } from "vitest";
import { datesBetweenInclusive, zonedDayRange } from "../src/shared/date-utils.js";

describe("date utilities", () => {
  it("creates inclusive date ranges", () => {
    expect(datesBetweenInclusive("2026-07-01", "2026-07-03")).toEqual(["2026-07-01", "2026-07-02", "2026-07-03"]);
  });

  it("resolves a calendar day in an IANA timezone", () => {
    const range = zonedDayRange("2026-07-28", "Asia/Shanghai");
    expect(range?.start.toISOString()).toBe("2026-07-27T16:00:00.000Z");
    expect(range?.end.toISOString()).toBe("2026-07-28T16:00:00.000Z");
  });
});
