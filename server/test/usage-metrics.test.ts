import { randomUUID } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_usage_metrics";
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
    console.warn(`[usage-metrics.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[usage-metrics.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    EventModel.syncIndexes(),
    CredentialModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
  ]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

/** Sets up the Owner password if needed, logs in, and returns the session cookie. */
async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).then((response) => {
    if (response.status !== 204 && response.status !== 409) {
      throw new Error(`Owner setup failed with status ${response.status}.`);
    }
  });
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

async function createBearer(kind: "device_token" | "query_token", overrides: Record<string, unknown> = {}): Promise<string> {
  const cookie = await ownerCookie();
  const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
    .send({ kind, name: `${kind} ${randomUUID()}`, scopes: [kind === "device_token" ? "events:write" : "events:read"], ...overrides })
    .expect(201);
  return created.body.token as string;
}

interface IntervalOverrides {
  device_id?: string;
  device_platform?: string;
  source_kind?: string;
  start_at: string;
  end_at?: string;
  is_afk?: boolean;
  privacy_level?: string;
  invalidated?: boolean;
}

/** Inserts one finalized activity interval row with contract envelope columns. */
async function seedInterval(overrides: IntervalOverrides): Promise<void> {
  const start = new Date(overrides.start_at);
  const end = overrides.end_at ? new Date(overrides.end_at) : null;
  const now = new Date();
  await EventModel.create({
    id: randomUUID(),
    bucket_id: `bucket:${overrides.device_id ?? "device-a"}:activity.interval`,
    user_id: "test-user",
    device_id: overrides.device_id ?? "device-a",
    source: overrides.source_kind ?? (overrides.device_platform === "android" ? "android.accessibility" : "windows.foreground"),
    type: "activity.interval",
    schema_version: 1,
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.1.0", observed_at: now.toISOString() },
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    invalidated: overrides.invalidated ?? false,
    source_kind: overrides.source_kind ?? (overrides.device_platform === "android" ? "android.accessibility" : "windows.foreground"),
    source_record_id: "rec-1",
    device_platform: overrides.device_platform ?? "windows",
    start_at: start,
    end_at: end,
    duration_ms: end ? end.getTime() - start.getTime() : null,
    value: null,
    unit: null,
    data: { application_id: "idea64.exe", is_afk: overrides.is_afk ?? false, duration: { value: end ? end.getTime() - start.getTime() : 0, unit: "ms" } },
    privacy_level: overrides.privacy_level ?? "normal",
    confidence: 1,
    raw_hash: null,
    created_at: now,
    updated_at: now,
  });
}

function dayUrl(date: string, overrides: Record<string, string> = {}): string {
  const params = new URLSearchParams({ date, ...overrides });
  return `/api/v1/metrics/usage/day?${params.toString()}`;
}

function weekUrl(date: string, overrides: Record<string, string> = {}): string {
  const params = new URLSearchParams({ date, ...overrides });
  return `/api/v1/metrics/usage/week?${params.toString()}`;
}

describe("usage day metrics", () => {
  it("returns zeroed metrics for an empty day", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    const response = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(response.body).toEqual({
      date: "2026-09-01",
      timezone: "Asia/Shanghai",
      metrics: { device_minutes: 0, active_minutes: 0 },
      devices: [],
      context: {
        from: "2026-08-31T16:00:00.000Z",
        to: "2026-09-01T16:00:00.000Z",
        timezone: "Asia/Shanghai",
        provenance: [],
        completeness: "complete",
      },
    });
  });

  it("sums device minutes per device and unions active minutes across devices", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    // Two concurrent devices: 10:00-11:00 and 10:30-11:30 local (Asia/Shanghai).
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T02:00:00Z", end_at: "2026-09-01T03:00:00Z" });
    await seedInterval({ device_id: "device-b", device_platform: "android", start_at: "2026-09-01T02:30:00Z", end_at: "2026-09-01T03:30:00Z" });

    const response = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    // Device time sums both lanes (120 > 60 elapsed minutes); active time unions overlap once.
    expect(response.body.metrics).toEqual({ device_minutes: 120, active_minutes: 90 });
    expect(response.body.devices).toEqual([
      { device_id: "device-a", platform: "windows", device_minutes: 60, active_minutes: 60 },
      { device_id: "device-b", platform: "android", device_minutes: 60, active_minutes: 60 },
    ]);
    expect(response.body.context.provenance).toEqual(["android.accessibility", "windows.foreground"]);
  });

  it("counts AFK foreground time in device minutes but not in active minutes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T00:00:00Z", end_at: "2026-09-01T01:00:00Z", is_afk: true });
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T02:00:00Z", is_afk: false });

    const response = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(response.body.metrics).toEqual({ device_minutes: 120, active_minutes: 60 });
    expect(response.body.devices).toEqual([
      { device_id: "device-a", platform: "windows", device_minutes: 120, active_minutes: 60 },
    ]);
  });

  it("merges adjacent intervals of one device without double counting", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T01:30:00Z" });
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T01:30:00Z", end_at: "2026-09-01T02:00:00Z" });

    const response = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(response.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
  });

  it("splits an interval crossing local midnight across the two day reports", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    // Local 23:30-00:30 in Asia/Shanghai: half before midnight, half after.
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T15:30:00Z", end_at: "2026-09-01T16:30:00Z" });

    const firstDay = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(firstDay.body.metrics).toEqual({ device_minutes: 30, active_minutes: 30 });
    expect(firstDay.body.context.from).toBe("2026-08-31T16:00:00.000Z");

    const secondDay = await request(app).get(dayUrl("2026-09-02", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(secondDay.body.metrics).toEqual({ device_minutes: 30, active_minutes: 30 });
    expect(secondDay.body.context.from).toBe("2026-09-01T16:00:00.000Z");
  });

  it("resolves boundaries in the Owner report timezone and accepts a request override", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).post("/api/v1/owner/settings").set("Cookie", cookie)
      .send({ report_timezone: "Asia/Shanghai" }).expect(200);
    // Local 2026-09-01 01:00-02:00 in Asia/Shanghai, but still UTC 2026-08-31.
    await seedInterval({ device_id: "device-a", start_at: "2026-08-31T17:00:00Z", end_at: "2026-08-31T18:00:00Z" });

    const ownerZone = await request(app).get(dayUrl("2026-09-01")).set("Cookie", cookie).expect(200);
    expect(ownerZone.body.timezone).toBe("Asia/Shanghai");
    expect(ownerZone.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });

    const otherDay = await request(app).get(dayUrl("2026-08-31")).set("Cookie", cookie).expect(200);
    expect(otherDay.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });

    const override = await request(app).get(dayUrl("2026-08-31", { timezone: "UTC" })).set("Cookie", cookie).expect(200);
    expect(override.body.timezone).toBe("UTC");
    expect(override.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
  });

  it("buckets a 23-hour DST spring-forward day and a 25-hour fall-back day", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ device_id: "device-a", start_at: "2026-03-08T06:00:00Z", end_at: "2026-03-08T07:00:00Z" });

    const springDay = await request(app).get(dayUrl("2026-03-08", { timezone: "America/New_York" })).set("Cookie", cookie).expect(200);
    expect(springDay.body.context).toEqual({
      from: "2026-03-08T05:00:00.000Z",
      to: "2026-03-09T04:00:00.000Z",
      timezone: "America/New_York",
      provenance: ["windows.foreground"],
      completeness: "complete",
    });
    expect(springDay.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });

    const nextDay = await request(app).get(dayUrl("2026-03-09", { timezone: "America/New_York" })).set("Cookie", cookie).expect(200);
    expect(nextDay.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });

    await seedInterval({ device_id: "device-a", start_at: "2026-11-01T05:30:00Z", end_at: "2026-11-01T06:30:00Z" });
    const fallDay = await request(app).get(dayUrl("2026-11-01", { timezone: "America/New_York" })).set("Cookie", cookie).expect(200);
    expect(fallDay.body.context.from).toBe("2026-11-01T04:00:00.000Z");
    expect(fallDay.body.context.to).toBe("2026-11-02T05:00:00.000Z");
    expect(fallDay.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
  });

  it("excludes invalidated events from both metrics", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T02:00:00Z", invalidated: true });

    const response = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(response.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });
  });

  it("rejects invalid dates and unknown timezones", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).get(dayUrl("2026-13-01", { timezone: "UTC" })).set("Cookie", cookie).expect(400);
    await request(app).get(dayUrl("2026-09-01", { timezone: "Mars/Olympus" })).set("Cookie", cookie).expect(400);
    const rejected = await request(app).get(dayUrl("2026-09-01", { timezone: "Mars/Olympus" })).set("Cookie", cookie);
    expect(rejected.body.error.code).toBe("invalid_timezone");
  });

  it("requires authentication and denies device tokens", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app).get(dayUrl("2026-09-01", { timezone: "UTC" })).expect(401);

    const deviceToken = await createBearer("device_token");
    await request(app).get(dayUrl("2026-09-01", { timezone: "UTC" })).set("Authorization", `Bearer ${deviceToken}`).expect(403);
  });

  it("serves query tokens within their privacy ceiling and event types", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const scopedToken = await createBearer("query_token", { allowed_event_types: ["activity.interval"], privacy_ceiling: "normal" });
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T01:00:00Z", end_at: "2026-09-01T02:00:00Z" });
    await seedInterval({ device_id: "device-a", start_at: "2026-09-01T03:00:00Z", end_at: "2026-09-01T04:00:00Z", privacy_level: "sensitive" });

    const scoped = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" }))
      .set("Authorization", `Bearer ${scopedToken}`).expect(200);
    expect(scoped.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
    // The sensitive interval was withheld by the privacy ceiling, so the report is partial.
    expect(scoped.body.context.completeness).toBe("partial");

    const unrestrictedToken = await createBearer("query_token", { allowed_event_types: ["activity.interval"], privacy_ceiling: "sensitive" });
    const unrestricted = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" }))
      .set("Authorization", `Bearer ${unrestrictedToken}`).expect(200);
    expect(unrestricted.body.metrics).toEqual({ device_minutes: 120, active_minutes: 120 });
    expect(unrestricted.body.context.completeness).toBe("complete");

    const otherTypesToken = await createBearer("query_token", { allowed_event_types: ["payment.transaction"], privacy_ceiling: "sensitive" });
    const excluded = await request(app).get(dayUrl("2026-09-01", { timezone: "Asia/Shanghai" }))
      .set("Authorization", `Bearer ${otherTypesToken}`).expect(200);
    expect(excluded.body.metrics).toEqual({ device_minutes: 0, active_minutes: 0 });
    expect(excluded.body.context.completeness).toBe("partial");
  });
});

describe("usage week metrics", () => {
  it("resolves a Monday-start week with per-day metrics", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    // Monday 2026-07-27 10:00-11:00 local; the Sunday before the week must not count.
    await seedInterval({ device_id: "device-a", start_at: "2026-07-27T02:00:00Z", end_at: "2026-07-27T03:00:00Z" });
    await seedInterval({ device_id: "device-a", start_at: "2026-07-26T10:00:00Z", end_at: "2026-07-26T11:00:00Z" });

    const response = await request(app).get(weekUrl("2026-07-28", { timezone: "Asia/Shanghai" })).set("Cookie", cookie).expect(200);
    expect(response.body.week_start_date).toBe("2026-07-27");
    expect(response.body.week_end_date).toBe("2026-08-02");
    expect(response.body.timezone).toBe("Asia/Shanghai");
    expect(response.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
    expect(response.body.days).toHaveLength(7);
    expect(response.body.days[0]).toEqual({ date: "2026-07-27", device_minutes: 60, active_minutes: 60 });
    expect(response.body.days.slice(1).every((day: { device_minutes: number }) => day.device_minutes === 0)).toBe(true);
  });

  it("buckets a week spanning the DST spring-forward transition", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedInterval({ device_id: "device-a", start_at: "2026-03-08T06:00:00Z", end_at: "2026-03-08T07:00:00Z" });

    const response = await request(app).get(weekUrl("2026-03-08", { timezone: "America/New_York" })).set("Cookie", cookie).expect(200);
    expect(response.body.week_start_date).toBe("2026-03-02");
    expect(response.body.week_end_date).toBe("2026-03-08");
    expect(response.body.context.from).toBe("2026-03-02T05:00:00.000Z");
    expect(response.body.context.to).toBe("2026-03-09T04:00:00.000Z");
    expect(response.body.metrics).toEqual({ device_minutes: 60, active_minutes: 60 });
    expect(response.body.days.at(-1)).toEqual({ date: "2026-03-08", device_minutes: 60, active_minutes: 60 });
  });
});
