import { randomBytes, scrypt as scryptCallback, timingSafeEqual, type ScryptOptions } from "node:crypto";
import { promisify } from "node:util";

const scrypt = promisify(scryptCallback) as (
  password: string,
  salt: Buffer,
  keyLength: number,
  options: ScryptOptions,
) => Promise<Buffer>;

const SALT_BYTES = 16;
const MAXMEM_BYTES = 64 * 1024 * 1024;

export interface ScryptParams {
  N: number;
  r: number;
  p: number;
  key_length: number;
}

export interface PasswordRecord {
  kdf: "scrypt";
  kdf_params: ScryptParams;
  salt: string;
  password_hash: string;
}

const SCRYPT_PARAMS: ScryptParams = { N: 16384, r: 8, p: 1, key_length: 64 };

export async function hashPassword(password: string): Promise<PasswordRecord> {
  const salt = randomBytes(SALT_BYTES);
  const derivedKey = await deriveKey(password, salt, SCRYPT_PARAMS);
  return {
    kdf: "scrypt",
    kdf_params: { ...SCRYPT_PARAMS },
    salt: salt.toString("base64"),
    password_hash: derivedKey.toString("base64"),
  };
}

export async function verifyPassword(password: string, record: PasswordRecord): Promise<boolean> {
  if (record.kdf !== "scrypt") return false;
  const expected = Buffer.from(record.password_hash, "base64");
  const actual = await deriveKey(password, Buffer.from(record.salt, "base64"), record.kdf_params);
  return expected.length === actual.length && timingSafeEqual(expected, actual);
}

async function deriveKey(password: string, salt: Buffer, params: ScryptParams): Promise<Buffer> {
  return scrypt(password.normalize("NFKC"), salt, params.key_length, {
    N: params.N,
    r: params.r,
    p: params.p,
    maxmem: Math.max(MAXMEM_BYTES, 128 * params.N * params.r * 2),
  });
}
