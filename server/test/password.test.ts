import { describe, expect, it } from "vitest";
import { hashPassword, verifyPassword } from "../src/modules/owner/password.js";

describe("password hashing", () => {
  it("verifies the correct password against its scrypt record", async () => {
    const record = await hashPassword("correct horse battery staple");
    expect(record.kdf).toBe("scrypt");
    expect(await verifyPassword("correct horse battery staple", record)).toBe(true);
  });

  it("rejects a wrong password", async () => {
    const record = await hashPassword("correct horse battery staple");
    expect(await verifyPassword("wrong horse", record)).toBe(false);
  });

  it("records memory-hard KDF parameters and never stores plaintext", async () => {
    const password = "correct horse battery staple";
    const record = await hashPassword(password);
    expect(record.kdf_params.N).toBeGreaterThanOrEqual(16384);
    expect(JSON.stringify(record)).not.toContain(password);
  });

  it("uses an independent salt per hash", async () => {
    const [first, second] = await Promise.all([hashPassword("same"), hashPassword("same")]);
    expect(first.salt).not.toBe(second.salt);
    expect(first.password_hash).not.toBe(second.password_hash);
  });
});
