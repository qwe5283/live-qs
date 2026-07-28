export function eventFingerprint(type: string, data: Record<string, unknown>, value?: number | null, unit?: string | null): string {
  return JSON.stringify({
    type,
    value: value ?? null,
    unit: unit ?? null,
    app_id: typeof data.app_id === "string" ? data.app_id : "",
    app_name: typeof data.app_name === "string" ? data.app_name : "",
    title_hash: typeof data.title_hash === "string" ? data.title_hash : "",
    is_afk: typeof data.is_afk === "boolean" ? data.is_afk : null,
  });
}

export function durationMs(startAt: Date, endAt: Date): number {
  return Math.max(0, endAt.getTime() - startAt.getTime());
}

export function shouldMergeHeartbeat(
  latest: { type: string; startAt: Date; endAt: Date | null; fingerprint: string } | null,
  incoming: { type: string; timestamp: Date; heartbeatIntervalMs: number; fingerprint: string },
): boolean {
  if (!latest || latest.type !== incoming.type || latest.fingerprint !== incoming.fingerprint) return false;
  const gapMs = incoming.timestamp.getTime() - (latest.endAt ?? latest.startAt).getTime();
  return gapMs >= 0 && gapMs <= incoming.heartbeatIntervalMs * 2.5;
}

export function closeOpenEventAt(latestStartAt: Date, incomingTimestamp: Date): Date {
  return new Date(Math.max(latestStartAt.getTime(), incomingTimestamp.getTime() - 1));
}
