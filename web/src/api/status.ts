import { apiGet } from "./client";
import type { DeviceStatusList } from "../generated/contract-models";

/**
 * Current-state projections of every reporting device. Heartbeats are
 * expiring projections, not events; the list never implies one global focus.
 */
export function fetchDeviceStatuses() {
  return apiGet<DeviceStatusList>("/api/v1/status");
}
