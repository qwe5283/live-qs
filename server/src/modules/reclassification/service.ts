import { randomUUID } from "node:crypto";
import type {
  ReclassificationDeviceReport,
  ReclassificationEstimate,
  ReclassificationEstimateDevice,
  ReclassificationTaskAssignment,
  ReclassificationTaskProgress,
  ReclassificationTaskStatus,
} from "../../generated/contract-models.js";
import { EventModel, ReclassificationTaskModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";
import { recordAuditLog } from "../../shared/audit.js";
import { getReportTimezone } from "../owner/settings.js";
import { readRuleSet } from "../classification/service.js";
import { CORRECTION_REVISION_BASE } from "../events/payload-registry.js";

/**
 * Explicit historical reclassification (SPEC implementation decision 19). The
 * server cannot reclassify by itself — raw titles exist only on devices — so
 * a task is a coordination object: the Owner freezes an estimated scope and a
 * target rule set version, devices re-run their local engine over locally
 * retained context and submit higher revisions through the batch endpoint,
 * and the server accounts for what each device could and could not recover.
 */

type TaskStatus = "open" | "closed";

interface EstimateDeviceRecord {
  device_id: string;
  platform: string;
  event_count: number;
  earliest_start_at: Date | null;
  latest_start_at: Date | null;
}

interface TaskDocument {
  id: string;
  user_id: string;
  status: TaskStatus;
  target_rule_set_version: number;
  from: Date | null;
  to: Date | null;
  created_at: Date;
  closed_at: Date | null;
  estimate: { generated_at: Date; total_events: number; devices: EstimateDeviceRecord[] };
  device_reports: Array<{
    device_id: string;
    platform: string;
    scanned: number;
    reclassified: number;
    unchanged: number;
    failed: number;
    reported_at: Date;
  }>;
}

export interface DeviceReportInput {
  platform: "windows" | "android";
  scanned: number;
  reclassified: number;
  unchanged: number;
  failed: number;
}

/**
 * The reclassification scope: finalized, non-AFK activity intervals that still
 * sit below the reserved manual-correction revision space. Open checkpoints
 * stay owned by the live checkpoint stream (every extension re-uploads with
 * the current cached rules anyway); manually corrected and invalidated events
 * are never re-interpreted by a device.
 */
function scopeFilter(userId: string, from: Date | null, to: Date | null): Record<string, unknown> {
  const timeRange = from || to
    ? { start_at: { ...(from ? { $gte: from } : {}), ...(to ? { $lt: to } : {}) } }
    : {};
  return {
    user_id: userId,
    type: "activity.interval",
    finalization_state: "final",
    invalidated: { $ne: true },
    revision: { $lt: CORRECTION_REVISION_BASE },
    "data.is_afk": { $ne: true },
    ...timeRange,
  };
}

function isoInstant(value: Date | null): string | null {
  return value ? value.toISOString() : null;
}

function platformOf(value: unknown): "windows" | "android" {
  return value === "android" ? "android" : "windows";
}

/** Aggregates the scope per server-bound device identity: counts and start range. */
export async function estimateReclassification(
  userId: string,
  from: Date | null,
  to: Date | null,
): Promise<ReclassificationEstimate> {
  const grouped = await EventModel.aggregate<{
    _id: string;
    platform: string | null;
    event_count: number;
    earliest: Date | null;
    latest: Date | null;
  }>([
    { $match: scopeFilter(userId, from, to) },
    {
      $group: {
        _id: "$device_id",
        platform: { $first: "$device_platform" },
        event_count: { $sum: 1 },
        earliest: { $min: "$start_at" },
        latest: { $max: "$start_at" },
      },
    },
    { $sort: { _id: 1 } },
  ]);
  const devices: ReclassificationEstimateDevice[] = grouped.map((row) => ({
    device_id: row._id,
    platform: platformOf(row.platform),
    event_count: row.event_count,
    earliest_start_at: isoInstant(row.earliest),
    latest_start_at: isoInstant(row.latest),
  }));
  return {
    generated_at: new Date().toISOString(),
    total_events: devices.reduce((total, device) => total + device.event_count, 0),
    devices,
  };
}

function frozenEstimate(estimate: ReclassificationEstimate, generatedAt: Date) {
  return {
    generated_at: generatedAt,
    total_events: estimate.total_events,
    devices: estimate.devices.map((device) => ({
      device_id: device.device_id,
      platform: device.platform,
      event_count: device.event_count,
      earliest_start_at: device.earliest_start_at ? new Date(device.earliest_start_at) : null,
      latest_start_at: device.latest_start_at ? new Date(device.latest_start_at) : null,
    })),
  };
}

/**
 * Events a reporting device did not find locally: the frozen task scope minus
 * what the device still holds. This is the explicit unrecoverable statement —
 * raw context aged out of device retention is accounted, never silently
 * skipped. Counts are clamped so segments finalized after task creation
 * cannot turn the statement negative.
 */
function unrecoverableFor(deviceId: string, scanned: number, estimate: TaskDocument["estimate"]): number {
  const scoped = estimate.devices.find((device) => device.device_id === deviceId)?.event_count ?? 0;
  return Math.max(0, scoped - scanned);
}

function toStatus(doc: TaskDocument): ReclassificationTaskStatus {
  const reports: ReclassificationDeviceReport[] = [...doc.device_reports]
    .sort((a, b) => a.device_id.localeCompare(b.device_id))
    .map((report) => ({
      device_id: report.device_id,
      platform: platformOf(report.platform),
      scanned: report.scanned,
      reclassified: report.reclassified,
      unchanged: report.unchanged,
      failed: report.failed,
      unrecoverable: unrecoverableFor(report.device_id, report.scanned, doc.estimate),
      reported_at: report.reported_at.toISOString(),
    }));
  const progress: ReclassificationTaskProgress = {
    devices_reported: reports.length,
    scanned: reports.reduce((total, report) => total + report.scanned, 0),
    reclassified: reports.reduce((total, report) => total + report.reclassified, 0),
    unchanged: reports.reduce((total, report) => total + report.unchanged, 0),
    failed: reports.reduce((total, report) => total + report.failed, 0),
    unrecoverable: reports.reduce((total, report) => total + report.unrecoverable, 0),
  };
  return {
    task_id: doc.id,
    status: doc.status,
    target_rule_set_version: doc.target_rule_set_version,
    from: isoInstant(doc.from),
    to: isoInstant(doc.to),
    created_at: doc.created_at.toISOString(),
    closed_at: isoInstant(doc.closed_at),
    estimate: {
      generated_at: doc.estimate.generated_at.toISOString(),
      total_events: doc.estimate.total_events,
      devices: doc.estimate.devices.map((device) => ({
        device_id: device.device_id,
        platform: platformOf(device.platform),
        event_count: device.event_count,
        earliest_start_at: isoInstant(device.earliest_start_at),
        latest_start_at: isoInstant(device.latest_start_at),
      })),
    },
    progress,
    device_reports: reports,
  };
}

async function openTask(userId: string): Promise<TaskDocument | null> {
  return ReclassificationTaskModel.findOne({ user_id: userId, status: "open" }).lean<TaskDocument | null>();
}

/**
 * Starts a reclassification task: the target rule set version defaults to the
 * currently published one, the scope estimate is frozen at creation (later
 * observations already classify under the new rules through the normal
 * stream), and at most one task may be open. The start is audited with the
 * Owner, the target rule version, and the time range.
 */
export async function createTask(
  userId: string,
  actorId: string | null,
  input: { targetRuleSetVersion?: number; from: Date | null; to: Date | null },
): Promise<ReclassificationTaskStatus> {
  const ruleSet = await readRuleSet(userId);
  const target = input.targetRuleSetVersion ?? ruleSet.rule_set_version;
  if (target < 1 || target > ruleSet.rule_set_version) {
    throw new AppError(
      400,
      `The target rule set version must be between 1 and the published version ${ruleSet.rule_set_version}.`,
      "invalid_target_version",
    );
  }
  if (await openTask(userId)) {
    throw new AppError(409, "A reclassification task is already open; close it before starting another.", "task_already_open");
  }

  const estimate = await estimateReclassification(userId, input.from, input.to);
  const now = new Date();
  const document = {
    id: randomUUID(),
    user_id: userId,
    status: "open" as TaskStatus,
    target_rule_set_version: target,
    from: input.from,
    to: input.to,
    created_at: now,
    closed_at: null,
    estimate: frozenEstimate(estimate, now),
    device_reports: [],
  };
  try {
    await ReclassificationTaskModel.create(document);
  } catch (error) {
    if ((error as { code?: number }).code === 11000) {
      throw new AppError(409, "A reclassification task is already open; close it before starting another.", "task_already_open");
    }
    throw error;
  }

  await recordAuditLog({
    userId,
    actorId,
    action: "reclassification.task_started",
    details: {
      task_id: document.id,
      target_rule_set_version: target,
      from: isoInstant(input.from),
      to: isoInstant(input.to),
      estimated_events: estimate.total_events,
      estimated_devices: estimate.devices.length,
    },
  });
  return toStatus(document as TaskDocument);
}

/** The latest task (open or closed) for the Owner progress view; null when none exists. */
export async function readLatestTask(userId: string): Promise<ReclassificationTaskStatus | null> {
  const doc = await ReclassificationTaskModel.findOne({ user_id: userId })
    .sort({ created_at: -1 })
    .lean<TaskDocument | null>();
  return doc ? toStatus(doc) : null;
}

/**
 * The open task a device should work on, or null when there is none or the
 * device already reported it. Devices poll this, refresh their cached rule
 * set to the target version, and process their locally retained context.
 */
export async function readTaskAssignment(
  userId: string,
  credentialId: string,
): Promise<ReclassificationTaskAssignment | null> {
  const doc = await openTask(userId);
  if (!doc || doc.device_reports.some((report) => report.device_id === credentialId)) return null;
  return {
    task_id: doc.id,
    target_rule_set_version: doc.target_rule_set_version,
    from: isoInstant(doc.from),
    to: isoInstant(doc.to),
  };
}

/**
 * Records one device's outcome counts for an assigned task, replacing any
 * previous report of the same device so a retried pass converges. Events the
 * device no longer holds locally are never claimed here; the status view
 * derives them as unrecoverable from the frozen scope.
 */
export async function recordDeviceReport(
  userId: string,
  credentialId: string,
  taskId: string,
  report: DeviceReportInput,
): Promise<void> {
  const doc = await ReclassificationTaskModel.findOne({ id: taskId, user_id: userId }).lean<TaskDocument | null>();
  if (!doc) throw new AppError(404, "The reclassification task does not exist.", "not_found");
  if (doc.status !== "open") {
    throw new AppError(409, "The reclassification task is closed and no longer accepts reports.", "task_not_open");
  }
  const entry = {
    device_id: credentialId,
    platform: report.platform,
    scanned: report.scanned,
    reclassified: report.reclassified,
    unchanged: report.unchanged,
    failed: report.failed,
    reported_at: new Date(),
  };
  await ReclassificationTaskModel.updateOne({ id: taskId }, [
    {
      $set: {
        device_reports: {
          $concatArrays: [
            { $filter: { input: "$device_reports", cond: { $ne: ["$$this.device_id", credentialId] } } },
            [entry],
          ],
        },
      },
    },
  ]);
  await recordAuditLog({
    userId,
    actorType: "device",
    actorId: credentialId,
    action: "reclassification.device_reported",
    details: {
      task_id: taskId,
      device_platform: report.platform,
      scanned: report.scanned,
      reclassified: report.reclassified,
      unchanged: report.unchanged,
      failed: report.failed,
    },
  });
}

/**
 * Closes a task and audits its actual impact: the Owner, the target rule
 * version, the time range, the report timezone, and the aggregated counts
 * including the server-computed unrecoverable total. Derived summaries rebuild
 * on read, so closing is a statement, not a rebuild job. Idempotent.
 */
export async function closeTask(userId: string, actorId: string | null, taskId: string): Promise<ReclassificationTaskStatus> {
  for (let attempt = 0; attempt < 5; attempt++) {
    const doc = await ReclassificationTaskModel.findOne({ id: taskId, user_id: userId }).lean<TaskDocument | null>();
    if (!doc) throw new AppError(404, "The reclassification task does not exist.", "not_found");
    if (doc.status === "closed") return toStatus(doc);

    const now = new Date();
    const updated = await ReclassificationTaskModel.updateOne(
      { id: taskId, status: "open" },
      { $set: { status: "closed", closed_at: now } },
    );
    if (updated.modifiedCount !== 1) continue;

    const status = toStatus({ ...doc, status: "closed", closed_at: now });
    const timezone = await getReportTimezone(userId);
    await recordAuditLog({
      userId,
      actorId,
      action: "reclassification.task_closed",
      details: {
        task_id: taskId,
        target_rule_set_version: doc.target_rule_set_version,
        from: isoInstant(doc.from),
        to: isoInstant(doc.to),
        timezone,
        devices_reported: status.progress.devices_reported,
        scanned: status.progress.scanned,
        reclassified: status.progress.reclassified,
        unchanged: status.progress.unchanged,
        failed: status.progress.failed,
        unrecoverable: status.progress.unrecoverable,
      },
    });
    return status;
  }
  throw new AppError(500, "The task could not be closed after repeated races.", "internal_error");
}
