export interface RetentionPolicy {
  screenDays: number;
  healthDays: number;
  paymentDays: number;
  defaultDays: number;
}

export const DEFAULT_RETENTION_POLICY: RetentionPolicy = {
  screenDays: 90,
  healthDays: 365,
  paymentDays: 3650,
  defaultDays: 365,
};

export function retentionCutoffs(now: Date, policy: RetentionPolicy = DEFAULT_RETENTION_POLICY) {
  return {
    screenBefore: daysAgo(now, policy.screenDays),
    healthBefore: daysAgo(now, policy.healthDays),
    paymentBefore: daysAgo(now, policy.paymentDays),
    defaultBefore: daysAgo(now, policy.defaultDays),
  };
}

export function parsePositiveInt(value: string | null, fallback: number, min: number, max: number): number {
  const parsed = Number.parseInt(value ?? "", 10);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.max(min, Math.min(max, parsed));
}

function daysAgo(now: Date, days: number): string {
  return new Date(now.getTime() - days * 24 * 60 * 60 * 1000).toISOString();
}
