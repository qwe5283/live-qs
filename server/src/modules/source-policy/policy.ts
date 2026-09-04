import type { SourceConflict, SourcePolicyEntry } from "../../generated/contract-models.js";

/**
 * Metric keys carrying an explicit, versioned source priority. Usage and
 * payment metrics prioritize source kinds; health metrics prioritize Health
 * Connect data origins, because every health observation shares the single
 * android.healthconnect source kind and competes by writing application.
 */
export const USAGE_APP_MINUTES = "usage.app_minutes";
export const HEALTH_STEP_TOTAL = "health.step_total";
export const HEALTH_SLEEP_MINUTES = "health.sleep_minutes";
export const HEALTH_HEARTRATE_AVERAGE = "health.heartrate_average";
export const PAYMENT_TRANSACTION_TOTALS = "payment.transaction_totals";

export const POLICY_METRICS = [
  USAGE_APP_MINUTES,
  HEALTH_STEP_TOTAL,
  HEALTH_SLEEP_MINUTES,
  HEALTH_HEARTRATE_AVERAGE,
  PAYMENT_TRANSACTION_TOTALS,
] as const;

export type PolicyMetric = (typeof POLICY_METRICS)[number];

/** Health metrics rank data origins; every other metric ranks source kinds. */
export const HEALTH_METRICS: readonly string[] = [HEALTH_STEP_TOTAL, HEALTH_SLEEP_MINUTES, HEALTH_HEARTRATE_AVERAGE];

/** Maps each health event type to the metric key carrying its origin priority. */
export const HEALTH_METRIC_FOR_EVENT_TYPE: Record<string, string> = {
  "health.step.sample": HEALTH_STEP_TOTAL,
  "health.sleep.session": HEALTH_SLEEP_MINUTES,
  "health.heartrate.sample": HEALTH_HEARTRATE_AVERAGE,
};

/** The version applied until the Owner changes the policy. */
export const DEFAULT_SOURCE_POLICY_VERSION = 1;

/**
 * Default priorities, documented in the contract: Android UsageStats is
 * authoritative for daily application totals (accessibility observations
 * support current and contextual activity only), and payment totals prefer
 * the structured notification source. Health metrics start without an origin
 * preference: every origin ranks by name deterministically and nothing is
 * ever deleted.
 */
export const DEFAULT_SOURCE_POLICY_ENTRIES: SourcePolicyEntry[] = [
  { metric: USAGE_APP_MINUTES, priority: ["windows.foreground", "android.usagestats", "android.accessibility"] },
  { metric: HEALTH_STEP_TOTAL, priority: [] },
  { metric: HEALTH_SLEEP_MINUTES, priority: [] },
  { metric: HEALTH_HEARTRATE_AVERAGE, priority: [] },
  { metric: PAYMENT_TRANSACTION_TOTALS, priority: ["android.wechatpay"] },
];

/** Server-side policy state; `updatedAt` is null while the default applies. */
export interface SourcePolicyState {
  version: number;
  entries: SourcePolicyEntry[];
  updatedAt: Date | null;
}

/** The default policy document; the live default version stays at 1. */
export function defaultPolicyState(): SourcePolicyState {
  return { version: DEFAULT_SOURCE_POLICY_VERSION, entries: DEFAULT_SOURCE_POLICY_ENTRIES.map((entry) => ({ ...entry, priority: [...entry.priority] })), updatedAt: null };
}

/**
 * Ranks the observed sources for one metric: listed priorities first in
 * policy order, then unlisted sources by name, so the ranking is total and
 * deterministic even when observations bring a source the policy never named.
 */
export function rankSources(priority: string[], observed: string[]): string[] {
  const listed = priority.filter((source) => observed.includes(source));
  const unlisted = observed.filter((source) => !priority.includes(source)).sort((a, b) => a.localeCompare(b));
  return [...listed, ...unlisted];
}

/** The effective priority for one metric; omitted metrics keep the default. */
export function priorityFor(policy: SourcePolicyState, metric: string): string[] {
  return policy.entries.find((entry) => entry.metric === metric)?.priority
    ?? DEFAULT_SOURCE_POLICY_ENTRIES.find((entry) => entry.metric === metric)?.priority
    ?? [];
}

export interface ActivityObservation {
  id: string;
  deviceId: string;
  sourceKind: string;
  startMs: number;
  endMs: number | null;
}

/** An observation with a known positive duration (open checkpoints excluded). */
export type TimedActivityObservation = ActivityObservation & { endMs: number };

function hasDuration(observation: ActivityObservation): observation is TimedActivityObservation {
  return observation.endMs !== null && observation.endMs > observation.startMs;
}

interface ConflictCluster {
  startMs: number;
  endMs: number;
  members: TimedActivityObservation[];
}

/** Merges overlapping or touching intervals into maximal time clusters. */
function mergeClusters(intervals: TimedActivityObservation[]): ConflictCluster[] {
  const sorted = [...intervals].sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs || a.id.localeCompare(b.id));
  const clusters: ConflictCluster[] = [];
  for (const interval of sorted) {
    const last = clusters.at(-1);
    if (last && interval.startMs <= last.endMs) {
      last.endMs = Math.max(last.endMs, interval.endMs);
      last.members.push(interval);
    } else {
      clusters.push({ startMs: interval.startMs, endMs: interval.endMs, members: [interval] });
    }
  }
  return clusters;
}

function overlap(first: { startMs: number; endMs: number }, second: { startMs: number; endMs: number }): boolean {
  return first.startMs < second.endMs && second.startMs < first.endMs;
}

/**
 * Per-device source selection for daily application totals: the
 * highest-ranked source kind observed on a device is authoritative for the
 * whole day, and intervals of competing kinds are withheld from the normalized
 * result — never deleted. Every withheld cluster becomes a conflict entry
 * referencing the source event identifiers of both sides, so the Owner can see
 * exactly which observations compete and what the policy chose.
 */
export function selectActivityObservations(
  observations: ActivityObservation[],
  priority: string[],
  policyVersion: number,
  range?: { fromMs: number; toMs: number },
): { selected: ActivityObservation[]; conflicts: SourceConflict[] } {
  const byDevice = new Map<string, ActivityObservation[]>();
  for (const observation of observations) {
    const lanes = byDevice.get(observation.deviceId) ?? [];
    lanes.push(observation);
    byDevice.set(observation.deviceId, lanes);
  }

  const selected: ActivityObservation[] = [];
  const conflicts: SourceConflict[] = [];
  for (const deviceId of [...byDevice.keys()].sort((a, b) => a.localeCompare(b))) {
    const lanes = byDevice.get(deviceId)!;
    const winner = rankSources(priority, [...new Set(lanes.map((lane) => lane.sourceKind))])[0]!;
    const winning = lanes.filter((lane) => lane.sourceKind === winner);
    selected.push(...winning);

    const withheld = lanes.filter((lane) => lane.sourceKind !== winner).filter(hasDuration);
    const winningClosed = winning.filter(hasDuration);
    for (const cluster of mergeClusters(withheld)) {
      const fromMs = Math.max(cluster.startMs, range?.fromMs ?? cluster.startMs);
      const toMs = Math.min(cluster.endMs, range?.toMs ?? cluster.endMs);
      if (fromMs >= toMs) continue;
      const window = { startMs: fromMs, endMs: toMs };
      conflicts.push({
        metric: USAGE_APP_MINUTES,
        policy_version: policyVersion,
        selected_source: winner,
        selected_event_ids: winningClosed.filter((lane) => overlap(lane as { startMs: number; endMs: number }, window)).map((lane) => lane.id).sort(),
        competing_sources: [...new Set(cluster.members.map((member) => member.sourceKind))].sort((a, b) => a.localeCompare(b)),
        competing_event_ids: cluster.members.map((member) => member.id).sort(),
        from: new Date(fromMs).toISOString(),
        to: new Date(toMs).toISOString(),
      });
    }
  }
  conflicts.sort((a, b) => a.from.localeCompare(b.from) || a.to.localeCompare(b.to) || a.selected_source.localeCompare(b.selected_source));
  return { selected, conflicts };
}

export interface HealthObservation {
  id: string;
  metric: string;
  origin: string;
  startMs: number;
  /** Equals startMs for instantaneous samples. */
  endMs: number;
}

function buildConflict(
  metric: string,
  policyVersion: number,
  selectedSource: string,
  selected: HealthObservation[],
  competing: HealthObservation[],
  fromMs: number,
  toMs: number,
): SourceConflict {
  return {
    metric,
    policy_version: policyVersion,
    selected_source: selectedSource,
    selected_event_ids: selected.map((observation) => observation.id).sort(),
    competing_sources: [...new Set(competing.map((observation) => observation.origin))].sort((a, b) => a.localeCompare(b)),
    competing_event_ids: competing.map((observation) => observation.id).sort(),
    from: new Date(fromMs).toISOString(),
    to: new Date(toMs).toISOString(),
  };
}

/**
 * Health Connect multi-origin selection: interval observations (steps, sleep)
 * from different origins overlapping in time compete for the normalized
 * summary, and the policy picks one origin per merged conflict window.
 * Instantaneous samples compete only at the same instant. Disjoint origins
 * are complementary and never conflict. Competing observations stay retained;
 * their identifiers are returned so callers can withhold them from normalized
 * totals while keeping every observation individually readable.
 */
export function selectHealthObservations(
  observations: HealthObservation[],
  prioritiesByMetric: Partial<Record<string, string[]>>,
  policyVersion: number,
): { withheldIds: Set<string>; conflicts: SourceConflict[] } {
  const withheldIds = new Set<string>();
  const conflicts: SourceConflict[] = [];
  const byMetric = new Map<string, HealthObservation[]>();
  for (const observation of observations) {
    const group = byMetric.get(observation.metric) ?? [];
    group.push(observation);
    byMetric.set(observation.metric, group);
  }

  for (const metric of [...byMetric.keys()].sort((a, b) => a.localeCompare(b))) {
    const group = byMetric.get(metric)!;
    const priority = prioritiesByMetric[metric] ?? [];

    const intervals = group.filter((observation) => observation.endMs > observation.startMs);
    const boundaries = [...new Set(intervals.flatMap((observation) => [observation.startMs, observation.endMs]))].sort((a, b) => a - b);
    const windows: Array<{ startMs: number; endMs: number }> = [];
    for (let index = 0; index < boundaries.length - 1; index++) {
      const startMs = boundaries[index]!;
      const endMs = boundaries[index + 1]!;
      const active = intervals.filter((observation) => observation.startMs <= startMs && observation.endMs >= endMs);
      if (new Set(active.map((observation) => observation.origin)).size >= 2) {
        const last = windows.at(-1);
        if (last && startMs <= last.endMs) last.endMs = endMs;
        else windows.push({ startMs, endMs });
      }
    }
    for (const window of windows) {
      const overlapping = intervals.filter((observation) => overlap(observation, window));
      const origins = [...new Set(overlapping.map((observation) => observation.origin))];
      const winner = rankSources(priority, origins)[0]!;
      const selected = overlapping.filter((observation) => observation.origin === winner);
      const competing = overlapping.filter((observation) => observation.origin !== winner);
      competing.forEach((observation) => withheldIds.add(observation.id));
      conflicts.push(buildConflict(metric, policyVersion, winner, selected, competing, window.startMs, window.endMs));
    }

    const instants = new Map<number, HealthObservation[]>();
    for (const observation of group.filter((candidate) => candidate.endMs === candidate.startMs)) {
      const sameInstant = instants.get(observation.startMs) ?? [];
      sameInstant.push(observation);
      instants.set(observation.startMs, sameInstant);
    }
    for (const instantMs of [...instants.keys()].sort((a, b) => a - b)) {
      const sameInstant = instants.get(instantMs)!;
      const origins = [...new Set(sameInstant.map((observation) => observation.origin))];
      if (origins.length < 2) continue;
      const winner = rankSources(priority, origins)[0]!;
      const selected = sameInstant.filter((observation) => observation.origin === winner);
      const competing = sameInstant.filter((observation) => observation.origin !== winner);
      competing.forEach((observation) => withheldIds.add(observation.id));
      conflicts.push(buildConflict(metric, policyVersion, winner, selected, competing, instantMs, instantMs));
    }
  }

  conflicts.sort((a, b) => a.from.localeCompare(b.from) || a.metric.localeCompare(b.metric));
  return { withheldIds, conflicts };
}
