import { createHash, randomBytes } from "node:crypto";
import type { Request } from "express";
import type { ScryptParams } from "./password.js";
import { hashPassword, verifyPassword } from "./password.js";
import { OwnerCredentialModel, OwnerSessionModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";

export const SESSION_COOKIE_NAME = "liveqs_session";

interface OwnerCredentialRecord {
  kdf: string;
  kdf_params: unknown;
  salt: string;
  password_hash: string;
}

export interface OwnerSession {
  userId: string;
  sessionId: string;
}

export async function isOwnerInitialized(userId: string): Promise<boolean> {
  return (await OwnerCredentialModel.exists({ user_id: userId })) !== null;
}

export async function createOwnerCredential(userId: string, password: string): Promise<void> {
  if (await isOwnerInitialized(userId)) {
    throw new AppError(409, "The Owner password has already been set up.", "already_initialized");
  }
  const record = await hashPassword(password);
  const now = new Date();
  try {
    await OwnerCredentialModel.create({
      user_id: userId,
      kdf: record.kdf,
      kdf_params: record.kdf_params,
      salt: record.salt,
      password_hash: record.password_hash,
      created_at: now,
      updated_at: now,
    });
  } catch (error) {
    if (isDuplicateKeyError(error)) {
      throw new AppError(409, "The Owner password has already been set up.", "already_initialized");
    }
    throw error;
  }
}

export async function verifyOwnerPassword(userId: string, password: string): Promise<boolean> {
  const credential = await OwnerCredentialModel.findOne({ user_id: userId }).lean<OwnerCredentialRecord | null>();
  if (!credential) return false;
  return verifyPassword(password, {
    kdf: credential.kdf as "scrypt",
    kdf_params: credential.kdf_params as ScryptParams,
    salt: credential.salt,
    password_hash: credential.password_hash,
  });
}

export async function createOwnerSession(userId: string, ttlHours: number): Promise<string> {
  const token = randomBytes(32).toString("base64url");
  const now = new Date();
  await OwnerSessionModel.create({
    id: sessionTokenHash(token),
    user_id: userId,
    created_at: now,
    expires_at: new Date(now.getTime() + ttlHours * 3_600_000),
    revoked_at: null,
  });
  return token;
}

export async function resolveOwnerSession(token: string | null): Promise<OwnerSession | null> {
  if (!token) return null;
  const session = await OwnerSessionModel.findOne({ id: sessionTokenHash(token) }).lean();
  if (!session) return null;
  if (session.revoked_at !== null) return null;
  if (session.expires_at.getTime() <= Date.now()) return null;
  return { userId: session.user_id, sessionId: session.id };
}

export async function revokeOwnerSession(token: string | null): Promise<void> {
  if (!token) return;
  await OwnerSessionModel.updateOne({ id: sessionTokenHash(token) }, { revoked_at: new Date() });
}

export function readSessionToken(req: Request): string | null {
  const header = req.get("cookie");
  if (!header) return null;
  for (const part of header.split(";")) {
    const separator = part.indexOf("=");
    if (separator < 0) continue;
    if (part.slice(0, separator).trim() === SESSION_COOKIE_NAME) {
      const value = part.slice(separator + 1).trim();
      try {
        return decodeURIComponent(value);
      } catch {
        return null;
      }
    }
  }
  return null;
}

function sessionTokenHash(token: string): string {
  return createHash("sha256").update(token).digest("hex");
}

function isDuplicateKeyError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && (error as { code: unknown }).code === 11000;
}
