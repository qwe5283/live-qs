/**
 * Pure usage-metric math for historical intervals: clipping to a report range,
 * summed device time (concurrent use can exceed elapsed time), and unioned
 * active time (overlapping or adjacent time counts once).
 */
export interface ClippedInterval {
  startMs: number;
  endMs: number;
}

/** Clips one interval to the report range; returns null when it does not overlap the range. */
export function clipInterval(startMs: number, endMs: number, rangeStartMs: number, rangeEndMs: number): ClippedInterval | null {
  const startMsClipped = Math.max(startMs, rangeStartMs);
  const endMsClipped = Math.min(endMs, rangeEndMs);
  return endMsClipped > startMsClipped ? { startMs: startMsClipped, endMs: endMsClipped } : null;
}

/** Device time: every interval duration is added, so concurrent use may exceed elapsed time. */
export function summedDurationMs(intervals: ClippedInterval[]): number {
  return intervals.reduce((total, interval) => total + (interval.endMs - interval.startMs), 0);
}

/** Active time: sorts, then merges overlapping or adjacent intervals so shared time counts once. */
export function unionedDurationMs(intervals: ClippedInterval[]): number {
  const sorted = [...intervals].sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs);
  let totalMs = 0;
  let current: ClippedInterval | null = null;
  for (const interval of sorted) {
    if (current && interval.startMs <= current.endMs) {
      current.endMs = Math.max(current.endMs, interval.endMs);
      continue;
    }
    if (current) totalMs += current.endMs - current.startMs;
    current = { ...interval };
  }
  if (current) totalMs += current.endMs - current.startMs;
  return totalMs;
}

export function minutesFromMs(durationMs: number): number {
  return Math.round(durationMs / 60_000);
}
