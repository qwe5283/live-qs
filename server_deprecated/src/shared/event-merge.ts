export interface MergeCandidate {
  type: string;
  startAt: string;
  endAt: string | null;
  fingerprint: string;
}

export interface IncomingEventState {
  type: string;
  timestamp: Date;
  heartbeatIntervalMs: number;
  fingerprint: string;
}

export function eventFingerprint(
  type: string,
  data: Record<string, unknown>,
  value?: number | null,
  unit?: string | null,
): string {
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

export function durationMs(startAt: string, endAt: string): number {
  return Math.max(0, new Date(endAt).getTime() - new Date(startAt).getTime());
}

export function shouldMergeHeartbeat(latest: MergeCandidate | null, incoming: IncomingEventState): boolean {
  if (!latest) return false;
  if (latest.type !== incoming.type) return false;
  if (latest.fingerprint !== incoming.fingerprint) return false;

  const latestEnd = new Date(latest.endAt || latest.startAt).getTime();
  if (Number.isNaN(latestEnd)) return false;

  const gapMs = incoming.timestamp.getTime() - latestEnd;
  return gapMs >= 0 && gapMs <= incoming.heartbeatIntervalMs * 2.5;
}

export function closeOpenEventAt(latestStartAt: string, incomingTimestamp: Date): string {
  return new Date(Math.max(new Date(latestStartAt).getTime(), incomingTimestamp.getTime() - 1)).toISOString();
}
