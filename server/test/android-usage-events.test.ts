import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_android_usage";
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
    console.warn(`[android-usage-events.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[android-usage-events.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
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

async function createDeviceToken(cookie: string): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind: "device_token", name: "安卓采集器", scopes: ["events:write"] })
    .expect(201);
  return response.body.token;
}

/** A UsageStats foreground session for Bilibili on a Pixel, in the report day 2026-08-01 (Asia/Shanghai). */
function usageStatsItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: overrides.event_id ?? "3f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
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
    provenance: { collector_version: "0.1.0", observed_at: "2026-08-01T14:06:00.000Z" },
    invalidated: false,
    payload: {
      application_id: "tv.danmaku.bili",
      application_label: "哔哩哔哩",
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

describe("android usagestats activity intervals", () => {
  it("accepts the authoritative source through checkpoint and final revisions and preserves source identity on read", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const checkpoint = await uploadBatch(token, [usageStatsItem()]);
    expect(checkpoint.body.results[0]).toMatchObject({
      event_id: "3f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
      revision: 1,
      status: "accepted",
    });

    const final = await uploadBatch(token, [
      usageStatsItem({
        revision: 2,
        finalization_state: "final",
        provenance: { collector_version: "0.1.0", observed_at: "2026-08-01T14:20:00.000Z" },
      }),
    ]);
    expect(final.body.results[0]).toMatchObject({ revision: 2, status: "accepted" });

    const response = await request(app)
      .get("/api/v1/events?from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(response.body.data).toHaveLength(1);
    const event = response.body.data.at(0);
    expect(event.source).toEqual({ kind: "android.usagestats", record_id: "usage-session-tv.danmaku.bili-1754043000000" });
    expect(event.device).toEqual({ id: expect.stringMatching(/^cred_/), platform: "android" });
    expect(event.revision).toBe(2);
    expect(event.finalization_state).toBe("final");
    expect(response.body.context.provenance).toEqual(["android.usagestats"]);
  });

  it("never duplicates or loses daily app facts under repeated redelivery and stale revisions", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const statuses: string[] = [];
    for (let attempt = 0; attempt < 10; attempt++) {
      const upload = await uploadBatch(token, [
        usageStatsItem({
          event_id: "4f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
          source: { kind: "android.usagestats", record_id: "usage-session-com.example.app-1754043000000" },
          revision: 2,
          finalization_state: "final",
        }),
      ]);
      statuses.push(upload.body.results[0].status);
    }
    expect(statuses.at(0)).toBe("accepted");
    expect(statuses.slice(1)).toEqual(Array.from({ length: 9 }, () => "duplicate"));

    // A delayed redelivery of the superseded checkpoint must not roll the fact back.
    const stale = await uploadBatch(token, [
      usageStatsItem({
        event_id: "4f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
        source: { kind: "android.usagestats", record_id: "usage-session-com.example.app-1754043000000" },
        revision: 1,
      }),
    ]);
    expect(stale.body.results[0]).toMatchObject({ revision: 1, status: "stale_revision" });

    const rows = await EventModel.countDocuments({ id: "4f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f" });
    expect(rows).toBe(1);
    const response = await request(app)
      .get("/api/v1/events?from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(response.body.data).toHaveLength(1);
    expect(response.body.data.at(0).revision).toBe(2);
    expect(response.body.data.at(0).finalization_state).toBe("final");
  });

  it("counts UsageStats intervals as daily device time while heartbeats contribute nothing", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    // Two non-overlapping UsageStats sessions: 35 min + 20 min = 55 device minutes.
    await uploadBatch(token, [
      usageStatsItem(),
      usageStatsItem({
        event_id: "6f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
        source: { kind: "android.usagestats", record_id: "usage-session-tv.danmaku.bili-1754050000000" },
        start_at: "2026-08-01T15:00:00.000Z",
        end_at: "2026-08-01T15:20:00.000Z",
        payload: {
          application_id: "tv.danmaku.bili",
          is_afk: false,
          duration: { value: 1_200_000, unit: "ms" },
        },
      }),
    ]);

    // The accessibility realtime state reports the same app as currently foreground.
    await request(app)
      .post("/api/v1/heartbeats")
      .set("Authorization", `Bearer ${token}`)
      .send({
        platform: "android",
        device_name: "Pixel 8",
        captured_at: "2026-08-01T15:10:00.000Z",
        activity: { application_id: "tv.danmaku.bili", application_label: "哔哩哔哩", is_afk: false },
      })
      .expect(204);

    const response = await request(app)
      .get("/api/v1/metrics/usage/day?date=2026-08-01&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(response.body.metrics.device_minutes).toBe(55);
    expect(response.body.metrics.active_minutes).toBe(55);
    const lane = response.body.devices.at(0);
    expect(lane).toMatchObject({ device_id: expect.any(String), platform: "android", device_minutes: 55, active_minutes: 55 });
    // Heartbeats are not historical evidence: the accessibility source never appears in report provenance.
    expect(response.body.context.provenance).toEqual(["android.usagestats"]);

    const events = await EventModel.countDocuments({ device_platform: "android" });
    expect(events).toBe(2); // the two interval events only; the heartbeat never became an event
  });

  it("rejects UsageStats events with unknown source kinds, non-permitted types, or privacy levels beyond the ceiling", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const rejected = await uploadBatch(token, [
      usageStatsItem({
        event_id: "7f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
        source: { kind: "android.usage_stats", record_id: "usage-session-x" },
      }),
      usageStatsItem({
        event_id: "8f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
        privacy_level: "sensitive",
      }),
      usageStatsItem({
        event_id: "9f2c9d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f",
        event_type: "usage.app_daily",
      }),
    ]);
    expect(rejected.body.results.map((result: { status: string }) => result.status)).toEqual([
      "rejected",
      "rejected",
      "rejected",
    ]);
    expect(rejected.body.results.map((result: { error?: { code?: string } }) => result.error?.code)).toEqual([
      "invalid_event",
      "privacy_ceiling_exceeded",
      "unknown_event_type",
    ]);

    const page = await request(app)
      .get("/api/v1/events?from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(page.body.data).toHaveLength(0);
  });
});
