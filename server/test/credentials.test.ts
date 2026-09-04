import { createHash } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, AuditLogModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_credentials";
const ownerPassword = "correct horse battery staple";

function buildEnv(overrides: Partial<Env> = {}): Env {
  return {
    NODE_ENV: "test",
    HOST: "127.0.0.1",
    PORT: 0,
    MONGODB_URI: testUri,
    HASH_SECRET: "test-secret-with-at-least-thirty-two-characters",
    DEFAULT_USER_ID: "test-user",
    SESSION_TTL_HOURS: 168,
    COOKIE_SECURE: false,
    CORS_ORIGINS: "http://localhost:5173",
    RATE_LIMIT_PER_MINUTE: 120,
    QUERY_TOKEN_MAX_RANGE_DAYS: 366,
    ...overrides,
  };
}

function setCookieFor(response: Response, name: string): string | undefined {
  const values = response.headers["set-cookie"];
  const list = Array.isArray(values) ? values : values ? [values] : [];
  return list.find((value) => value.startsWith(`${name}=`));
}

function sessionCookieValue(setCookie: string): string {
  return setCookie.split(";")[0] ?? "";
}

let dbReady = false;
let app: ReturnType<typeof createApp>;

beforeAll(async () => {
  try {
    await connectDatabase(testUri);
    dbReady = true;
  } catch {
    console.warn(`[credentials.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[credentials.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    CredentialModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
  ]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

/** Sets up the Owner password, logs in, and returns the session cookie. */
async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

describe("credential creation by the Owner", () => {
  it("creates a device token, shows plaintext once, and persists only digest and prefix", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const response = await request(app)
      .post("/api/v1/credentials")
      .set("Cookie", cookie)
      .send({
        kind: "device_token",
        name: "Windows 桌面机",
        scopes: ["events:write"],
        allowed_event_types: ["activity.interval"],
        privacy_ceiling: "normal",
        expires_at: null,
      })
      .expect(201);

    expect(response.body.token).toMatch(/^lqdev_[A-Za-z0-9_-]{40,}$/);
    const view = response.body.credential;
    expect(view.kind).toBe("device_token");
    expect(view.name).toBe("Windows 桌面机");
    expect(view.scopes).toEqual(["events:write"]);
    expect(view.allowed_event_types).toEqual(["activity.interval"]);
    expect(view.privacy_ceiling).toBe("normal");
    expect(view.expires_at).toBeNull();
    expect(view.last_used_at).toBeNull();
    expect(view.revoked_at).toBeNull();
    expect(view.created_at).toBeTruthy();
    expect(view.credential_id).toMatch(/^cred_/);
    expect(view.token_prefix).toMatch(/^lqdev_/);
    expect(view.token_prefix.length).toBeLessThan(response.body.token.length);
    expect(JSON.stringify(view)).not.toContain(response.body.token);

    const stored = await CredentialModel.findOne({ id: view.credential_id }).lean();
    expect(stored?.token_hash).toBe(createHash("sha256").update(response.body.token).digest("hex"));
    expect(stored?.token_prefix).toBe(view.token_prefix);
    expect(JSON.stringify(stored)).not.toContain(response.body.token);
  });

  it("creates a query token with the lqqry_ prefix", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const response = await request(app)
      .post("/api/v1/credentials")
      .set("Cookie", cookie)
      .send({ kind: "query_token", name: "分析 Agent", scopes: ["events:read"] })
      .expect(201);
    expect(response.body.token).toMatch(/^lqqry_[A-Za-z0-9_-]{40,}$/);
    expect(response.body.credential.kind).toBe("query_token");
    expect(response.body.credential.allowed_event_types).toEqual([]);
    expect(response.body.credential.privacy_ceiling).toBe("normal");
  });

  it("rejects scopes outside the credential kind", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const response = await request(app)
      .post("/api/v1/credentials")
      .set("Cookie", cookie)
      .send({ kind: "device_token", name: "越权设备", scopes: ["events:read"] })
      .expect(400);
    expect(response.body.error.code).toBe("invalid_scope");
    expect(typeof response.body.request_id).toBe("string");
  });

  it("rejects creation without an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app)
      .post("/api/v1/credentials")
      .send({ kind: "device_token", name: "匿名", scopes: ["events:write"] })
      .expect(401);
  });
});

describe("credential listing, revocation, and audit", () => {
  async function createDeviceToken(cookie: string, name: string): Promise<{ id: string; token: string; prefix: string }> {
    const response = await request(app)
      .post("/api/v1/credentials")
      .set("Cookie", cookie)
      .send({ kind: "device_token", name, scopes: ["events:write"] })
      .expect(201);
    return { id: response.body.credential.credential_id, token: response.body.token, prefix: response.body.credential.token_prefix };
  }

  it("lists credentials with lifecycle state but without token material", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const first = await createDeviceToken(cookie, "Windows 桌面机");
    await request(app)
      .post("/api/v1/credentials")
      .set("Cookie", cookie)
      .send({ kind: "query_token", name: "分析 Agent", scopes: ["events:read"] })
      .expect(201);

    const response = await request(app).get("/api/v1/credentials").set("Cookie", cookie).expect(200);
    expect(response.body.credentials).toHaveLength(2);
    const serialized = JSON.stringify(response.body);
    expect(serialized).not.toContain(first.token);
    expect(serialized).not.toContain("token_hash");
    const device = response.body.credentials.find((entry: { name: string }) => entry.name === "Windows 桌面机") as
      | { token_prefix: string; revoked_at: string | null; last_used_at: string | null }
      | undefined;
    expect(device?.token_prefix).toBe(first.prefix);
    expect(device?.revoked_at).toBeNull();
    expect(device?.last_used_at).toBeNull();
  });

  it("revokes a credential immediately and reports the revocation state", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const created = await createDeviceToken(cookie, "待撤销设备");

    await request(app).post(`/api/v1/credentials/${created.id}/revoke`).set("Cookie", cookie).expect(204);
    const listed = await request(app).get("/api/v1/credentials").set("Cookie", cookie).expect(200);
    expect(listed.body.credentials[0].revoked_at).toBeTruthy();

    const stored = await CredentialModel.findOne({ id: created.id }).lean();
    expect(stored?.revoked_at).toBeInstanceOf(Date);
  });

  it("treats repeated revocation as idempotent and unknown credentials as not found", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const created = await createDeviceToken(cookie, "重复撤销");

    await request(app).post(`/api/v1/credentials/${created.id}/revoke`).set("Cookie", cookie).expect(204);
    await request(app).post(`/api/v1/credentials/${created.id}/revoke`).set("Cookie", cookie).expect(204);
    await request(app).post("/api/v1/credentials/cred_missing/revoke").set("Cookie", cookie).expect(404);
  });

  it("audits creation and revocation without storing secret values", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const created = await createDeviceToken(cookie, "审计设备");

    await request(app).post(`/api/v1/credentials/${created.id}/revoke`).set("Cookie", cookie).expect(204);

    const actions = await AuditLogModel.find({ action: { $in: ["credential.create", "credential.revoke"] } })
      .sort({ created_at: 1 })
      .lean();
    expect(actions.map((entry) => entry.action)).toEqual(["credential.create", "credential.revoke"]);
    const [createAudit, revokeAudit] = actions;
    expect(createAudit?.actor_type).toBe("user");
    expect(createAudit?.details.credential_id).toBe(created.id);
    expect(createAudit?.details.credential_kind).toBe("device_token");
    expect(revokeAudit?.details.credential_id).toBe(created.id);

    const serialized = JSON.stringify(actions);
    expect(serialized).not.toContain(created.token);
    expect(serialized).not.toContain(created.token.slice(16));
  });
});
