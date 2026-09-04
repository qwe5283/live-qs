import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, OwnerCredentialModel, OwnerSessionModel, AuditLogModel, EventModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_ai_skill";
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
    console.warn(`[ai-skill-boundary.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[ai-skill-boundary.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
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

async function createToken(
  cookie: string,
  input: { kind: string; scopes: string[]; privacy_ceiling?: string },
): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({
      kind: input.kind,
      name: "AI Skill",
      scopes: input.scopes,
      privacy_ceiling: input.privacy_ceiling ?? "normal",
      expires_at: null,
    })
    .expect(201);
  return response.body.token as string;
}

/** A heartbeat projection so the status read has something to report. */
async function reportHeartbeat(deviceToken: string): Promise<void> {
  await request(app)
    .post("/api/v1/heartbeats")
    .set("Authorization", `Bearer ${deviceToken}`)
    .send({
      platform: "windows",
      captured_at: new Date().toISOString(),
      activity: { application_id: "Code.exe", application_label: "Visual Studio Code", is_afk: false },
    })
    .expect(204);
}

describe("current-context reads for query tokens (context:read)", () => {
  it("a query token with context:read reads GET /status", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken(cookie, { kind: "device_token", scopes: ["events:write"] });
    await reportHeartbeat(deviceToken);
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });

    const response = await request(app)
      .get("/api/v1/status")
      .set("Authorization", `Bearer ${queryToken}`)
      .expect(200);

    expect(response.body.server_time).toBeTruthy();
    expect(response.body.devices).toHaveLength(1);
    expect(response.body.devices[0].activity.application_label).toBe("Visual Studio Code");
  });

  it("a query token with context:read reads GET /diagnostics/sync", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken(cookie, { kind: "device_token", scopes: ["events:write"] });
    await request(app)
      .post("/api/v1/diagnostics/sync")
      .set("Authorization", `Bearer ${deviceToken}`)
      .send({ platform: "windows", pending_count: 0, permanent_failure_count: 0, recent_errors: [] })
      .expect(204);
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });

    const response = await request(app)
      .get("/api/v1/diagnostics/sync")
      .set("Authorization", `Bearer ${queryToken}`)
      .expect(200);

    expect(response.body.devices).toHaveLength(1);
    expect(response.body.devices[0].pending_count).toBe(0);
  });

  it("a query token without context:read is denied the current-context reads", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["events:read"] });

    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${queryToken}`).expect(403);
    await request(app).get("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${queryToken}`).expect(403);

    const denies = await AuditLogModel.find({ action: "credential.deny" }).lean();
    expect(denies.some((entry) => JSON.stringify(entry.details).includes("insufficient_scope"))).toBe(true);
  });

  it("a device token is denied the current-context reads", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken(cookie, { kind: "device_token", scopes: ["events:write"] });

    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${deviceToken}`).expect(403);
    await request(app).get("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${deviceToken}`).expect(403);
  });

  it("the Owner session keeps reading the current context without a scope", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
  });
});

describe("per-credential rate limiting on bearer paths", () => {
  it("answers 429 with Retry-After once a credential exceeds its per-minute limit", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });
    const limitedApp = createApp(buildEnv({ RATE_LIMIT_PER_MINUTE: 3 }));

    for (let index = 0; index < 3; index++) {
      await request(limitedApp).get("/api/v1/status").set("Authorization", `Bearer ${queryToken}`).expect(200);
    }
    const denied = await request(limitedApp)
      .get("/api/v1/status")
      .set("Authorization", `Bearer ${queryToken}`)
      .expect(429);

    expect(denied.body.error.code).toBe("rate_limited");
    const retryAfter = Number(denied.headers["retry-after"]);
    expect(Number.isInteger(retryAfter)).toBe(true);
    expect(retryAfter).toBeGreaterThanOrEqual(1);
    expect(retryAfter).toBeLessThanOrEqual(60);

    const denies = await AuditLogModel.find({ action: "credential.deny" }).lean();
    const rateLimited = denies.filter((entry) => JSON.stringify(entry.details).includes("rate_limited"));
    expect(rateLimited.length).toBeGreaterThanOrEqual(1);
    expect(JSON.stringify(rateLimited)).not.toContain(queryToken);
  });

  it("the rate limit is per credential: another credential is unaffected", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const first = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });
    const second = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });
    const limitedApp = createApp(buildEnv({ RATE_LIMIT_PER_MINUTE: 2 }));

    for (let index = 0; index < 2; index++) {
      await request(limitedApp).get("/api/v1/status").set("Authorization", `Bearer ${first}`).expect(200);
    }
    await request(limitedApp).get("/api/v1/status").set("Authorization", `Bearer ${first}`).expect(429);
    await request(limitedApp).get("/api/v1/status").set("Authorization", `Bearer ${second}`).expect(200);
  });

  it("Owner session requests are not rate limited (trusted-LAN boundary)", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const limitedApp = createApp(buildEnv({ RATE_LIMIT_PER_MINUTE: 2 }));
    for (let index = 0; index < 5; index++) {
      await request(limitedApp).get("/api/v1/status").set("Cookie", cookie).expect(200);
    }
  });
});

describe("bounded query time range for query tokens", () => {
  function range(days: number): { from: string; to: string } {
    const to = new Date("2026-03-10T00:00:00Z");
    const from = new Date(to.getTime() - days * 86_400_000);
    return { from: from.toISOString(), to: to.toISOString() };
  }

  it("rejects a query-token event read whose span exceeds the configured maximum", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["events:read", "health:read", "payment:read"] });
    const boundedApp = createApp(buildEnv({ QUERY_TOKEN_MAX_RANGE_DAYS: 2 }));
    const wide = range(3);

    for (const path of ["/api/v1/events", "/api/v1/health/events", "/api/v1/payment/events"]) {
      const denied = await request(boundedApp)
        .get(path)
        .query({ ...wide, timezone: "UTC" })
        .set("Authorization", `Bearer ${queryToken}`)
        .expect(400);
      expect(denied.body.error.code).toBe("range_too_large");
    }
  });

  it("allows a span up to the maximum and applies per request", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["events:read"] });
    const boundedApp = createApp(buildEnv({ QUERY_TOKEN_MAX_RANGE_DAYS: 2 }));

    await request(boundedApp)
      .get("/api/v1/events")
      .query({ ...range(2), timezone: "UTC" })
      .set("Authorization", `Bearer ${queryToken}`)
      .expect(200);
  });

  it("does not bound the Owner session to the query-token maximum", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const boundedApp = createApp(buildEnv({ QUERY_TOKEN_MAX_RANGE_DAYS: 2 }));
    await request(boundedApp)
      .get("/api/v1/events")
      .query({ ...range(30), timezone: "UTC" })
      .set("Cookie", cookie)
      .expect(200);
  });
});

describe("query.read audit for AI credential reads", () => {
  async function seedEvent(overrides: { event_id?: string } = {}): Promise<string> {
    const eventId = overrides.event_id ?? crypto.randomUUID();
    const start = new Date("2026-09-01T01:00:00Z");
    const end = new Date(start.getTime() + 60_000);
    const now = new Date();
    await EventModel.create({
      id: eventId,
      bucket_id: `bucket:${eventId}`,
      user_id: "test-user",
      device_id: "cred_device",
      source: "windows.foreground",
      type: "activity.interval",
      schema_version: 1,
      revision: 1,
      finalization_state: "final",
      provenance: { collector_version: "0.1.0", observed_at: now.toISOString() },
      capture_timezone: "Asia/Shanghai",
      capture_offset_minutes: 480,
      invalidated: false,
      source_kind: "windows.foreground",
      source_record_id: "rec-1",
      device_platform: "windows",
      start_at: start,
      end_at: end,
      duration_ms: end.getTime() - start.getTime(),
      value: null,
      unit: null,
      data: { application_id: "idea64.exe", is_afk: false, duration: { value: 60_000, unit: "ms" } },
      privacy_level: "normal",
      confidence: 1,
      raw_hash: null,
      created_at: now,
      updated_at: now,
    });
    return eventId;
  }

  it("records subject, scopes, range, data types, and result count — never prompt-like content", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["events:read"] });
    await seedEvent();

    const page = await request(app)
      .get("/api/v1/events")
      .query({ from: "2026-09-01T00:00:00Z", to: "2026-09-02T00:00:00Z", timezone: "Asia/Shanghai" })
      .set("Authorization", `Bearer ${queryToken}`)
      .expect(200);

    const audits = await AuditLogModel.find({ action: "query.read" }).lean();
    expect(audits).toHaveLength(1);
    const audit = audits[0]!;
    expect(audit.actor_type).toBe("query");
    expect(audit.actor_id).toBeTruthy();
    const details = audit.details as Record<string, unknown>;
    expect(details.credential_kind).toBe("query_token");
    expect(details.scopes).toEqual(["events:read"]);
    expect(details.from).toBe("2026-09-01T00:00:00.000Z");
    expect(details.to).toBe("2026-09-02T00:00:00.000Z");
    expect(details.timezone).toBe("Asia/Shanghai");
    expect(details.result_count).toBe(page.body.data.length);
    expect(details.completeness).toBe(page.body.context.completeness);
    const detailsText = JSON.stringify(audit.details);
    expect(detailsText).not.toMatch(/prompt|window_title|notification_body|authorization|token_hash/i);
  });

  it("records the current-context reads with data types and result counts", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken(cookie, { kind: "device_token", scopes: ["events:write"] });
    await reportHeartbeat(deviceToken);
    const queryToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });

    const status = await request(app).get("/api/v1/status").set("Authorization", `Bearer ${queryToken}`).expect(200);
    await request(app).get("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${queryToken}`).expect(200);

    const audits = await AuditLogModel.find({ action: "query.read" }).sort({ created_at: 1 }).lean();
    expect(audits).toHaveLength(2);
    const statusAudit = audits[0]!;
    expect((statusAudit.details as Record<string, unknown>).data_types).toEqual(["device_status"]);
    expect((statusAudit.details as Record<string, unknown>).result_count).toBe(status.body.devices.length);
    const diagnosticsAudit = audits[1]!;
    expect((diagnosticsAudit.details as Record<string, unknown>).data_types).toEqual(["sync_diagnostics"]);
  });

  it("does not record query.read for Owner session reads", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app)
      .get("/api/v1/events")
      .query({ from: "2026-09-01T00:00:00Z", to: "2026-09-02T00:00:00Z", timezone: "Asia/Shanghai" })
      .set("Cookie", cookie)
      .expect(200);
    const audits = await AuditLogModel.find({ action: "query.read" }).lean();
    expect(audits).toHaveLength(0);
  });
});

describe("the AI credential has no mutation, execution, or administration capability", () => {
  it("denies every write, correction, rule, credential, and admin operation to a fully read-scoped query token", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const queryToken = await createToken(cookie, {
      kind: "query_token",
      scopes: ["events:read", "health:read", "payment:read", "context:read"],
      privacy_ceiling: "private",
    });
    const auth = { Authorization: `Bearer ${queryToken}` };
    const eventId = crypto.randomUUID();

    // Event writes and heartbeats/diagnostics pushes are device-token capabilities.
    await request(app).post("/api/v1/events/batch").set(auth)
      .send({ events: [] }).expect(403);
    await request(app).post("/api/v1/heartbeats").set(auth)
      .send({ platform: "windows", captured_at: new Date().toISOString(), activity: { is_afk: false } }).expect(403);
    await request(app).post("/api/v1/diagnostics/sync").set(auth)
      .send({ platform: "windows", pending_count: 0, permanent_failure_count: 0, recent_errors: [] }).expect(403);
    // Owner corrections, credentials, settings, source policy: Owner-session only.
    await request(app).post(`/api/v1/events/${eventId}/corrections`).set(auth)
      .send({ fields: [], reason: null, invalidate: false }).expect(401);
    await request(app).post("/api/v1/credentials").set(auth)
      .send({ kind: "query_token", name: "x", scopes: ["events:read"] }).expect(401);
    await request(app).get("/api/v1/credentials").set(auth).expect(401);
    await request(app).post("/api/v1/credentials/cred_x/revoke").set(auth).expect(401);
    await request(app).post("/api/v1/owner/settings").set(auth)
      .send({ report_timezone: "UTC" }).expect(401);
    await request(app).put("/api/v1/source-policy").set(auth).send({ entries: [] }).expect(401);
    await request(app).get("/api/v1/source-policy").set(auth).expect(401);
    // Classification management and reclassification: Owner-session only; rule
    // download is a device-only scope a query token can never hold.
    await request(app).put("/api/v1/classification/ruleset").set(auth).send({ entities: [], rules: [] }).expect(401);
    await request(app).get("/api/v1/classification/ruleset").set(auth).expect(403);
    await request(app).post("/api/v1/classification/reclassification/tasks").set(auth)
      .send({}).expect(401);
    // Admin and deletion surfaces sit behind the global Owner guard.
    await request(app).post("/api/v1/admin/events/delete").set(auth).send({}).expect(401);
    await request(app).get("/api/v1/admin/audit-logs").set(auth).expect(401);
    await request(app).get("/api/v1/context/current").set(auth).expect(401);
  });

  it("a revoked or expired query token cannot read anything, including the current context", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const revokedToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });
    const expiredToken = await createToken(cookie, { kind: "query_token", scopes: ["context:read"] });

    // Expiry is enforced at use time, so expire one credential directly in the store.
    await CredentialModel.updateOne(
      { token_prefix: expiredToken.slice(0, 16) },
      { expires_at: new Date(Date.now() - 1000) },
    );
    // Revocation goes through the Owner API.
    const list = await request(app).get("/api/v1/credentials").set("Cookie", cookie).expect(200);
    const credentials = list.body.credentials as Array<{ credential_id: string; token_prefix: string }>;
    const revokedId = credentials.find((entry) => entry.token_prefix === revokedToken.slice(0, 16))!.credential_id;
    await request(app).post(`/api/v1/credentials/${revokedId}/revoke`).set("Cookie", cookie).expect(204);

    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${revokedToken}`).expect(401);
    await request(app).get("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${revokedToken}`).expect(401);
    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${expiredToken}`).expect(401);
    await request(app).get("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${expiredToken}`).expect(401);
  });
});
