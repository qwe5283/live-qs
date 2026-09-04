import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test";
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
    console.warn(`[owner-auth.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[owner-auth.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([OwnerCredentialModel.syncIndexes(), OwnerSessionModel.syncIndexes()]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

describe("Owner first-run setup against real MongoDB", () => {
  it("reports an uninitialized instance", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const response = await request(app).get("/api/v1/owner/status").expect(200);
    expect(response.body).toEqual({ initialized: false });
  });

  it("creates the Owner password through controlled first-run setup", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const response = await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
    const setCookie = setCookieFor(response, "liveqs_session");
    expect(setCookie).toBeDefined();
    expect(setCookie).toContain("HttpOnly");
    expect(setCookie).toContain("SameSite=Lax");

    const status = await request(app).get("/api/v1/owner/status").expect(200);
    expect(status.body).toEqual({ initialized: true });

    const credential = await OwnerCredentialModel.findOne({ user_id: "test-user" }).lean();
    expect(credential?.kdf).toBe("scrypt");
    expect(credential?.kdf_params.N).toBeGreaterThanOrEqual(16384);
    expect(JSON.stringify(credential)).not.toContain(ownerPassword);
  });

  it("rejects setup once the Owner password exists", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
    const response = await request(app).post("/api/v1/owner/setup").send({ password: "another password" }).expect(409);
    expect(response.body.error.code).toBe("already_initialized");
    expect(typeof response.body.request_id).toBe("string");
  });

  it("rejects passwords shorter than the contract minimum", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const response = await request(app).post("/api/v1/owner/setup").send({ password: "short" }).expect(400);
    expect(response.body.error.code).toBe("invalid_password");
  });
});

describe("Owner login and protected access against real MongoDB", () => {
  async function initializeOwner(): Promise<void> {
    await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
  }

  it("logs in with the correct password and probes the session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    const response = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
    const setCookie = setCookieFor(response, "liveqs_session");
    expect(setCookie).toBeDefined();
    expect(setCookie).toContain("HttpOnly");
    expect(setCookie).toContain("SameSite=Lax");
    expect(setCookie).not.toContain("Secure");

    const probe = await request(app)
      .get("/api/v1/owner/session")
      .set("Cookie", sessionCookieValue(setCookie ?? ""))
      .expect(200);
    expect(probe.body).toEqual({ authenticated: true });
  });

  it("rejects a wrong password without establishing a session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    const response = await request(app)
      .post("/api/v1/owner/login")
      .send({ password: "totally wrong password" })
      .expect(401);
    expect(response.body.error.code).toBe("invalid_credentials");
    expect(typeof response.body.request_id).toBe("string");
    expect(setCookieFor(response, "liveqs_session")).toBeUndefined();
  });

  it("rejects unauthenticated protected access with the contract error shape", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    const response = await request(app).get("/api/v1/context/current").expect(401);
    expect(response.body.error.code).toBe("unauthorized");
    expect(typeof response.body.error.message).toBe("string");
    expect(typeof response.body.request_id).toBe("string");
  });

  it("accepts protected access with a valid session cookie", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
    const cookie = sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
    await request(app).get("/api/v1/context/current").set("Cookie", cookie).expect(200);
  });

  it("rejects logout without a session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    await request(app).post("/api/v1/owner/logout").expect(401);
  });

  it("revokes the session after logout so it can no longer access protected endpoints", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await initializeOwner();
    const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
    const cookie = sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");

    const logout = await request(app).post("/api/v1/owner/logout").set("Cookie", cookie).expect(204);
    const cleared = setCookieFor(logout, "liveqs_session");
    expect(cleared).toBeDefined();
    expect(cleared).toContain("Expires=Thu, 01 Jan 1970 00:00:00 GMT");

    await request(app).get("/api/v1/owner/session").set("Cookie", cookie).expect(401);
    await request(app).get("/api/v1/context/current").set("Cookie", cookie).expect(401);
  });

  it("marks the session cookie Secure when configured for HTTPS", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const secureApp = createApp(buildEnv({ COOKIE_SECURE: true }));
    await request(secureApp).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
    const login = await request(secureApp).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
    expect(setCookieFor(login, "liveqs_session")).toContain("Secure");
  });
});
