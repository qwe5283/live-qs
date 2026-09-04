import { z } from "zod";

/** Event types registered in contracts/schemas/events; unknown types are rejected at ingest and never mapped. */
export const REGISTERED_EVENT_TYPES = [
  "activity.interval",
  "health.heartrate.sample",
  "health.sleep.session",
  "health.step.sample",
  "payment.transaction",
] as const;

export type RegisteredEventType = (typeof REGISTERED_EVENT_TYPES)[number];

/** Health-domain event types (Health Connect observations). */
export const HEALTH_EVENT_TYPES: RegisteredEventType[] = [
  "health.heartrate.sample",
  "health.sleep.session",
  "health.step.sample",
];

/** Payment-domain event types (structured transaction facts). */
export const PAYMENT_EVENT_TYPES: RegisteredEventType[] = ["payment.transaction"];

/** Activity-domain event types consumed by usage metrics and timelines. */
export const ACTIVITY_EVENT_TYPES: RegisteredEventType[] = ["activity.interval"];

/**
 * Source kinds declared by the registered contract schemas
 * (contracts/schemas/events/*.schema.json, $defs.SourceKind). Ingest rejects
 * anything else so stored events always re-read as a contract-valid envelope.
 */
export const LEGAL_SOURCE_KINDS = [
  "windows.foreground",
  "android.accessibility",
  "android.usagestats",
  "android.healthconnect",
  "android.wechatpay",
] as const;

/**
 * The contract forbids full executable paths in identifiers that name an
 * application or origin; they must stay opaque package or executable names.
 * Path separators and drive-letter prefixes are the rejected shapes.
 */
export function isOpaqueApplicationId(value: string): boolean {
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

/** The Health Connect application that produced the observation; never a path. */
const dataOriginSchema = z
  .string()
  .min(1)
  .refine(isOpaqueApplicationId, "data_origin must be a package name, never a path.");

const healthStepSamplePayloadSchema = z.strictObject({
  count: z.strictObject({
    value: z.number().int().min(0).max(1_000_000),
    unit: z.literal("steps"),
  }),
  data_origin: dataOriginSchema,
});

const healthHeartrateSamplePayloadSchema = z.strictObject({
  beats_per_minute: z.number().int().min(15).max(300),
  data_origin: dataOriginSchema,
});

const healthSleepSessionPayloadSchema = z.strictObject({
  duration: z.strictObject({
    value: z.number().int().min(0),
    unit: z.literal("ms"),
  }),
  data_origin: dataOriginSchema,
});

/**
 * Spending categories produced by on-device classification rules; the enum
 * mirrors contracts/schemas/events/payment.transaction.v1.schema.json.
 */
const PAYMENT_CATEGORIES = [
  "food",
  "transport",
  "shopping",
  "bills",
  "health",
  "education",
  "entertainment",
  "transfer",
  "uncategorized",
] as const;

/** ISO 4217 alphabetic currency code in upper case. */
const currencyCodeSchema = z.string().regex(/^[A-Z]{3}$/, "currency must be an upper-case ISO 4217 alphabetic code.");

/**
 * The exact transaction amount in minor currency units. The approved merchant
 * label names a payee, never a filesystem path, and the strict payload shape
 * keeps free text (notification bodies, titles) out of the contract.
 */
const paymentTransactionPayloadSchema = z.strictObject({
  amount: z.strictObject({
    value: z.number().int().min(1).max(10_000_000_000),
    currency: currencyCodeSchema,
  }),
  direction: z.enum(["income", "expense"]),
  merchant: z
    .string()
    .min(1)
    .max(80)
    .refine(isOpaqueApplicationId, "merchant must be an extracted label, never a path."),
  category: z.enum(PAYMENT_CATEGORIES),
  pending_confirmation: z.boolean(),
});

/**
 * Manual Owner corrections allocate revisions in a reserved high space above
 * every device revision. Device checkpoint streams increment small counters,
 * so once a correction exists any device re-upload compares lower under the
 * existing three-way revision compare and answers `stale_revision`: a device
 * revision can never overwrite a human interpretation or resurrect an
 * invalidated fact, and batch idempotency semantics stay untouched.
 */
export const CORRECTION_REVISION_BASE = 1_000_000_000;

/**
 * Dotted paths the Owner may correct per registered event type: the
 * contract-approved structured interpretation fields only (SPEC implementation
 * decision 20). Identity, source, device, provenance, privacy, and
 * finalization state are never correctable; free text does not exist in any
 * registered payload. Time corrections address the envelope bounds
 * (`start_at`/`end_at`); a declared duration is re-derived from the corrected
 * bounds, never hand-edited.
 */
export const CORRECTABLE_PATHS: Record<RegisteredEventType, readonly string[]> = {
  "activity.interval": ["payload.application_label", "payload.subject_id", "payload.is_afk", "start_at", "end_at"],
  "health.heartrate.sample": ["payload.beats_per_minute", "start_at"],
  "health.sleep.session": ["start_at", "end_at"],
  "health.step.sample": ["payload.count.value", "start_at", "end_at"],
  "payment.transaction": [
    "payload.amount.value",
    "payload.amount.currency",
    "payload.direction",
    "payload.merchant",
    "payload.category",
    "payload.pending_confirmation",
    "start_at",
  ],
};

/** The contract-approved correctable paths of one registered event type. */
export function correctablePathsFor(eventType: RegisteredEventType): readonly string[] {
  return CORRECTABLE_PATHS[eventType];
}

export interface RegisteredEventEnvelope {
  eventType: RegisteredEventType;
  schemaVersion: number;
  sourceKind: string;
  startAt: Date;
  endAt: Date | null;
  finalizationState: "checkpoint" | "final";
  payload: Record<string, unknown>;
}

/** Per-schema registration: payload shape, legal sources, write scope, and privacy default. */
interface RegisteredEventSchema {
  payloadSchema: z.ZodType;
  sourceKinds: readonly string[];
  /** Scope a device credential needs to upload events of this type. */
  requiredWriteScope: string;
  /** Privacy level applied when the envelope omits privacy_level. */
  defaultPrivacy: "normal" | "sensitive";
  /** Interval semantics: "instant" types (heart rate samples) must not carry end_at. */
  timeSemantics: "interval" | "instant";
  /** Interval-bounded health schemas require end_at even outside finalization. */
  requiresEndAt: boolean;
}

const ACTIVITY_SOURCE_KINDS = ["windows.foreground", "android.accessibility", "android.usagestats"] as const;
const HEALTH_SOURCE_KINDS = ["android.healthconnect"] as const;
const PAYMENT_SOURCE_KINDS = ["android.wechatpay"] as const;

const registeredSchemas = new Map<string, RegisteredEventSchema>([
  [
    "activity.interval@1",
    {
      payloadSchema: activityIntervalPayloadSchema,
      sourceKinds: ACTIVITY_SOURCE_KINDS,
      requiredWriteScope: "events:write",
      defaultPrivacy: "normal",
      timeSemantics: "interval",
      requiresEndAt: false,
    },
  ],
  [
    "health.heartrate.sample@1",
    {
      payloadSchema: healthHeartrateSamplePayloadSchema,
      sourceKinds: HEALTH_SOURCE_KINDS,
      requiredWriteScope: "health:write",
      defaultPrivacy: "sensitive",
      timeSemantics: "instant",
      requiresEndAt: false,
    },
  ],
  [
    "health.sleep.session@1",
    {
      payloadSchema: healthSleepSessionPayloadSchema,
      sourceKinds: HEALTH_SOURCE_KINDS,
      requiredWriteScope: "health:write",
      defaultPrivacy: "sensitive",
      timeSemantics: "interval",
      requiresEndAt: true,
    },
  ],
  [
    "health.step.sample@1",
    {
      payloadSchema: healthStepSamplePayloadSchema,
      sourceKinds: HEALTH_SOURCE_KINDS,
      requiredWriteScope: "health:write",
      defaultPrivacy: "sensitive",
      timeSemantics: "interval",
      requiresEndAt: true,
    },
  ],
  [
    "payment.transaction@1",
    {
      payloadSchema: paymentTransactionPayloadSchema,
      sourceKinds: PAYMENT_SOURCE_KINDS,
      requiredWriteScope: "payment:write",
      defaultPrivacy: "sensitive",
      timeSemantics: "instant",
      requiresEndAt: false,
    },
  ],
]);

/** The scope a device credential needs to upload events of the given type. */
export function requiredWriteScope(eventType: RegisteredEventType): string {
  const schema = registeredSchemas.get(`${eventType}@1`);
  if (!schema) throw new Error(`The event type ${eventType} has no registered schema.`);
  return schema.requiredWriteScope;
}

/** Privacy level applied when a batch item omits privacy_level. */
export function defaultPrivacyLevel(eventType: RegisteredEventType): "normal" | "sensitive" {
  const schema = registeredSchemas.get(`${eventType}@1`);
  if (!schema) throw new Error(`The event type ${eventType} has no registered schema.`);
  return schema.defaultPrivacy;
}

/**
 * Validates one event against its registered payload schema (units, required
 * fields, forbidden fields, legal sources) plus the interval semantics the
 * schema binds to the envelope: interval events need `end_at`, instant events
 * reject it, and whenever `end_at` is present it must not precede `start_at`
 * and a declared `duration` must equal the interval bounds within one
 * millisecond of rounding tolerance.
 */
export function validateRegisteredEvent(envelope: RegisteredEventEnvelope): string | null {
  const schema = registeredSchemas.get(`${envelope.eventType}@${envelope.schemaVersion}`);
  if (!schema) return "The event payload has no registered schema.";
  if (!(schema.sourceKinds as readonly string[]).includes(envelope.sourceKind)) {
    return `source.kind must be one of: ${schema.sourceKinds.join(", ")}.`;
  }
  const parsed = schema.payloadSchema.safeParse(envelope.payload);
  if (!parsed.success) {
    const issue = parsed.error.issues.at(0);
    if (!issue) return `The event payload is not valid for ${envelope.eventType} v${envelope.schemaVersion}.`;
    const path = issue.path.length > 0 ? `${issue.path.join(".")}: ` : "";
    return `The event payload is not valid for ${envelope.eventType} v${envelope.schemaVersion}. ${path}${issue.message}`;
  }
  if (schema.timeSemantics === "interval" && envelope.finalizationState === "final" && envelope.endAt === null) {
    return "A finalized event must carry end_at.";
  }
  if (schema.timeSemantics === "instant" && envelope.endAt !== null) {
    return `${envelope.eventType} is an instantaneous sample and must not carry end_at.`;
  }
  if (schema.requiresEndAt && envelope.endAt === null) {
    return `${envelope.eventType} is interval-bounded and must carry end_at.`;
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
