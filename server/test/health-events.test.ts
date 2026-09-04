import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_health_events";
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
    console.warn(`[health-events.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[health-events.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
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

async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  const setCookie = Array.isArray(login.headers["set-cookie"]) ? login.headers["set-cookie"].at(0) : login.headers["set-cookie"];
  return sessionCookieValue(setCookie ?? "");
}

async function createCredential(
  cookie: string,
  kind: "device_token" | "query_token",
  scopes: string[],
  privacyCeiling: "normal" | "sensitive" | "private" = "normal",
): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind, name: "健康测试凭据", scopes, privacy_ceiling: privacyCeiling })
    .expect(201);
  return response.body.token;
}

/** A Health Connect steps record for a walk, reported by the Fit origin app. */
function stepItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0001-4000-8000-000000000001",
    event_type: "health.step.sample",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.healthconnect", record_id: "hc-record-steps-0001" },
    device: { id: "pixel-8", platform: "android" },
    start_at: "2026-08-01T01:30:00.000Z",
    end_at: "2026-08-01T02:00:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T02:05:00.000Z" },
    invalidated: false,
    payload: {
      count: { value: 2415, unit: "steps" },
      data_origin: "com.google.android.apps.fitness",
    },
    ...overrides,
  };
}

/** An instantaneous heart rate sample without end_at. */
function heartRateItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0002-4000-8000-000000000002",
    event_type: "health.heartrate.sample",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.healthconnect", record_id: "hc-record-hr-0002" },
    device: { id: "pixel-8", platform: "android" },
    start_at: "2026-08-01T02:14:30.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T02:05:00.000Z" },
    invalidated: false,
    payload: {
      beats_per_minute: 62,
      data_origin: "com.mi.health",
    },
    ...overrides,
  };
}

/** A source-provided sleep session with exact bounds and matching duration. */
function sleepItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0003-4000-8000-000000000003",
    event_type: "health.sleep.session",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.healthconnect", record_id: "hc-record-sleep-0003" },
    device: { id: "pixel-8", platform: "android" },
    start_at: "2026-07-31T22:10:00.000Z",
    end_at: "2026-08-01T05:46:30.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T08:00:00.000Z" },
    invalidated: false,
    payload: {
      duration: { value: 27_390_000, unit: "ms" },
      data_origin: "com.urbandroid.sleep",
    },
    ...overrides,
  };
}

/** A foreground activity interval for scope-interaction assertions. */
function activityItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0004-4000-8000-000000000004",
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.usagestats", record_id: "usage-session-tv.danmaku.bili-1754043000000" },
    device: { id: "pixel-8", platform: "android" },
    start_at: "2026-08-01T13:30:00.000Z",
    end_at: "2026-08-01T14:05:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 1,
    finalization_state: "checkpoint",
    provenance: { collector_version: "0.2.0", observed_at: "2026-08-01T14:06:00.000Z" },
    invalidated: false,
    payload: {
      application_id: "tv.danmaku.bili",
      is_afk: false,
      duration: { value: 2_100_000, unit: "ms" },
    },
    ...overrides,
  };
}

async function uploadBatch(token: string, events: Record<string, unknown>[]) {
  return request(app)
    .post("/api/v1/events/batch")
    .set("Authorization", `Bearer ${token}`)
    .send({ events })
    .expect(200);
}

describe("health connect observations", () => {
  it("accepts steps, heart rate, and sleep with their data origin and reads them back with source attribution", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createCredential(cookie, "device_token", ["events:write", "health:write"], "sensitive");

    const batch = await uploadBatch(token, [stepItem(), heartRateItem(), sleepItem()]);
    expect(batch.body.results.map((result: { status: string }) => result.status)).toEqual(["accepted", "accepted", "accepted"]);

    // Redelivery stays one logical fact per record identity.
    const redelivery = await uploadBatch(token, [stepItem()]);
    expect(redelivery.body.results[0]).toMatchObject({ event_id: stepItem().event_id, revision: 1, status: "duplicate" });

    const healthPage = await request(app)
      .get("/api/v1/health/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(healthPage.body.data).toHaveLength(3);
    const steps = healthPage.body.data.find((event: { event_type: string }) => event.event_type === "health.step.sample");
    expect(steps.source).toEqual({ kind: "android.healthconnect", record_id: "hc-record-steps-0001" });
    expect(steps.payload).toEqual({ count: { value: 2415, unit: "steps" }, data_origin: "com.google.android.apps.fitness" });
    expect(steps.privacy_level).toBe("sensitive");
    const heartRate = healthPage.body.data.find((event: { event_type: string }) => event.event_type === "health.heartrate.sample");
    expect(heartRate.end_at).toBeUndefined();
    expect(heartRate.payload.data_origin).toBe("com.mi.health");
    expect(healthPage.body.context.provenance).toEqual(["android.healthconnect"]);

    // The generic event read surfaces the same facts for the Owner session.
    const allPage = await request(app)
      .get("/api/v1/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(allPage.body.data).toHaveLength(3);
  });

  it("enforces health:write per item while permitted domains still progress in the same batch", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const activityOnly = await createCredential(cookie, "device_token", ["events:write"], "sensitive");
    const healthOnly = await createCredential(cookie, "device_token", ["health:write"], "sensitive");

    const mixed = await uploadBatch(activityOnly, [activityItem(), stepItem()]);
    expect(mixed.body.results[0]).toMatchObject({ status: "accepted" });
    expect(mixed.body.results[1]).toMatchObject({ status: "rejected", error: { code: "insufficient_scope" } });

    const healthUpload = await uploadBatch(healthOnly, [stepItem({ event_id: "b1b2c3d4-0001-4000-8000-000000000001" }), activityItem()]);
    expect(healthUpload.body.results[0]).toMatchObject({ status: "accepted" });
    expect(healthUpload.body.results[1]).toMatchObject({ status: "rejected", error: { code: "insufficient_scope" } });
  });

  it("restricts health reads to health:read query tokens and never leaks them to events:read tokens", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "health:write"], "sensitive");
    await uploadBatch(device, [stepItem(), heartRateItem(), activityItem()]);

    const activityReader = await createCredential(cookie, "query_token", ["events:read"], "sensitive");
    const healthDenied = await request(app)
      .get("/api/v1/health/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Authorization", `Bearer ${activityReader}`)
      .expect(403);
    expect(healthDenied.body.error.code).toBe("insufficient_scope");

    // The generic read is domain-scoped: health events are hidden and the
    // context reports the page as partial instead of pretending completeness.
    const genericPage = await request(app)
      .get("/api/v1/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Authorization", `Bearer ${activityReader}`)
      .expect(200);
    expect(genericPage.body.data.map((event: { event_type: string }) => event.event_type)).toEqual(["activity.interval"]);
    expect(genericPage.body.context.completeness).toBe("partial");

    const healthReader = await createCredential(cookie, "query_token", ["events:read", "health:read"], "sensitive");
    const healthPage = await request(app)
      .get("/api/v1/health/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Authorization", `Bearer ${healthReader}`)
      .expect(200);
    expect(healthPage.body.data).toHaveLength(2);
    expect(healthPage.body.context.completeness).toBe("complete");

    // A health-only token cannot use the generic read (events:read required).
    const healthOnlyReader = await createCredential(cookie, "query_token", ["health:read"], "sensitive");
    await request(app)
      .get("/api/v1/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Authorization", `Bearer ${healthOnlyReader}`)
      .expect(403);
    const healthOnlyPage = await request(app)
      .get("/api/v1/health/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Authorization", `Bearer ${healthOnlyReader}`)
      .expect(200);
    expect(healthOnlyPage.body.data).toHaveLength(2);
  });

  it("retains observations from different data origins even when values are similar or intervals overlap", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "health:write"], "sensitive");

    // Two sleep applications report the same night with overlapping intervals.
    await uploadBatch(device, [
      sleepItem(),
      sleepItem({
        event_id: "c1b2c3d4-0003-4000-8000-000000000003",
        source: { kind: "android.healthconnect", record_id: "hc-record-sleep-wearable-0003" },
        start_at: "2026-07-31T23:00:00.000Z",
        end_at: "2026-08-01T06:30:00.000Z",
        payload: {
          duration: { value: 27_000_000, unit: "ms" },
          data_origin: "com.wearable.sleep",
        },
      }),
      // A watch and a phone report nearly identical step counts for the same walk.
      stepItem(),
      stepItem({
        event_id: "c1b2c3d4-0001-4000-8000-000000000001",
        source: { kind: "android.healthconnect", record_id: "hc-record-steps-watch-0001" },
        payload: {
          count: { value: 2400, unit: "steps" },
          data_origin: "com.wearable.fitness",
        },
      }),
    ]);

    const page = await request(app)
      .get("/api/v1/health/events?from=2026-07-31T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(page.body.data).toHaveLength(4);
    const origins = page.body.data.map((event: { payload: { data_origin: string } }) => event.payload.data_origin).sort();
    expect(origins).toEqual(["com.google.android.apps.fitness", "com.urbandroid.sleep", "com.wearable.fitness", "com.wearable.sleep"].sort());
  });

  it("defaults health observations to sensitive so a normal-ceiling credential cannot upload them", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["health:write"], "normal");

    // privacy_level omitted: the registered schema defaults health to sensitive,
    // which the normal ceiling rejects per item with a diagnosable code.
    const omitted = stepItem();
    delete (omitted as Record<string, unknown>).privacy_level;
    const rejected = await uploadBatch(device, [omitted]);
    expect(rejected.body.results[0]).toMatchObject({ status: "rejected", error: { code: "privacy_ceiling_exceeded" } });

    const rows = await EventModel.countDocuments({ type: "health.step.sample" });
    expect(rows).toBe(0);
  });

  it("rejects invalid or unauthorized health records with stable diagnosable error codes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "health:write"], "sensitive");

    const wrongUnit = stepItem({ event_id: "d1b2c3d4-0001-4000-8000-000000000001" });
    (wrongUnit.payload as { count: { unit: string } }).count.unit = "count";
    const missingOrigin = stepItem({ event_id: "d1b2c3d4-0002-4000-8000-000000000002" });
    delete (missingOrigin.payload as Record<string, unknown>).data_origin;
    const missingEnd = stepItem({ event_id: "d1b2c3d4-0003-4000-8000-000000000003" });
    delete (missingEnd as Record<string, unknown>).end_at;
    const instantWithEnd = heartRateItem({ event_id: "d1b2c3d4-0004-4000-8000-000000000004", end_at: "2026-08-01T02:20:00.000Z" });
    const implausibleBpm = heartRateItem({ event_id: "d1b2c3d4-0005-4000-8000-000000000005" });
    (implausibleBpm.payload as { beats_per_minute: number }).beats_per_minute = 12;
    const durationMismatch = sleepItem({ event_id: "d1b2c3d4-0006-4000-8000-000000000006" });
    (durationMismatch.payload as { duration: { value: number } }).duration.value = 18_000_000;
    const wrongSourceKind = stepItem({
      event_id: "d1b2c3d4-0007-4000-8000-000000000007",
      source: { kind: "android.usagestats", record_id: "usage-session-not-health" },
    });
    const unknownType = stepItem({ event_id: "d1b2c3d4-0008-4000-8000-000000000008", event_type: "health.blood_pressure" });

    const batch = await uploadBatch(device, [
      wrongUnit,
      missingOrigin,
      missingEnd,
      instantWithEnd,
      implausibleBpm,
      durationMismatch,
      wrongSourceKind,
      unknownType,
    ]);
    expect(batch.body.results).toHaveLength(8);
    expect(batch.body.results.map((result: { status: string }) => result.status)).toEqual(Array.from({ length: 8 }, () => "rejected"));
    expect(batch.body.results.every((result: { error?: { code?: string } }) => result.error?.code === "invalid_event"
      || result.error?.code === "unknown_event_type")).toBe(true);
    expect(batch.body.results.at(-1).error?.code).toBe("unknown_event_type");

    const rows = await EventModel.countDocuments({});
    expect(rows).toBe(0);
  });

  it("never counts health observations as usage time in day metrics", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "health:write"], "sensitive");
    await uploadBatch(device, [stepItem(), heartRateItem(), sleepItem()]);

    const response = await request(app)
      .get("/api/v1/metrics/usage/day?date=2026-08-01&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(response.body.metrics.device_minutes).toBe(0);
    expect(response.body.metrics.active_minutes).toBe(0);
    expect(response.body.context.provenance).toEqual([]);
  });
});
