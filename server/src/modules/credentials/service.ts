import { createHash, randomBytes, randomUUID } from "node:crypto";
import type { CredentialKind, CredentialPrivacyCeiling, CredentialScope } from "../../generated/contract-models.js";
import { CredentialModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";

/** Recognizable plaintext prefix per credential kind; also used for token generation. */
const TOKEN_PREFIX: Record<CredentialKind, string> = {
  device_token: "lqdev_",
  query_token: "lqqry_",
};

/**
 * Scopes a credential of each kind may carry, as a non-empty subset.
 * Capabilities never cross actor types: device tokens hold write scopes
 * (events:write for activity, health:write for Health Connect observations,
 * payment:write for payment transactions), query tokens hold the matching
 * read scopes.
 */
export const KIND_SCOPES: Record<CredentialKind, CredentialScope[]> = {
  device_token: ["events:write", "health:write", "payment:write"],
  query_token: ["events:read", "health:read", "payment:read"],
};

export interface CredentialInput {
  kind: CredentialKind;
  name: string;
  scopes: string[];
  allowed_event_types: string[];
  privacy_ceiling: CredentialPrivacyCeiling;
  expires_at: string | null;
}

export interface CredentialView {
  credential_id: string;
  kind: CredentialKind;
  name: string;
  token_prefix: string;
  scopes: CredentialScope[];
  allowed_event_types: string[];
  privacy_ceiling: CredentialPrivacyCeiling;
  created_at: string;
  expires_at: string | null;
  last_used_at: string | null;
  revoked_at: string | null;
}

/** Non-secret credential facts used by the authorization middlewares. */
export interface CredentialAuthContext {
  id: string;
  userId: string;
  kind: CredentialKind;
  scopes: string[];
  allowed_event_types: string[];
  privacy_ceiling: CredentialPrivacyCeiling;
}

interface CredentialRecord {
  id: string;
  user_id: string;
  kind: CredentialKind;
  name: string;
  token_hash: string;
  token_prefix: string;
  scopes: string[];
  allowed_event_types: string[];
  privacy_ceiling: CredentialPrivacyCeiling;
  created_at: Date;
  expires_at: Date | null;
  last_used_at: Date | null;
  revoked_at: Date | null;
}

export function assertScopesMatchKind(kind: CredentialKind, scopes: string[]): void {
  const allowed = KIND_SCOPES[kind];
  const unique = new Set(scopes);
  if (scopes.length === 0 || unique.size !== scopes.length
    || !scopes.every((scope) => (allowed as string[]).includes(scope))) {
    throw new AppError(
      400,
      kind === "device_token"
        ? "A device token may only carry write scopes: events:write, health:write, payment:write."
        : "A query token may only carry read scopes: events:read, health:read, payment:read.",
      "invalid_scope",
    );
  }
}

export function parseExpiry(value: string | null): Date | null {
  if (value === null) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    throw new AppError(400, "expires_at must be an ISO 8601 instant or null.", "invalid_expiry");
  }
  return parsed;
}

/** Creates a credential and returns the plaintext token; it is never persisted or logged. */
export async function createCredential(userId: string, input: CredentialInput): Promise<{ credential: CredentialView; token: string }> {
  assertScopesMatchKind(input.kind, input.scopes);
  const expiresAt = parseExpiry(input.expires_at);
  const token = `${TOKEN_PREFIX[input.kind]}${randomBytes(32).toString("base64url")}`;
  const now = new Date();
  const record = await CredentialModel.create({
    id: `cred_${randomUUID()}`,
    user_id: userId,
    kind: input.kind,
    name: input.name,
    token_hash: hashToken(token),
    token_prefix: token.slice(0, 16),
    scopes: input.scopes,
    allowed_event_types: input.allowed_event_types,
    privacy_ceiling: input.privacy_ceiling,
    created_at: now,
    expires_at: expiresAt,
    last_used_at: null,
    revoked_at: null,
  });
  return { credential: toView(record.toObject() as CredentialRecord), token };
}

export async function listCredentials(userId: string): Promise<CredentialView[]> {
  const records = await CredentialModel.find({ user_id: userId }).sort({ created_at: -1 }).lean<CredentialRecord[]>();
  return records.map(toView);
}

/** Revokes the credential; returns false when the credential does not exist. Idempotent otherwise. */
export async function revokeCredential(userId: string, credentialId: string): Promise<boolean> {
  const result = await CredentialModel.updateOne(
    { id: credentialId, user_id: userId, revoked_at: null },
    { revoked_at: new Date() },
  );
  if (result.matchedCount > 0) return true;
  // Already revoked credentials match only without the revoked_at guard.
  const existing = await CredentialModel.exists({ id: credentialId, user_id: userId });
  return existing !== null;
}

export function toView(record: CredentialRecord): CredentialView {
  return {
    credential_id: record.id,
    kind: record.kind,
    name: record.name,
    token_prefix: record.token_prefix,
    scopes: record.scopes as CredentialScope[],
    allowed_event_types: record.allowed_event_types,
    privacy_ceiling: record.privacy_ceiling,
    created_at: record.created_at.toISOString(),
    expires_at: record.expires_at?.toISOString() ?? null,
    last_used_at: record.last_used_at?.toISOString() ?? null,
    revoked_at: record.revoked_at?.toISOString() ?? null,
  };
}

export function hashToken(token: string): string {
  return createHash("sha256").update(token).digest("hex");
}

/**
 * Resolves a bearer token to its credential record. Returns the denial reason
 * instead of a record when the token is unknown, revoked, or expired.
 */
export async function resolveBearerCredential(token: string): Promise<
  | CredentialAuthContext
  | { denial: "unknown_token" | "token_revoked" | "token_expired"; prefix: string; kind?: CredentialKind; userId?: string }
> {
  const record = await CredentialModel.findOne({ token_hash: hashToken(token) }).lean<CredentialRecord | null>();
  if (!record) return { denial: "unknown_token", prefix: token.slice(0, 16) };
  if (record.revoked_at !== null) return { denial: "token_revoked", prefix: record.token_prefix, kind: record.kind, userId: record.user_id };
  if (record.expires_at !== null && record.expires_at.getTime() <= Date.now()) {
    return { denial: "token_expired", prefix: record.token_prefix, kind: record.kind, userId: record.user_id };
  }
  return {
    id: record.id,
    userId: record.user_id,
    kind: record.kind,
    scopes: record.scopes,
    allowed_event_types: record.allowed_event_types,
    privacy_ceiling: record.privacy_ceiling,
  };
}

const LAST_USED_FLUSH_INTERVAL_MS = 60_000;
const lastUsedFlushedAt = new Map<string, number>();

/**
 * Updates last_used_at at most once per minute per credential and process to
 * avoid write amplification on every request; a restart may cause one extra
 * write per credential.
 */
export async function touchCredentialLastUsed(credentialId: string): Promise<void> {
  const now = Date.now();
  const last = lastUsedFlushedAt.get(credentialId) ?? 0;
  if (now - last < LAST_USED_FLUSH_INTERVAL_MS) return;
  lastUsedFlushedAt.set(credentialId, now);
  await CredentialModel.updateOne({ id: credentialId }, { last_used_at: new Date() });
}
