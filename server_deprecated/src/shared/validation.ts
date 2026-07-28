const MAX_DATA_JSON_BYTES = 8 * 1024;

export function jsonObject(value: unknown): Record<string, unknown> {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function parseJsonString(value: string): Record<string, unknown> {
  try {
    return jsonObject(JSON.parse(value));
  } catch {
    return {};
  }
}

export function safeDataJson(data: Record<string, unknown>): string {
  const json = JSON.stringify(data);
  if (new TextEncoder().encode(json).length > MAX_DATA_JSON_BYTES) {
    throw new Error("data too large");
  }
  return json;
}

export function eventType(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (!/^[a-z][a-z0-9_.-]{1,80}$/.test(trimmed)) return null;
  return trimmed;
}

export function bucketId(value: unknown, fallback: string): string {
  if (typeof value !== "string") return fallback;
  const trimmed = value.trim();
  if (!trimmed || trimmed.length > 160) return fallback;
  return trimmed;
}

export function isoTime(value: unknown): Date | null {
  if (typeof value !== "string" || !value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function recentIsoTime(value: unknown): Date {
  const parsed = isoTime(value);
  const now = new Date();
  if (!parsed) return now;
  return Math.abs(parsed.getTime() - now.getTime()) > 5 * 60 * 1000 ? now : parsed;
}

export function bucketParts(bucket: string, fallbackType: string): { source: string; bucketType: string } {
  const parts = bucket.split(":").filter(Boolean);
  return {
    source: parts[0] || "unknown",
    bucketType: parts.length >= 3 ? parts.slice(2).join(":") : fallbackType,
  };
}
