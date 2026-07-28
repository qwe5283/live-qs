const SENSITIVE_KEY_PATTERN = /(authorization|token|secret|password|window_title|notification_body|raw|payload|body)/i;

export function sanitizeAuditDetails(value: unknown): unknown {
  if (value === null) return null;
  if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") return value;
  if (Array.isArray(value)) return value.slice(0, 50).map((item) => sanitizeAuditDetails(item));
  if (typeof value !== "object") return null;

  const output: Record<string, unknown> = {};
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    if (SENSITIVE_KEY_PATTERN.test(key)) {
      output[key] = "[redacted]";
      continue;
    }
    output[key] = sanitizeAuditDetails(child);
  }
  return output;
}
