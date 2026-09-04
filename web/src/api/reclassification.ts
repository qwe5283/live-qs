import { apiGet, apiPost } from "./client";
import type {
  ReclassificationEstimate,
  ReclassificationTaskStatus,
} from "../generated/contract-models";

/**
 * The device-executed explicit reclassification control plane: the server
 * coordinates tasks and accounts for unrecoverable events, while the actual
 * re-interpretation happens on the collecting devices that still hold the
 * raw context.
 */
export async function fetchReclassificationEstimate(params?: { from?: string; to?: string }): Promise<ReclassificationEstimate> {
  const query: Record<string, string> = {};
  if (params?.from) query.from = params.from;
  if (params?.to) query.to = params.to;
  return apiGet<ReclassificationEstimate>("/api/v1/classification/reclassification/estimate", query);
}

export async function fetchCurrentReclassificationTask(): Promise<ReclassificationTaskStatus | null> {
  // 204 (no task yet) resolves to undefined, which normalizes to null.
  return (await apiGet<ReclassificationTaskStatus | undefined>("/api/v1/classification/reclassification/tasks/current")) ?? null;
}

export function createReclassificationTask(request: {
  target_rule_set_version?: number;
  from?: string;
  to?: string;
}): Promise<ReclassificationTaskStatus> {
  return apiPost<ReclassificationTaskStatus>("/api/v1/classification/reclassification/tasks", request);
}

export function closeReclassificationTask(taskId: string): Promise<ReclassificationTaskStatus> {
  return apiPost<ReclassificationTaskStatus>(`/api/v1/classification/reclassification/tasks/${taskId}/close`);
}
