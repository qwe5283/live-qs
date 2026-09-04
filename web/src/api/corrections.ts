import { apiPost } from "./client";
import type { EventCorrectionRequest, EventCorrectionResult, VersionedEvent } from "../generated/contract-models";

/**
 * Submits an auditable Owner correction for one logical event: contract-approved
 * structured fields, an optional reason, and an optional invalidation flag. The
 * server allocates a revision in the reserved manual-correction space, archives
 * the superseded snapshot, and answers with the corrected event plus the
 * affected report-day ranges. Device Tokens and Query Tokens can never call
 * this; the browser Owner session is the only accepted credential.
 */
export function submitCorrection(eventId: string, body: EventCorrectionRequest): Promise<EventCorrectionResult> {
  return apiPost<EventCorrectionResult>(`/api/v1/events/${eventId}/corrections`, body);
}

export interface CorrectableFieldDescriptor {
  path: string;
  label: string;
  kind: "text" | "number" | "money" | "boolean" | "datetime" | "select";
  options?: Array<{ value: string; label: string }>;
  required?: boolean;
}

const PAYMENT_CATEGORIES: Array<{ value: string; label: string }> = [
  { value: "food", label: "餐饮" },
  { value: "transport", label: "交通" },
  { value: "shopping", label: "购物" },
  { value: "bills", label: "生活缴费" },
  { value: "health", label: "医疗健康" },
  { value: "education", label: "教育" },
  { value: "entertainment", label: "娱乐" },
  { value: "transfer", label: "转账红包" },
  { value: "uncategorized", label: "未分类" },
];

/**
 * The contract-approved correctable fields per registered event type, mirroring
 * the server payload registry. The modal renders exactly these; identity,
 * source, provenance, and free text are never editable.
 */
export function correctionFieldsFor(event: VersionedEvent): CorrectableFieldDescriptor[] {
  if (event.event_type === "payment.transaction") {
    return [
      { path: "payload.amount.value", label: "金额（元）", kind: "money", required: true },
      { path: "payload.amount.currency", label: "币种", kind: "text", required: true },
      { path: "payload.direction", label: "收支方向", kind: "select", options: [
        { value: "expense", label: "支出" },
        { value: "income", label: "收入" },
      ], required: true },
      { path: "payload.merchant", label: "商户标签", kind: "text", required: true },
      { path: "payload.category", label: "分类", kind: "select", options: PAYMENT_CATEGORIES, required: true },
      { path: "payload.pending_confirmation", label: "待确认（疑似重复）", kind: "boolean" },
      { path: "start_at", label: "发生时间", kind: "datetime", required: true },
    ];
  }
  if (event.event_type === "activity.interval") {
    const fields: CorrectableFieldDescriptor[] = [
      { path: "payload.application_label", label: "应用显示名", kind: "text" },
      { path: "payload.subject_id", label: "主题标识（subject）", kind: "text" },
      { path: "payload.is_afk", label: "AFK（离开）", kind: "boolean" },
      { path: "start_at", label: "开始时间", kind: "datetime", required: true },
    ];
    if (event.end_at) fields.push({ path: "end_at", label: "结束时间", kind: "datetime", required: true });
    return fields;
  }
  if (event.event_type === "health.heartrate.sample") {
    return [
      { path: "payload.beats_per_minute", label: "心率（bpm）", kind: "number", required: true },
      { path: "start_at", label: "采样时间", kind: "datetime", required: true },
    ];
  }
  if (event.event_type === "health.step.sample") {
    return [
      { path: "payload.count.value", label: "步数", kind: "number", required: true },
      { path: "start_at", label: "开始时间", kind: "datetime", required: true },
      { path: "end_at", label: "结束时间", kind: "datetime", required: true },
    ];
  }
  if (event.event_type === "health.sleep.session") {
    return [
      { path: "start_at", label: "入睡时间", kind: "datetime", required: true },
      { path: "end_at", label: "醒来时间", kind: "datetime", required: true },
    ];
  }
  return [];
}

/** Reads the current value of a dotted path from the event envelope. */
export function readEventPath(event: VersionedEvent, path: string): unknown {
  const segments = path.replace(/^payload\./, "").split(".");
  if (path === "start_at") return event.start_at;
  if (path === "end_at") return event.end_at ?? null;
  let cursor: unknown = event.payload;
  for (const segment of segments) {
    if (typeof cursor !== "object" || cursor === null) return undefined;
    cursor = (cursor as Record<string, unknown>)[segment];
  }
  return cursor;
}

/** Exact integer conversion yuan-string to minor units; floats never touch money. */
export function yuanTextToMinor(text: string): number | null {
  const match = /^(\d+)(?:\.(\d{1,2}))?$/.exec(text.trim());
  if (!match) return null;
  const whole = match[1] ?? "0";
  const fraction = (match[2] ?? "").padEnd(2, "0");
  return Number(whole) * 100 + Number(fraction);
}

/** Renders minor units as a decimal yuan string with integer math. */
export function minorToYuanText(minor: number): string {
  const sign = minor < 0 ? "-" : "";
  const abs = Math.abs(minor);
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, "0")}`;
}
