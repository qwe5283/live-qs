import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import type { Clock } from "../src/shared/clock.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, DeviceStatusModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_heartbeats";
const ownerPassword = "correct horse battery staple";

/** Controllable clock so tests drive freshness and expiry deterministically. */
function fakeClock(startIso: string) {
  let currentMs = new Date(startIso).getTime();
  return {
    now: (): Date => new Date(currentMs),
    advanceSeconds: (seconds: number) => {
      currentMs += seconds * 1000;
    },
    iso: (): string => new Date(currentMs).toISOString(),
  };
}

// The fake clock starts at real "now" so stored capture times stay inside the
// TTL backstop horizon while the suite manipulates only simulated time.
const clock = fakeClock(new Date().toISOString());

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
    console.warn(`[heartbeats.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[heartbeats.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    CredentialModel.syncIndexes(),
    DeviceStatusModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
  ]);
  app = createApp(buildEnv(), clock);
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

/** Sets up the Owner password if needed, logs in, and returns the session cookie. */
async function ownerCookie(): Promise<string> {
  const setup = await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword });
  if (setup.status !== 204 && setup.status !== 409) {
    throw new Error(`Owner setup failed with ${setup.status}.`);
  }
  return loginCookie();
}

/** Logs in with the already-initialized Owner password and returns the session cookie. */
async function loginCookie(): Promise<string> {
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

async function createToken(kind: "device_token" | "query_token", name: string): Promise<string> {
  const cookie = await ownerCookie();
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind, name, scopes: [kind === "device_token" ? "events:write" : "events:read"] })
    .expect(201);
  return response.body.token as string;
}

function heartbeatBody(overrides: Record<string, unknown> = {}) {
  return {
    platform: "windows",
    device_name: "Desk PC",
    captured_at: clock.iso(),
    activity: { application_id: "winword.exe", application_label: "Word", is_afk: false },
    ...overrides,
  };
}

describe("heartbeat recording", () => {
  it("accepts a device-token heartbeat and exposes its current state to the Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");

    await request(app)
      .post("/api/v1/heartbeats")
      .set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody())
      .expect(204);

    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    expect(response.body.server_time).toBe(clock.iso());
    const device = response.body.devices.at(-1);
    expect(device.device_id).toMatch(/^cred_/); // server-bound identity, not client-claimed
    expect(device.device_name).toBe("Desk PC");
    expect(device.platform).toBe("windows");
    expect(device.online).toBe(true);
    expect(device.age_seconds).toBe(0);
    expect(device.captured_at).toBe(clock.iso());
    expect(device.activity).toEqual({ application_id: "winword.exe", application_label: "Word", is_afk: false });
  });

  it("rejects heartbeats from Owner sessions and query tokens, and rejects status reads without the context:read scope", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const deviceToken = await createToken("device_token", "Desk collector");
    const queryToken = await createToken("query_token", "Analyst agent");

    await request(app).post("/api/v1/heartbeats").set("Cookie", await ownerCookie())
      .send(heartbeatBody()).expect(401);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${queryToken}`)
      .send(heartbeatBody()).expect(403);
    // Device tokens can never hold context:read; a query token without it is
    // scope-denied. A query token with context:read is allowed (ticket 16).
    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${deviceToken}`).expect(403);
    await request(app).get("/api/v1/status").set("Authorization", `Bearer ${queryToken}`).expect(403);
    await request(app).get("/api/v1/status").expect(401);
  });

  it("binds the device identity to the credential even when display metadata differs", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const firstToken = await createToken("device_token", "Desk collector");
    const secondToken = await createToken("device_token", "Pocket collector");

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${firstToken}`)
      .send(heartbeatBody()).expect(204);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${secondToken}`)
      .send(heartbeatBody({ platform: "android", device_name: "Phone" })).expect(204);

    const cookie = await ownerCookie();
    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    const devices = response.body.devices;
    expect(devices).toHaveLength(2);
    expect(new Set(devices.map((device: { device_id: string }) => device.device_id)).size).toBe(2);
    expect(devices.map((device: { platform: string }) => device.platform).sort()).toEqual(["android", "windows"]);
  });

  it("rejects malformed heartbeats, unknown fields, and path-shaped application identifiers", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const deviceToken = await createToken("device_token", "Desk collector");

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send({ platform: "windows" }).expect(400);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ captured_at: "not-an-instant" })).expect(400);
    // Unknown fields are rejected so raw titles have nowhere to hide.
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ window_title: "机密文档.docx - Word" })).expect(400);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ activity: { application_id: "C:\\Program Files\\winword.exe", is_afk: false } })).expect(400);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ activity: { application_id: "com.example/sneaky", is_afk: false } })).expect(400);

    const cookie = await ownerCookie();
    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    expect(response.body.devices).toHaveLength(0);
  });

  it("rejects capture times too far in the future instead of freezing the projection", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const deviceToken = await createToken("device_token", "Desk collector");

    const oneHourAhead = new Date(clock.now().getTime() + 3_600_000).toISOString();
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ captured_at: oneHourAhead })).expect(400);

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ captured_at: clock.iso() })).expect(204);
    const cookie = await ownerCookie();
    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    expect(response.body.devices).toHaveLength(1);
  });
});

describe("freshness and offline expiry", () => {
  it("keeps the status age at or below thirty seconds while reporting and shows offline within sixty", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");

    const post = () => request(app).post("/api/v1/heartbeats")
      .set("Authorization", `Bearer ${deviceToken}`).send(heartbeatBody()).expect(204);

    const read = () => request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);

    await post();
    clock.advanceSeconds(10);
    await post();
    const fresh = await read();
    expect(fresh.body.devices[0].age_seconds).toBeLessThanOrEqual(30);
    expect(fresh.body.devices[0].online).toBe(true);

    clock.advanceSeconds(45);
    const stale = await read();
    expect(stale.body.devices[0].age_seconds).toBe(45);
    expect(stale.body.devices[0].online).toBe(true); // still within the sixty-second window

    clock.advanceSeconds(15); // sixty seconds after the last heartbeat
    const offline = await read();
    expect(offline.body.devices[0].age_seconds).toBe(60);
    expect(offline.body.devices[0].online).toBe(false);
  });

  it("shows a stopped device as offline but keeps listing it", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody()).expect(204);

    clock.advanceSeconds(3600);
    const response = await readStatus(cookie);
    expect(response.body.devices).toHaveLength(1);
    expect(response.body.devices[0].online).toBe(false);
  });
});

describe("concurrent devices and monotonic updates", () => {
  it("tracks concurrent devices independently", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deskToken = await createToken("device_token", "Desk collector");
    const phoneToken = await createToken("device_token", "Pocket collector");

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deskToken}`)
      .send(heartbeatBody()).expect(204);
    clock.advanceSeconds(5);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${phoneToken}`)
      .send(heartbeatBody({ platform: "android", device_name: "Phone",
        activity: { application_id: "tv.danmaku.bili", application_label: "BiliBili", is_afk: false } })).expect(204);

    clock.advanceSeconds(70); // only the desk device keeps reporting
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deskToken}`)
      .send(heartbeatBody({ activity: { application_id: "devenv.exe", is_afk: false } })).expect(204);

    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    const desk = response.body.devices.find((device: { device_name: string }) => device.device_name === "Desk PC");
    const phone = response.body.devices.find((device: { device_name: string }) => device.device_name === "Phone");
    expect(desk.online).toBe(true);
    expect(desk.activity.application_id).toBe("devenv.exe");
    expect(phone.online).toBe(false); // its own lane expired; the desk lane is unaffected
  });

  it("acknowledges out-of-order heartbeats without regressing the stored state", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");

    clock.advanceSeconds(30);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ activity: { application_id: "devenv.exe", is_afk: false } })).expect(204);

    const staleCapture = new Date(clock.now().getTime() - 20_000).toISOString();
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ captured_at: staleCapture, activity: { application_id: "winword.exe", is_afk: false } }))
      .expect(204);

    // The newer state survives; the delayed heartbeat cannot regress it.
    clock.advanceSeconds(5);
    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    const device = response.body.devices[0];
    expect(device.activity.application_id).toBe("devenv.exe");
    expect(device.captured_at).toBe(new Date(clock.now().getTime() - 5_000).toISOString());
    expect(device.age_seconds).toBe(5);
    expect(device.online).toBe(true);
  });

  it("acknowledges duplicate heartbeats idempotently", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");
    const body = heartbeatBody();

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`).send(body).expect(204);
    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`).send(body).expect(204);

    const response = await request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
    expect(response.body.devices).toHaveLength(1);
    expect(response.body.devices[0].activity.application_id).toBe("winword.exe");
  });
});

describe("isolation from historical facts", () => {
  it("never writes events or usage metrics from heartbeats", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createToken("device_token", "Desk collector");

    await request(app).post("/api/v1/heartbeats").set("Authorization", `Bearer ${deviceToken}`)
      .send(heartbeatBody({ activity: { application_id: "winword.exe", application_label: "Word", is_afk: false } }))
      .expect(204);

    expect(await EventModel.countDocuments()).toBe(0);
    const events = await request(app).get("/api/v1/events")
      .query({ from: "2026-01-01T00:00:00.000Z", to: "2026-01-31T00:00:00.000Z", timezone: "UTC" })
      .set("Cookie", cookie).expect(200);
    expect(events.body.data).toHaveLength(0);

    const metrics = await request(app).get("/api/v1/metrics/usage/day")
      .query({ date: "2026-01-15" }).set("Cookie", cookie).expect(200);
    expect(metrics.body.metrics.device_minutes).toBe(0);
    expect(metrics.body.metrics.active_minutes).toBe(0);
  });
});

async function readStatus(cookie: string) {
  return request(app).get("/api/v1/status").set("Cookie", cookie).expect(200);
}
