import { apiGet, apiPost } from "./client";

export interface OwnerStatus {
  initialized: boolean;
}

export interface OwnerSessionInfo {
  authenticated: boolean;
}

export function fetchOwnerStatus(): Promise<OwnerStatus> {
  return apiGet<OwnerStatus>("/api/v1/owner/status");
}

export function fetchOwnerSession(): Promise<OwnerSessionInfo> {
  return apiGet<OwnerSessionInfo>("/api/v1/owner/session");
}

export function setupOwner(password: string): Promise<void> {
  return apiPost<void>("/api/v1/owner/setup", { password });
}

export function loginOwner(password: string): Promise<void> {
  return apiPost<void>("/api/v1/owner/login", { password });
}

export function logoutOwner(): Promise<void> {
  return apiPost<void>("/api/v1/owner/logout");
}
