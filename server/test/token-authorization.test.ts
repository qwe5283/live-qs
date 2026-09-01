import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { AuditLogModel, CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_tokens";
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
    console.warn(`[token-authorization.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[token-authorization.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    AuditLogModel.syncIndexes(),
    CredentialModel.syncIndexes(),
    EventModel.syncIndexes(),
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

interface CreateOverrides {
  kind?: "device_token" | "query_token";
  name?: string;
  scopes?: string[];
  allowed_event_types?: string[];
  privacy_ceiling?: string;
  expires_at?: string | null;
}

async function createCredential(cookie: string, overrides: CreateOverrides = {}): Promise<{ id: string; token: string; prefix: string; kind: string }> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({
      kind: overrides.kind ?? "query_token",
      name: overrides.name ?? "测试凭据",
      scopes: overrides.scopes ?? (overrides.kind === "device_token" ? ["events:write"] : ["events:read"]),
      allowed_event_types: overrides.allowed_event_types,
      privacy_ceiling: overrides.privacy_ceiling,
      expires_at: overrides.expires_at,
    })
    .expect(201);
  return {
    id: response.body.credential.credential_id,
    token: response.body.token,
    prefix: response.body.credential.token_prefix,
    kind: response.body.credential.kind,
  };
}

interface SeedOverrides {
  start_at?: string;
  privacy_level?: string;
  type?: string;
}

async function seedEvent(overrides: SeedOverrides = {}): Promise<void> {
  const start = new Date(overrides.start_at ?? "2026-09-01T01:00:00Z");
  const end = new Date(start.getTime() + 60_000);
  const now = new Date();
  await EventModel.create({
    id: `${overrides.type ?? "activity.interval"}-${start.getTime()}`,
    bucket_id: `bucket:${start.getTime()}`,
    user_id: "test-user",
    device_id: "cred_device",
    source: overrides.type ?? "activity.interval",
    type: overrides.type ?? "activity.interval",
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
    privacy_level: overrides.privacy_level ?? "normal",
    confidence: 1,
    raw_hash: null,
    created_at: now,
    updated_at: now,
  });
}

function readUrl(overrides: Record<string, string> = {}): string {
  const params = new URLSearchParams({
    from: "2026-09-01T00:00:00Z",
    to: "2026-09-02T00:00:00Z",
    timezone: "Asia/Shanghai",
    ...overrides,
  });
  return `/api/v1/events?${params.toString()}`;
}

describe("query token access to event reads", () => {
  it("applies the privacy ceiling and reports withheld data as partial", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({ start_at: "2026-09-01T01:00:00Z", privacy_level: "normal" });
    await seedEvent({ start_at: "2026-09-01T02:00:00Z", privacy_level: "sensitive" });
    const restricted = await createCredential(cookie, { privacy_ceiling: "normal" });
    const wider = await createCredential(cookie, { name: "更宽上限", privacy_ceiling: "sensitive" });

    const capped = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${restricted.token}`)
      .expect(200);
    expect(capped.body.data).toHaveLength(1);
    expect(capped.body.data[0].privacy_level).toBe("normal");
    expect(capped.body.context.completeness).toBe("partial");

    const full = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${wider.token}`)
      .expect(200);
    expect(full.body.data).toHaveLength(2);
    expect(full.body.context.completeness).toBe("complete");
  });

  it("applies the credential's allowed event types to reads", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({ start_at: "2026-09-01T01:00:00Z", type: "activity.interval" });
    await seedEvent({ start_at: "2026-09-01T03:00:00Z", type: "usage.app_daily" }); // legacy type
    const scoped = await createCredential(cookie, { allowed_event_types: ["activity.interval"] });
    const unrestricted = await createCredential(cookie, { name: "不限制类型" });

    const filtered = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${scoped.token}`)
      .expect(200);
    expect(filtered.body.data).toHaveLength(1);

    const everything = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${unrestricted.token}`)
      .expect(200);
    expect(everything.body.data).toHaveLength(1); // legacy types are not contract-representable
  });

  it("keeps reporting partial completeness on cursor-continuation pages", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    for (const hour of [1, 2, 3]) {
      await seedEvent({ start_at: `2026-09-01T0${hour}:00:00Z`, privacy_level: "normal" });
    }
    await seedEvent({ start_at: "2026-09-01T04:00:00Z", privacy_level: "sensitive" });
    const restricted = await createCredential(cookie, { privacy_ceiling: "normal" });

    const firstPage = await request(app)
      .get(readUrl({ page_size: "1" }))
      .set("Authorization", `Bearer ${restricted.token}`)
      .expect(200);
    expect(firstPage.body.data).toHaveLength(1);
    expect(firstPage.body.context.completeness).toBe("partial");

    const secondPage = await request(app)
      .get(readUrl({ page_size: "1", cursor: firstPage.body.page.next_cursor }))
      .set("Authorization", `Bearer ${restricted.token}`)
      .expect(200);
    expect(secondPage.body.data).toHaveLength(1);
    expect(secondPage.body.context.completeness).toBe("partial");
  });

  it("rejects a device token reading events with insufficient scope", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, { kind: "device_token" });
    const response = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${device.token}`)
      .expect(403);
    expect(response.body.error.code).toBe("insufficient_scope");
    expect(typeof response.body.request_id).toBe("string");
  });

  it("rejects unknown, expired, and revoked tokens with distinct stable codes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({});
    const expired = await createCredential(cookie, { expires_at: "2026-01-01T00:00:00Z" });
    const revoked = await createCredential(cookie, { name: "已撤销" });

    const unknown = await request(app).get(readUrl()).set("Authorization", "Bearer lqqry_not_a_real_token").expect(401);
    expect(unknown.body.error.code).toBe("unknown_token");

    const expiredResponse = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${expired.token}`)
      .expect(401);
    expect(expiredResponse.body.error.code).toBe("token_expired");

    await request(app).post(`/api/v1/credentials/${revoked.id}/revoke`).set("Cookie", cookie).expect(204);
    const revokedResponse = await request(app)
      .get(readUrl())
      .set("Authorization", `Bearer ${revoked.token}`)
      .expect(401);
    expect(revokedResponse.body.error.code).toBe("token_revoked");
  });

  it("records use and denial audits plus last-used time without storing token material", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({});
    const credential = await createCredential(cookie, { name: "审计查询" });
    const device = await createCredential(cookie, { kind: "device_token", name: "审计设备" });

    await request(app).get(readUrl()).set("Authorization", `Bearer ${credential.token}`).expect(200);
    await request(app).get(readUrl()).set("Authorization", `Bearer ${device.token}`).expect(403);

    const listed = await request(app).get("/api/v1/credentials").set("Cookie", cookie).expect(200);
    const used = listed.body.credentials.find((entry: { name: string }) => entry.name === "审计查询");
    expect(used?.last_used_at).toBeTruthy();

    const audits = await AuditLogModel.find({ action: { $in: ["credential.use", "credential.deny"] } }).sort({ created_at: 1 }).lean();
    const useAudit = audits.find((entry) => entry.action === "credential.use");
    const denyAudit = audits.find((entry) => entry.action === "credential.deny");
    expect(useAudit?.actor_type).toBe("query");
    expect(useAudit?.actor_id).toBe(credential.id);
    expect(denyAudit?.actor_id).toBe(device.id);
    expect(denyAudit?.details.reason).toBe("insufficient_scope");
    expect(denyAudit?.details.required_scope).toBe("events:read");
    expect(JSON.stringify(audits)).not.toContain(credential.token);
    expect(JSON.stringify(audits)).not.toContain(device.token);
  });

  it("denies query tokens access to credential management and other protected endpoints", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const credential = await createCredential(cookie, { name: "越界探针" });

    await request(app)
      .get("/api/v1/credentials")
      .set("Authorization", `Bearer ${credential.token}`)
      .expect(401);
    await request(app)
      .post(`/api/v1/credentials/${credential.id}/revoke`)
      .set("Authorization", `Bearer ${credential.token}`)
      .expect(401);
    await request(app)
      .get("/api/v1/context/current")
      .set("Authorization", `Bearer ${credential.token}`)
      .expect(401);
  });
});

describe("device token batch uploads", () => {
  const EVENT_ID = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

  function envelopeItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      event_id: EVENT_ID,
      event_type: "activity.interval",
      schema_version: 1,
      owner_id: "test-user",
      source: { kind: "windows.foreground", record_id: "rec-1" },
      device: { id: "win-desktop", platform: "windows" },
      start_at: "2026-09-01T05:00:00Z",
      end_at: "2026-09-01T05:30:00Z",
      capture_timezone: "Asia/Shanghai",
      capture_offset_minutes: 480,
      privacy_level: "normal",
      revision: 1,
      finalization_state: "final",
      provenance: { collector_version: "0.1.0", observed_at: "2026-09-01T05:30:00.000Z" },
      invalidated: false,
      payload: { application_id: "idea64.exe", is_afk: false, duration: { value: 1_800_000, unit: "ms" } },
      ...overrides,
    };
  }

  async function createDevice(cookie: string, overrides: CreateOverrides = {}): Promise<{ id: string; token: string }> {
    const credential = await createCredential(cookie, { kind: "device_token", ...overrides });
    return { id: credential.id, token: credential.token };
  }

  it("accepts an approved event and makes it readable through the Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createDevice(cookie, { allowed_event_types: ["activity.interval"] });

    const upload = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device.token}`)
      .send({ events: [envelopeItem()] })
      .expect(200);
    expect(upload.body.results).toHaveLength(1);
    expect(upload.body.results[0]).toMatchObject({ event_id: EVENT_ID, revision: 1, status: "accepted" });

    // Device identity is server-bound to the credential, not client-claimed.
    const listed = await request(app).get(readUrl()).set("Cookie", cookie).expect(200);
    expect(listed.body.data).toHaveLength(1);
    expect(listed.body.data[0].event_id).toBe(EVENT_ID);
    expect(listed.body.data[0].device).toEqual({ id: device.id, platform: "windows" });
    expect(listed.body.data[0].payload).toEqual(envelopeItem().payload);
  });

  it("returns duplicate for re-delivered event identifiers without duplicating data", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createDevice(cookie);

    for (let attempt = 0; attempt < 3; attempt++) {
      const upload = await request(app)
        .post("/api/v1/events/batch")
        .set("Authorization", `Bearer ${device.token}`)
        .send({ events: [envelopeItem()] })
        .expect(200);
      expect(upload.body.results[0].status).toBe(attempt === 0 ? "accepted" : "duplicate");
    }

    const listed = await request(app).get(readUrl()).set("Cookie", cookie).expect(200);
    expect(listed.body.data).toHaveLength(1);
  });

  it("rejects items whose event type is not on the credential allow-list", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    // Allow-list names need only be contract-valid patterns; the uploaded
    // activity.interval item is registered but not on this list.
    const device = await createDevice(cookie, { allowed_event_types: ["future.type"] });

    const upload = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device.token}`)
      .send({ events: [envelopeItem()] })
      .expect(200);
    expect(upload.body.results[0].status).toBe("rejected");
    expect(upload.body.results[0].error.code).toBe("event_type_not_allowed");

    const listed = await request(app).get(readUrl()).set("Cookie", cookie).expect(200);
    expect(listed.body.data).toHaveLength(0);
  });

  it("rejects items whose privacy level exceeds the credential ceiling", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const capped = await createDevice(cookie, { privacy_ceiling: "normal" });
    const wider = await createDevice(cookie, { name: "更宽设备", privacy_ceiling: "sensitive" });

    const cappedUpload = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${capped.token}`)
      .send({ events: [envelopeItem({ privacy_level: "sensitive" })] })
      .expect(200);
    expect(cappedUpload.body.results[0].status).toBe("rejected");
    expect(cappedUpload.body.results[0].error.code).toBe("privacy_ceiling_exceeded");

    const widerUpload = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${wider.token}`)
      .send({ events: [envelopeItem({ privacy_level: "sensitive" })] })
      .expect(200);
    expect(widerUpload.body.results[0].status).toBe("accepted");
  });

  it("rejects contract-invalid items with stable error codes while accepting valid ones", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createDevice(cookie);

    const upload = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device.token}`)
      .send({
        events: [
          envelopeItem({ event_id: "3f2504e0-4f89-11d3-9a0c-0305e82c3302", event_type: "not.registered" }),
          envelopeItem({ event_id: "3f2504e0-4f89-11d3-9a0c-0305e82c3303", schema_version: 2 }),
          envelopeItem({ event_id: "3f2504e0-4f89-11d3-9a0c-0305e82c3304", start_at: "not-a-time" }),
          envelopeItem({ event_id: "3f2504e0-4f89-11d3-9a0c-0305e82c3305", owner_id: "someone-else" }),
        ],
      })
      .expect(200);
    expect(upload.body.results.map((result: { error?: { code: string } }) => result.error?.code)).toEqual([
      "unknown_event_type",
      "unknown_schema_version",
      "invalid_event",
      "invalid_event",
    ]);
  });

  it("rejects batch uploads for query tokens and unauthenticated callers", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const query = await createCredential(cookie, { name: "只读探针" });

    const queryResponse = await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${query.token}`)
      .send({ events: [envelopeItem()] })
      .expect(403);
    expect(queryResponse.body.error.code).toBe("insufficient_scope");

    await request(app)
      .post("/api/v1/events/batch")
      .send({ events: [envelopeItem()] })
      .expect(401);

    await request(app)
      .get("/api/v1/credentials")
      .set("Authorization", `Bearer ${(await createDevice(cookie)).token}`)
      .expect(401);
  });

  it("audits device use without storing token material", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createDevice(cookie);

    await request(app)
      .post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device.token}`)
      .send({ events: [envelopeItem()] })
      .expect(200);

    const audits = await AuditLogModel.find({ action: "credential.use" }).lean();
    const deviceUse = audits.find((entry) => entry.actor_type === "device");
    expect(deviceUse?.actor_id).toBe(device.id);
    expect(JSON.stringify(audits)).not.toContain(device.token);
  });
});
