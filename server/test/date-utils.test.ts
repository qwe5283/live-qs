import { describe, expect, it } from "vitest";
import { datesBetweenInclusive, isValidTimezone, zonedDayRange, zonedWeekRange } from "../src/shared/date-utils.js";

describe("date utilities", () => {
  it("creates inclusive date ranges", () => {
    expect(datesBetweenInclusive("2026-07-01", "2026-07-03")).toEqual(["2026-07-01", "2026-07-02", "2026-07-03"]);
  });

  it("resolves a calendar day in an IANA timezone", () => {
    const range = zonedDayRange("2026-07-28", "Asia/Shanghai");
    expect(range?.start.toISOString()).toBe("2026-07-27T16:00:00.000Z");
    expect(range?.end.toISOString()).toBe("2026-07-28T16:00:00.000Z");
  });

  it("validates IANA timezones", () => {
    expect(isValidTimezone("Asia/Shanghai")).toBe(true);
    expect(isValidTimezone("America/New_York")).toBe(true);
    expect(isValidTimezone("UTC")).toBe(true);
    expect(isValidTimezone("Mars/Olympus")).toBe(false);
    expect(isValidTimezone("")).toBe(false);
  });

  it("resolves a Monday-start week in a fixed-offset timezone", () => {
    // 2026-07-28 is a Tuesday; its week runs Monday 2026-07-27 through Sunday 2026-08-02.
    const range = zonedWeekRange("2026-07-28", "Asia/Shanghai");
    expect(range?.start.toISOString()).toBe("2026-07-26T16:00:00.000Z");
    expect(range?.end.toISOString()).toBe("2026-08-02T16:00:00.000Z");
  });

  it("resolves a 23-hour day on the DST spring-forward boundary", () => {
    const range = zonedDayRange("2026-03-08", "America/New_York");
    expect(range?.start.toISOString()).toBe("2026-03-08T05:00:00.000Z");
    expect(range?.end.toISOString()).toBe("2026-03-09T04:00:00.000Z");
  });

  it("resolves a 25-hour day on the DST fall-back boundary", () => {
    const range = zonedDayRange("2026-11-01", "America/New_York");
    expect(range?.start.toISOString()).toBe("2026-11-01T04:00:00.000Z");
    expect(range?.end.toISOString()).toBe("2026-11-02T05:00:00.000Z");
  });

  it("resolves weeks spanning DST transitions", () => {
    const springWeek = zonedWeekRange("2026-03-08", "America/New_York");
    expect(springWeek?.start.toISOString()).toBe("2026-03-02T05:00:00.000Z");
    expect(springWeek?.end.toISOString()).toBe("2026-03-09T04:00:00.000Z");
    const fallWeek = zonedWeekRange("2026-11-01", "America/New_York");
    expect(fallWeek?.start.toISOString()).toBe("2026-10-26T04:00:00.000Z");
    expect(fallWeek?.end.toISOString()).toBe("2026-11-02T05:00:00.000Z");
  });

  it("rejects unknown timezones and invalid dates", () => {
    expect(zonedWeekRange("2026-07-28", "Not/AZone")).toBeNull();
    expect(zonedWeekRange("2026-02-30", "UTC")).toBeNull();
    expect(zonedDayRange("2026-02-30", "UTC")).toBeNull();
  });
});
