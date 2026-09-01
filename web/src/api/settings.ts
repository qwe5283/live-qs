import { apiGet, apiPost } from "./client";
import type { OwnerSettings, OwnerSettingsUpdate } from "../generated/contract-models";

/** Reads the Owner report settings; the report timezone defines day and week boundaries for every report. */
export function fetchOwnerSettings(): Promise<OwnerSettings> {
  return apiGet<OwnerSettings>("/api/v1/owner/settings");
}

export function updateOwnerSettings(update: OwnerSettingsUpdate): Promise<OwnerSettings> {
  return apiPost<OwnerSettings>("/api/v1/owner/settings", update);
}
