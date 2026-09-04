import { randomUUID } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { AuditLogModel, CredentialModel, EventModel, EventRevisionModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_source_policy";
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
    console.warn(`[source-policy.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[source-policy.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    EventModel.syncIndexes(),
    EventRevisionModel.syncIndexes(),
    CredentialModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
    AuditLogModel.syncIndexes(),
  ]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

function setCookieFor(response: Response, name: string): string | undefined {
  const values = response.headers["set-cookie"];
  const list = Array.isArray(values) ? values : values ? [values] : [];
  return list.find((value) => value.startsWith(`${name}=`));
}

async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).then((response) => {
    if (response.status !== 204 && response.status !== 409) {
      throw new Error(`Owner setup failed with status ${response.status}.`);
    }
  });
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

async function createDeviceCredential(scopes: string[]): Promise<string> {
  const cookie = await ownerCookie();
  const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
    .send({ kind: "device_token", name: `device ${randomUUID()}`, scopes, privacy_ceiling: "sensitive" })
    .expect(201);
  return created.body.token as string;
}

interface IntervalOverrides {
  id: string;
  device_id?: string;
  device_platform?: string;
  source_kind?: string;
  start_at: string;
  end_at?: string | null;
  is_afk?: boolean;
  application_id?: string;
}

/** Inserts one activity interval row with contract envelope columns and a stable event identifier. */
async function seedInterval(overrides: IntervalOverrides): Promise<void> {
  const start = new Date(overrides.start_at);
  const end = overrides.end_at === undefined ? new Date(start.getTime() + 60_000) : overrides.end_at === null ? null : new Date(overrides.end_at);
  const now = new Date();
  const duration = end ? end.getTime() - start.getTime() : 0;
  await EventModel.create({
    id: overrides.id,
    bucket_id: `bucket:${overrides.device_id ?? "device-a"}:activity.interval`,
    user_id: "test-user",
    device_id: overrides.device_id ?? "device-a",
    source: overrides.source_kind ?? "windows.foreground",
    type: "activity.interval",
    schema_version: 1,
    revision: 1,
    finalization_state: end ? "final" : "checkpoint",
    provenance: { collector_version: "0.1.0", observed_at: now.toISOString() },
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    invalidated: false,
    source_kind: overrides.source_kind ?? "windows.foreground",
    source_record_id: `${overrides.id}-record`,
    device_platform: overrides.device_platform ?? "windows",
    start_at: start,
    end_at: end,
    duration_ms: end ? duration : null,
    value: null,
    unit: null,
    data: { application_id: overrides.application_id ?? "idea64.exe", is_afk: overrides.is_afk ?? false, duration: { value: duration, unit: "ms" } },
    privacy_level: "normal",
    confidence: 1,
    raw_hash: null,
    created_at: now,
    updated_at: now,
  });
}

/** Snapshot of every stored event for observation-immutability assertions. */
async function eventSnapshot(): Promise<Map<string, unknown>> {
  const rows = await EventModel.find().lean<{ id: string; revision: number | null; updated_at: Date; data: unknown }[]>();
  return new Map(rows.map((row) => [row.id, { revision: row.revision, updated_at: row.updated_at.toISOString(), data: row.data }]));
}

function dayUrl(date: string): string {
  return `/api/v1/metrics/usage/day?date=${date}&timezone=UTC`;
}

function healthUrl(from: string, to: string): string {
  return `/api/v1/health/events?from=${from}&to=${to}&timezone=UTC`;
}

/** A Health Connect sleep session reported by one origin app. */
function sleepItem(eventId: string, origin: string, startAt: string, endAt: string): Record<string, unknown> {
  const duration = Date.parse(endAt) - Date.parse(startAt);
  return {
    event_id: eventId,
    event_type: "health.sleep.session",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.healthconnect", record_id: `hc-${eventId}` },
    device: { id: "pixel-8", platform: "android" },
    start_at: startAt,
    end_at: endAt,
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T08:00:00.000Z" },
    invalidated: false,
    payload: { duration: { value: duration, unit: "ms" }, data_origin: origin },
  };
}

/** A structured WeChat Pay transaction fact; notification text never exists here. */
function paymentItem(eventId: string, pending: boolean, startAt: string): Record<string, unknown> {
  return {
    event_id: eventId,
    event_type: "payment.transaction",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.wechatpay", record_id: `wechat-notification-${eventId}` },
    device: { id: "pixel-8", platform: "android" },
    start_at: startAt,
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T08:00:00.000Z" },
    invalidated: false,
    payload: {
      amount: { value: 2150, currency: "CNY" },
      direction: "expense",
      merchant: "瑞幸咖啡",
      category: "food",
      pending_confirmation: pending,
    },
  };
}

async function uploadBatch(token: string, events: Record<string, unknown>[]): Promise<Response> {
  return request(app).post("/api/v1/events/batch").set("Authorization", `Bearer ${token}`).send({ events });
}

describe("GET /api/v1/source-policy", () => {
  it("returns the default versioned policy until the Owner changes it", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    const response = await request(app).get("/api/v1/source-policy").set("Cookie", cookie).expect(200);
    expect(response.body).toEqual({
      version: 1,
      updated_at: null,
      entries: [
        { metric: "usage.app_minutes", priority: ["windows.foreground", "android.usagestats", "android.accessibility"] },
        { metric: "health.step_total", priority: [] },
        { metric: "health.sleep_minutes", priority: [] },
        { metric: "health.heartrate_average", priority: [] },
        { metric: "payment.transaction_totals", priority: ["android.wechatpay"] },
      ],
    });
  });

  it("requires an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app).get("/api/v1/source-policy").expect(401);
  });
});

describe("usage metrics source selection", () => {
  it("selects the authoritative source per device and reports conflicts with source event identifiers", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    // Deterministic Android app-duration sample: one UsageStats session plus
    // two competing accessibility observations of the same device.
    await seedInterval({ id: "u1", device_id: "pixel-8", device_platform: "android", source_kind: "android.usagestats", start_at: "2026-09-01T10:00:00Z", end_at: "2026-09-01T10:30:00Z", application_id: "tv.danmaku.bili" });
    await seedInterval({ id: "a1", device_id: "pixel-8", device_platform: "android", source_kind: "android.accessibility", start_at: "2026-09-01T10:10:00Z", end_at: "2026-09-01T10:20:00Z" });
    await seedInterval({ id: "a2", device_id: "pixel-8", device_platform: "android", source_kind: "android.accessibility", start_at: "2026-09-01T11:00:00Z", end_at: "2026-09-01T11:15:00Z" });

    const response = await request(app).get(dayUrl("2026-09-01")).set("Cookie", cookie).expect(200);
    // UsageStats is authoritative: the accessibility intervals never enter the totals.
    expect(response.body.metrics).toEqual({ device_minutes: 30, active_minutes: 30 });
    expect(response.body.devices).toEqual([
      { device_id: "pixel-8", platform: "android", device_minutes: 30, active_minutes: 30 },
    ]);
    expect(response.body.context.source_policy_version).toBe(1);
    expect(response.body.context.data_state).toBe("observed");
    expect(response.body.context.source_conflicts).toEqual([
      {
        metric: "usage.app_minutes",
        policy_version: 1,
        selected_source: "android.usagestats",
        selected_event_ids: ["u1"],
        competing_sources: ["android.accessibility"],
        competing_event_ids: ["a1"],
        from: "2026-09-01T10:10:00.000Z",
        to: "2026-09-01T10:20:00.000Z",
      },
      {
        metric: "usage.app_minutes",
        policy_version: 1,
        selected_source: "android.usagestats",
        selected_event_ids: [],
        competing_sources: ["android.accessibility"],
        competing_event_ids: ["a2"],
        from: "2026-09-01T11:00:00.000Z",
        to: "2026-09-01T11:15:00.000Z",
      },
    ]);
  });

  it("keeps single-source reports exactly as before, without conflict entries", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ id: "w1", device_id: "device-a", source_kind: "windows.foreground", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T02:00:00Z" });
    await seedInterval({ id: "u1", device_id: "pixel-8", device_platform: "android", source_kind: "android.usagestats", start_at: "2026-09-01T02:00:00Z", end_at: "2026-09-01T02:30:00Z" });

    const response = await request(app).get(dayUrl("2026-09-01")).set("Cookie", cookie).expect(200);
    expect(response.body.metrics).toEqual({ device_minutes: 90, active_minutes: 90 });
    expect(response.body.context.provenance).toEqual(["android.usagestats", "windows.foreground"]);
    expect(response.body.context.source_policy_version).toBe(1);
    expect(response.body.context.data_state).toBe("observed");
    expect(response.body.context.source_conflicts).toBeUndefined();
  });
});

describe("data state semantics", () => {
  it("distinguishes explicit zero (observations, no duration) from no-data (empty range)", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ id: "z1", device_id: "device-a", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T01:00:00Z" });

    const zeroDay = await request(app).get(dayUrl("2026-09-01")).set("Cookie", cookie).expect(200);
    expect(zeroDay.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });
    expect(zeroDay.body.context.data_state).toBe("zero");

    const emptyDay = await request(app).get(dayUrl("2026-09-02")).set("Cookie", cookie).expect(200);
    expect(emptyDay.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });
    expect(emptyDay.body.context.data_state).toBe("no_data");
  });

  it("marks event pages as observed or no_data in the query context", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const readUrl = "/api/v1/events?from=2026-09-01T00:00:00.000Z&to=2026-09-02T00:00:00.000Z&timezone=UTC";

    const emptyPage = await request(app).get(readUrl).set("Cookie", cookie).expect(200);
    expect(emptyPage.body.context.data_state).toBe("no_data");

    await seedInterval({ id: "w1", device_id: "device-a", start_at: "2026-09-01T01:00:00Z" });
    const populatedPage = await request(app).get(readUrl).set("Cookie", cookie).expect(200);
    expect(populatedPage.body.context.data_state).toBe("observed");
  });
});

describe("health domain selection", () => {
  it("retains both origins and reports the policy selection with source event identifiers", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceCredential(["events:write", "health:write"]);
    // Deterministic Health Connect multi-origin sample: two origins report the
    // same night, plus disjoint step observations that never compete.
    await uploadBatch(token, [
      sleepItem("a1b2c3d4-0001-4000-8000-000000000001", "com.google.android.apps.fitness", "2026-08-01T15:00:00.000Z", "2026-08-01T22:00:00.000Z"),
      sleepItem("a1b2c3d4-0002-4000-8000-000000000002", "com.mi.health", "2026-08-01T15:30:00.000Z", "2026-08-01T21:00:00.000Z"),
    ]);

    const page = await request(app).get(healthUrl("2026-08-01T00:00:00.000Z", "2026-08-02T00:00:00.000Z")).set("Cookie", cookie).expect(200);
    expect(page.body.data).toHaveLength(2); // both origins retained, nothing deleted
    expect(page.body.context.source_policy_version).toBe(1);
    expect(page.body.context.data_state).toBe("observed");
    expect(page.body.context.source_conflicts).toEqual([
      {
        metric: "health.sleep_minutes",
        policy_version: 1,
        selected_source: "com.google.android.apps.fitness",
        selected_event_ids: ["a1b2c3d4-0001-4000-8000-000000000001"],
        competing_sources: ["com.mi.health"],
        competing_event_ids: ["a1b2c3d4-0002-4000-8000-000000000002"],
        from: "2026-08-01T15:30:00.000Z",
        to: "2026-08-01T21:00:00.000Z",
      },
    ]);
    expect(page.body.context.pending_confirmation_count).toBeUndefined();
  });
});

describe("payment domain coverage", () => {
  it("reports the pending-confirmation count as the ambiguity surface", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceCredential(["events:write", "payment:write"]);
    await uploadBatch(token, [
      paymentItem("a1b2c3d4-0003-4000-8000-000000000003", false, "2026-08-01T01:30:00.000Z"),
      paymentItem("a1b2c3d4-0004-4000-8000-000000000004", true, "2026-08-01T02:00:00.000Z"),
    ]);

    const page = await request(app).get("/api/v1/payment/events?from=2026-08-01T00:00:00.000Z&to=2026-08-02T00:00:00.000Z&timezone=UTC")
      .set("Cookie", cookie).expect(200);
    expect(page.body.data).toHaveLength(2); // ambiguous candidates stay retained
    expect(page.body.context.pending_confirmation_count).toBe(1);
    expect(page.body.context.source_policy_version).toBe(1);
    expect(page.body.context.data_state).toBe("observed");
    expect(page.body.context.source_conflicts).toBeUndefined();
  });
});

describe("PUT /api/v1/source-policy", () => {
  it("bumps the version, states the affected ranges and result counts, audits, and never touches observations", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ id: "u1", device_id: "pixel-8", device_platform: "android", source_kind: "android.usagestats", start_at: "2026-09-01T10:00:00Z", end_at: "2026-09-01T10:30:00Z" });
    await seedInterval({ id: "a1", device_id: "pixel-8", device_platform: "android", source_kind: "android.accessibility", start_at: "2026-09-01T10:10:00Z", end_at: "2026-09-01T10:20:00Z" });
    await seedInterval({ id: "u2", device_id: "pixel-8", device_platform: "android", source_kind: "android.usagestats", start_at: "2026-09-02T10:00:00Z", end_at: "2026-09-02T10:20:00Z" });
    await seedInterval({ id: "a2", device_id: "pixel-8", device_platform: "android", source_kind: "android.accessibility", start_at: "2026-09-02T10:00:00Z", end_at: "2026-09-02T10:15:00Z" });
    const before = await eventSnapshot();

    const update = await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({ entries: [{ metric: "usage.app_minutes", priority: ["android.accessibility", "android.usagestats", "windows.foreground"] }] })
      .expect(200);
    expect(update.body.version).toBe(2);
    expect(update.body.updated_at).toBeTruthy();
    expect(update.body.impact).toEqual([
      {
        metric: "usage.app_minutes",
        from_version: 1,
        to_version: 2,
        timezone: "UTC",
        affected_ranges: [{ from: "2026-09-01", to: "2026-09-01" }, { from: "2026-09-02", to: "2026-09-02" }],
        result_count: 2,
      },
    ]);

    const document = await request(app).get("/api/v1/source-policy").set("Cookie", cookie).expect(200);
    expect(document.body.version).toBe(2);
    expect(document.body.entries).toEqual([
      { metric: "usage.app_minutes", priority: ["android.accessibility", "android.usagestats", "windows.foreground"] },
      { metric: "health.step_total", priority: [] },
      { metric: "health.sleep_minutes", priority: [] },
      { metric: "health.heartrate_average", priority: [] },
      { metric: "payment.transaction_totals", priority: ["android.wechatpay"] },
    ]);

    // Derived results rebuild on read under the new priority.
    const day = await request(app).get(dayUrl("2026-09-01")).set("Cookie", cookie).expect(200);
    expect(day.body.metrics).toEqual({ device_minutes: 10, active_minutes: 10 });
    expect(day.body.context.source_policy_version).toBe(2);
    expect(day.body.context.source_conflicts?.[0]).toMatchObject({
      policy_version: 2,
      selected_source: "android.accessibility",
      selected_event_ids: ["a1"],
      competing_event_ids: ["u1"],
    });

    // The audit states which time ranges and how many results the change affected.
    const audit = await AuditLogModel.findOne({ action: "source_policy.update" }).lean<{ details: Record<string, unknown> } | null>();
    expect(audit?.details).toMatchObject({
      from_version: 1,
      to_version: 2,
      timezone: "UTC",
      result_count: 2,
    });

    // Raw observations are never modified: same revisions, payloads, timestamps.
    expect(await eventSnapshot()).toEqual(before);
    expect(await EventRevisionModel.countDocuments()).toBe(0);
  });

  it("bumps the version with an empty impact when nothing competes differently", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const update = await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({ entries: [{ metric: "usage.app_minutes", priority: ["windows.foreground", "android.usagestats", "android.accessibility"] }] })
      .expect(200);
    expect(update.body.version).toBe(2);
    expect(update.body.impact).toEqual([]);
  });

  it("rejects unknown metric keys, illegal source kinds, and path-shaped origins", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({ entries: [{ metric: "bogus.metric", priority: [] }] }).expect(400);
    await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({ entries: [{ metric: "usage.app_minutes", priority: ["not-a-source-kind"] }] }).expect(400);
    await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({ entries: [{ metric: "health.sleep_minutes", priority: ["C:\\com\\evil"] }] }).expect(400);
    // Every entry is validated, including entries after a legal health metric.
    await request(app).put("/api/v1/source-policy").set("Cookie", cookie)
      .send({
        entries: [
          { metric: "health.sleep_minutes", priority: ["com.mi.health"] },
          { metric: "usage.app_minutes", priority: ["not-a-source-kind"] },
        ],
      }).expect(400);
    await request(app).put("/api/v1/source-policy").set("Cookie", cookie).send({}).expect(400);

    const document = await request(app).get("/api/v1/source-policy").set("Cookie", cookie).expect(200);
    expect(document.body.version).toBe(1); // failed updates never advance the version
  });

  it("is Owner-only", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
      .send({ kind: "query_token", name: `query ${randomUUID()}`, scopes: ["events:read", "health:read", "payment:read"], privacy_ceiling: "sensitive" })
      .expect(201);
    const token = created.body.token as string;
    await request(app).put("/api/v1/source-policy").set("Authorization", `Bearer ${token}`)
      .send({ entries: [] }).expect(401);
  });
});
