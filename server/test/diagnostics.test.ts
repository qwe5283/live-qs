import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import type { Clock } from "../src/shared/clock.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel, SyncDiagnosticModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_diagnostics";
const ownerPassword = "correct horse battery staple";

/** Controllable clock so snapshot ages are driven deterministically. */
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

// The fake clock starts at real "now" so stored instants stay inside any TTL
// horizon while the suite manipulates only simulated time.
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
    console.warn(`[diagnostics.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[diagnostics.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    CredentialModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
    SyncDiagnosticModel.syncIndexes(),
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
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

async function createDeviceToken(name: string): Promise<string> {
  const cookie = await ownerCookie();
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind: "device_token", name, scopes: ["events:write"] })
    .expect(201);
  return response.body.token as string;
}

function diagnosticsBody(overrides: Record<string, unknown> = {}) {
  return {
    platform: "windows",
    device_name: "Desk PC",
    collected_at: clock.iso(),
    last_successful_upload_at: clock.iso(),
    oldest_pending_at: clock.iso(),
    pending_count: 3,
    permanent_failure_count: 1,
    recent_errors: [
      { code: "invalid_event", message: "payload.duration must match the interval bounds.", occurred_at: clock.iso() },
      { code: "network_error", message: "无法连接同步服务。", occurred_at: clock.iso() },
    ],
    ...overrides,
  };
}

describe("sync diagnostics reporting", () => {
  it("stores a device snapshot and exposes it to the Owner session with ages", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createDeviceToken("Desk collector");

    const pushedAt = clock.iso();
    await request(app)
      .post("/api/v1/diagnostics/sync")
      .set("Authorization", `Bearer ${deviceToken}`)
      .send(diagnosticsBody())
      .expect(204);

    clock.advanceSeconds(30);
    const response = await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
    expect(response.body.server_time).toBe(clock.iso());
    const device = response.body.devices.at(-1);
    expect(device.device_id).toMatch(/^cred_/); // server-bound identity, not client-claimed
    expect(device.device_name).toBe("Desk PC");
    expect(device.platform).toBe("windows");
    expect(device.reported_at).toBe(pushedAt);
    expect(device.age_seconds).toBe(30);
    expect(device.collected_at).toBe(pushedAt);
    expect(device.last_successful_upload_at).toBe(pushedAt);
    expect(device.oldest_pending_at).toBe(pushedAt);
    expect(device.pending_count).toBe(3);
    expect(device.permanent_failure_count).toBe(1);
    expect(device.recent_errors).toEqual([
      { code: "invalid_event", message: "payload.duration must match the interval bounds.", occurred_at: pushedAt },
      { code: "network_error", message: "无法连接同步服务。", occurred_at: pushedAt },
    ]);
  });

  it("replaces a device snapshot on the next push and keeps devices in independent lanes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const firstToken = await createDeviceToken("Desk collector");
    const secondToken = await createDeviceToken("Pocket collector");

    await request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${firstToken}`)
      .send(diagnosticsBody({ pending_count: 10 })).expect(204);
    clock.advanceSeconds(5);
    await request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${secondToken}`)
      .send(diagnosticsBody({ platform: "android", device_name: "Phone", pending_count: 0, permanent_failure_count: 0, recent_errors: [] })).expect(204);
    clock.advanceSeconds(5);
    // The first device drains its queue; the new snapshot must replace, not append.
    await request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${firstToken}`)
      .send(diagnosticsBody({ pending_count: 0, permanent_failure_count: 0, recent_errors: [] })).expect(204);

    const cookie = await ownerCookie();
    const response = await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
    const devices = response.body.devices;
    expect(devices).toHaveLength(2);
    expect(new Set(devices.map((device: { device_id: string }) => device.device_id)).size).toBe(2);
    const desk = devices.find((device: { device_name: string | null }) => device.device_name === "Desk PC");
    expect(desk.pending_count).toBe(0);
    expect(desk.recent_errors).toEqual([]);
    expect(desk.age_seconds).toBe(0);
    const phone = devices.find((device: { device_name: string | null }) => device.device_name === "Phone");
    expect(phone.platform).toBe("android");
    expect(phone.age_seconds).toBe(5);
    // Oldest report first: the phone reported before the desk's replacement.
    expect(devices[0].device_name).toBe("Phone");
  });

  it("rejects pushes without a device token and reads without an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    await request(app).post("/api/v1/diagnostics/sync").set("Cookie", cookie)
      .send(diagnosticsBody()).expect(401);
    await request(app).post("/api/v1/diagnostics/sync").send(diagnosticsBody()).expect(401);
    await request(app).get("/api/v1/diagnostics/sync").expect(401);
    await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
  });

  it("rejects snapshots carrying unknown fields, unsafe codes, or out-of-bounds values", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const deviceToken = await createDeviceToken("Desk collector");
    const post = (body: Record<string, unknown>) =>
      request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${deviceToken}`).send(body);

    // Unknown fields are rejected so raw titles and notification text have nowhere to hide.
    await post(diagnosticsBody({ window_title: "机密文档.docx - Word" })).expect(400);
    await post(diagnosticsBody({ recent_errors: [{ code: "invalid_event", message: "x", occurred_at: clock.iso(), title: "机密文档" }] })).expect(400);
    // Stable-code pattern: free-form error strings are not stable codes.
    await post(diagnosticsBody({ recent_errors: [{ code: "Connection refused!", message: "x", occurred_at: clock.iso() }] })).expect(400);
    await post(diagnosticsBody({ recent_errors: [{ code: "invalid_event", message: "x".repeat(301), occurred_at: clock.iso() }] })).expect(400);
    // The contract caps the recent-error window at ten entries.
    await post(diagnosticsBody({
      recent_errors: Array.from({ length: 11 }, (_, index) => ({ code: "network_error", message: `e${index}`, occurred_at: clock.iso() })),
    })).expect(400);
    // Counts and instants stay in bounds.
    await post(diagnosticsBody({ pending_count: -1 })).expect(400);
    await post(diagnosticsBody({ collected_at: "not-an-instant" })).expect(400);
    await post(diagnosticsBody({ collected_at: new Date(clock.now().getTime() + 3_600_000).toISOString() })).expect(400);
    await post(diagnosticsBody({ last_successful_upload_at: new Date(clock.now().getTime() + 3_600_000).toISOString() })).expect(400);

    const cookie = await ownerCookie();
    const response = await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
    expect(response.body.devices).toHaveLength(0);
  });

  it("accepts a fresh collector that has not collected anything yet", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceToken = await createDeviceToken("Fresh install");

    await request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${deviceToken}`)
      .send({
        platform: "android",
        pending_count: 0,
        permanent_failure_count: 0,
        recent_errors: [],
      }).expect(204);

    const response = await request(app).get("/api/v1/diagnostics/sync").set("Cookie", cookie).expect(200);
    const device = response.body.devices[0];
    expect(device.collected_at).toBeNull();
    expect(device.last_successful_upload_at).toBeNull();
    expect(device.oldest_pending_at).toBeNull();
    expect(device.pending_count).toBe(0);
    expect(device.recent_errors).toEqual([]);
  });

  it("never creates historical events or touches usage data from diagnostics", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const deviceToken = await createDeviceToken("Desk collector");

    await request(app).post("/api/v1/diagnostics/sync").set("Authorization", `Bearer ${deviceToken}`)
      .send(diagnosticsBody()).expect(204);

    expect(await EventModel.countDocuments()).toBe(0);
  });
});
