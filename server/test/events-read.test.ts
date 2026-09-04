import { randomUUID } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_events";
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
    console.warn(`[events-read.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[events-read.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
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

interface SeedOverrides {
  event_id?: string;
  event_type?: string;
  start_at?: string;
  end_at?: string | null;
  privacy_level?: string;
  source_kind?: string;
  device_id?: string;
  payload?: Record<string, unknown>;
}

/** Inserts one event in the legacy event store; later tickets replace this store with the envelope protocol. */
async function seedEvent(overrides: SeedOverrides = {}): Promise<string> {
  const eventId = overrides.event_id ?? randomUUID();
  const start = new Date(overrides.start_at ?? "2026-09-01T01:00:00Z");
  const end = overrides.end_at === undefined ? new Date(start.getTime() + 60_000) : overrides.end_at ? new Date(overrides.end_at) : null;
  const now = new Date();
  await EventModel.create({
    id: eventId,
    bucket_id: `bucket:${eventId}`,
    user_id: "test-user",
    device_id: overrides.device_id ?? "cred_device",
    source: overrides.source_kind ?? "windows.foreground",
    type: overrides.event_type ?? "activity.interval",
    schema_version: 1,
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.1.0", observed_at: now.toISOString() },
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    invalidated: false,
    source_kind: overrides.source_kind ?? "windows.foreground",
    source_record_id: "rec-1",
    device_platform: "windows",
    start_at: start,
    end_at: end,
    duration_ms: end ? end.getTime() - start.getTime() : null,
    value: null,
    unit: null,
    data: overrides.payload ?? { application_id: "idea64.exe", is_afk: false, duration: { value: 60_000, unit: "ms" } },
    privacy_level: overrides.privacy_level ?? "normal",
    confidence: 1,
    raw_hash: null,
    created_at: now,
    updated_at: now,
  });
  return eventId;
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

describe("listing events with an Owner session", () => {
  it("returns events in range as contract envelopes with page and query context", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({});

    const response = await request(app).get(readUrl()).set("Cookie", cookie).expect(200);
    expect(response.body.data).toHaveLength(1);
    const event = response.body.data[0];
    expect(event.event_id).toBeTruthy();
    expect(event.event_type).toBe("activity.interval");
    expect(event.schema_version).toBe(1);
    expect(event.owner_id).toBe("test-user");
    expect(event.source).toEqual({ kind: "windows.foreground", record_id: "rec-1" });
    expect(event.device).toEqual({ id: "cred_device", platform: "windows" });
    expect(event.start_at).toBe("2026-09-01T01:00:00.000Z");
    expect(event.end_at).toBe("2026-09-01T01:01:00.000Z");
    expect(event.capture_timezone).toBe("Asia/Shanghai");
    expect(event.capture_offset_minutes).toBe(480);
    expect(event.privacy_level).toBe("normal");
    expect(event.revision).toBe(1);
    expect(event.finalization_state).toBe("final");
    expect(event.provenance).toEqual({ collector_version: "0.1.0", observed_at: expect.any(String) });
    expect(event.invalidated).toBe(false);
    expect(event.payload).toEqual({ application_id: "idea64.exe", is_afk: false, duration: { value: 60_000, unit: "ms" } });
    expect(response.body.page).toEqual({ page_size: 50, next_cursor: null });
    expect(response.body.context).toEqual({
      from: "2026-09-01T00:00:00.000Z",
      to: "2026-09-02T00:00:00.000Z",
      timezone: "Asia/Shanghai",
      provenance: ["windows.foreground"],
      completeness: "complete",
      data_state: "observed",
    });
  });

  it("filters by range boundaries and event type", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await seedEvent({ start_at: "2026-09-01T01:00:00Z" });
    await seedEvent({ start_at: "2026-09-01T03:00:00Z" });
    await seedEvent({ start_at: "2026-09-02T01:00:00Z" }); // after the range
    await seedEvent({ start_at: "2026-08-31T23:59:59Z" }); // before the range
    await seedEvent({ start_at: "2026-09-01T02:00:00Z", event_type: "usage.app_daily" }); // legacy type, not contract-representable

    const inRange = await request(app).get(readUrl()).set("Cookie", cookie).expect(200);
    expect(inRange.body.data).toHaveLength(2);

    const byType = await request(app)
      .get(readUrl({ event_type: "activity.interval" }))
      .set("Cookie", cookie)
      .expect(200);
    expect(byType.body.data).toHaveLength(2);

    const unknownType = await request(app)
      .get(readUrl({ event_type: "usage.app_daily" }))
      .set("Cookie", cookie)
      .expect(200);
    expect(unknownType.body.data).toHaveLength(0);
  });

  it("paginates with page_size and an opaque next_cursor", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    for (let index = 0; index < 3; index++) {
      await seedEvent({ start_at: `2026-09-01T0${index + 1}:00:00Z` });
    }

    const firstPage = await request(app).get(readUrl({ page_size: "2" })).set("Cookie", cookie).expect(200);
    expect(firstPage.body.data).toHaveLength(2);
    expect(firstPage.body.page.page_size).toBe(2);
    expect(firstPage.body.page.next_cursor).toBeTruthy();

    const secondPage = await request(app)
      .get(readUrl({ page_size: "2", cursor: firstPage.body.page.next_cursor }))
      .set("Cookie", cookie)
      .expect(200);
    expect(secondPage.body.data).toHaveLength(1);
    expect(secondPage.body.page.next_cursor).toBeNull();
    const allIds = [...firstPage.body.data, ...secondPage.body.data].map((event: { event_id: string }) => event.event_id);
    expect(new Set(allIds).size).toBe(3);
  });

  it("rejects requests missing required query parameters", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).get("/api/v1/events").set("Cookie", cookie).expect(400);
    await request(app).get("/api/v1/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z").set("Cookie", cookie).expect(400);
    await request(app)
      .get("/api/v1/events?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(400);
  });

  it("rejects listing without an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app).get(readUrl()).expect(401);
  });
});
