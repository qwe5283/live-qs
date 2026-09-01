import { apiGet, apiPost } from "./client";
import type { CredentialCreated, CredentialList } from "../generated/contract-models";

export type { CredentialCreated, CredentialList };

export interface CredentialCreateInput {
  kind: "device_token" | "query_token";
  name: string;
  scopes: string[];
  allowed_event_types?: string[];
  privacy_ceiling?: "normal" | "sensitive" | "private";
  expires_at?: string | null;
}

export function listCredentials(): Promise<CredentialList> {
  return apiGet<CredentialList>("/api/v1/credentials");
}

export function createCredential(input: CredentialCreateInput): Promise<CredentialCreated> {
  return apiPost<CredentialCreated>("/api/v1/credentials", {
    allowed_event_types: [],
    privacy_ceiling: "normal",
    expires_at: null,
    ...input,
  });
}

export function revokeCredential(credentialId: string): Promise<void> {
  return apiPost<void>(`/api/v1/credentials/${encodeURIComponent(credentialId)}/revoke`);
}
