import { z } from "zod";

/** Event types registered in contracts/schemas/events; unknown types are rejected at ingest and never mapped. */
export const REGISTERED_EVENT_TYPES = ["activity.interval"] as const;

/**
 * The contract forbids full executable paths in `application_id`; it must stay
 * an opaque executable or package name. Path separators and drive-letter
 * prefixes are the rejected shapes.
 */
function isOpaqueApplicationId(value: string): boolean {
  return !value.includes("/") && !value.includes("\\") && !/^[a-zA-Z]:/.test(value);
}

const activityIntervalPayloadSchema = z.strictObject({
  application_id: z
    .string()
    .min(1)
    .refine(isOpaqueApplicationId, "application_id must be an executable or package name, never a path."),
  application_label: z.string().min(1).optional(),
  subject_id: z.string().min(1).optional(),
  is_afk: z.boolean(),
  duration: z.strictObject({
    value: z.number().int().min(0),
    unit: z.literal("ms"),
  }),
  classification: z
    .strictObject({
      rule_id: z.string().min(1),
      rule_version: z.number().int().min(1),
      confidence: z.number().min(0).max(1),
    })
    .optional(),
});

export interface RegisteredEventEnvelope {
  eventType: (typeof REGISTERED_EVENT_TYPES)[number];
  schemaVersion: number;
  startAt: Date;
  endAt: Date | null;
  finalizationState: "checkpoint" | "final";
  payload: Record<string, unknown>;
}

const payloadSchemas = new Map<string, z.ZodType>([
  ["activity.interval@1", activityIntervalPayloadSchema],
]);

/**
 * Validates one event against its registered payload schema (units, required
 * fields, forbidden fields) plus the interval semantics the schema binds to the
 * envelope: a finalized event needs `end_at`, and whenever `end_at` is present
 * it must not precede `start_at` and `duration` must equal the interval bounds
 * within one millisecond of rounding tolerance.
 */
export function validateRegisteredEvent(envelope: RegisteredEventEnvelope): string | null {
  const schema = payloadSchemas.get(`${envelope.eventType}@${envelope.schemaVersion}`);
  if (!schema) return "The event payload has no registered schema.";
  const parsed = schema.safeParse(envelope.payload);
  if (!parsed.success) {
    const issue = parsed.error.issues.at(0);
    if (!issue) return `The event payload is not valid for ${envelope.eventType} v${envelope.schemaVersion}.`;
    const path = issue.path.length > 0 ? `${issue.path.join(".")}: ` : "";
    return `The event payload is not valid for ${envelope.eventType} v${envelope.schemaVersion}. ${path}${issue.message}`;
  }
  if (envelope.finalizationState === "final" && envelope.endAt === null) {
    return "A finalized event must carry end_at.";
  }
  if (envelope.endAt !== null) {
    if (envelope.endAt.getTime() < envelope.startAt.getTime()) {
      return "end_at must not precede start_at.";
    }
    const boundsMs = envelope.endAt.getTime() - envelope.startAt.getTime();
    const duration = (envelope.payload as { duration?: { value?: unknown } }).duration?.value;
    if (typeof duration === "number" && Math.abs(duration - boundsMs) > 1) {
      return "payload.duration must equal end_at - start_at.";
    }
  }
  return null;
}
