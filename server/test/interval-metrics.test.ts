import { describe, expect, it } from "vitest";
import { clipInterval, minutesFromMs, summedDurationMs, unionedDurationMs } from "../src/modules/metrics/interval-metrics.js";

const MIN = 60_000;

function interval(startMinutes: number, endMinutes: number) {
  return { startMs: startMinutes * MIN, endMs: endMinutes * MIN };
}

describe("usage interval metrics", () => {
  it("counts overlapping device time twice but active time once", () => {
    const intervals = [interval(0, 10), interval(5, 15)];
    expect(summedDurationMs(intervals)).toBe(20 * MIN);
    expect(unionedDurationMs(intervals)).toBe(15 * MIN);
  });

  it("counts identical concurrent intervals twice for device time", () => {
    const intervals = [interval(0, 10), interval(0, 10)];
    expect(summedDurationMs(intervals)).toBe(20 * MIN);
    expect(unionedDurationMs(intervals)).toBe(10 * MIN);
  });

  it("merges adjacent intervals without double counting active time", () => {
    const intervals = [interval(0, 10), interval(10, 20)];
    expect(summedDurationMs(intervals)).toBe(20 * MIN);
    expect(unionedDurationMs(intervals)).toBe(20 * MIN);
  });

  it("returns zero for empty input", () => {
    expect(summedDurationMs([])).toBe(0);
    expect(unionedDurationMs([])).toBe(0);
  });

  it("unions intervals regardless of input order", () => {
    const intervals = [interval(20, 30), interval(0, 10), interval(5, 25)];
    expect(unionedDurationMs(intervals)).toBe(30 * MIN);
  });

  it("clips intervals to the report range, dropping those outside it", () => {
    expect(clipInterval(-5 * MIN, 5 * MIN, 0, 10 * MIN)).toEqual({ startMs: 0, endMs: 5 * MIN });
    expect(clipInterval(5 * MIN, 15 * MIN, 0, 10 * MIN)).toEqual({ startMs: 5 * MIN, endMs: 10 * MIN });
    expect(clipInterval(0, 10 * MIN, 0, 10 * MIN)).toEqual({ startMs: 0, endMs: 10 * MIN });
    expect(clipInterval(10 * MIN, 20 * MIN, 0, 10 * MIN)).toBeNull();
    expect(clipInterval(0, 10 * MIN, 20 * MIN, 30 * MIN)).toBeNull();
  });

  it("keeps an interval that crosses the range boundary once, clipped to the range", () => {
    // A midnight-crossover interval contributes only its in-day portion.
    const clipped = clipInterval(-30 * MIN, 30 * MIN, 0, 60 * MIN);
    expect(clipped).toEqual({ startMs: 0, endMs: 30 * MIN });
    expect(minutesFromMs(summedDurationMs([clipped!]))).toBe(30);
    expect(minutesFromMs(unionedDurationMs([clipped!]))).toBe(30);
  });

  it("converts durations to whole minutes", () => {
    expect(minutesFromMs(90 * MIN)).toBe(90);
    expect(minutesFromMs(0)).toBe(0);
  });
});
