import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { OwnerCredentialModel, OwnerSessionModel, OwnerSettingsModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_owner_settings";
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
    console.warn(`[owner-settings.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[owner-settings.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
    OwnerSettingsModel.syncIndexes(),
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

describe("Owner report settings", () => {
  it("defaults the report timezone to UTC until the Owner configures one", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    const response = await request(app).get("/api/v1/owner/settings").set("Cookie", cookie).expect(200);
    expect(response.body).toEqual({ report_timezone: "UTC" });
  });

  it("updates and persists the report timezone", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    const update = await request(app)
      .post("/api/v1/owner/settings")
      .set("Cookie", cookie)
      .send({ report_timezone: "Asia/Shanghai" })
      .expect(200);
    expect(update.body).toEqual({ report_timezone: "Asia/Shanghai" });

    const reloaded = await request(app).get("/api/v1/owner/settings").set("Cookie", cookie).expect(200);
    expect(reloaded.body).toEqual({ report_timezone: "Asia/Shanghai" });
  });

  it("rejects an unknown IANA timezone and keeps the previous setting", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).post("/api/v1/owner/settings").set("Cookie", cookie)
      .send({ report_timezone: "Asia/Shanghai" }).expect(200);

    const rejected = await request(app)
      .post("/api/v1/owner/settings")
      .set("Cookie", cookie)
      .send({ report_timezone: "Mars/Olympus" })
      .expect(400);
    expect(rejected.body.error.code).toBe("invalid_timezone");

    const unchanged = await request(app).get("/api/v1/owner/settings").set("Cookie", cookie).expect(200);
    expect(unchanged.body).toEqual({ report_timezone: "Asia/Shanghai" });
  });

  it("rejects malformed update bodies", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).post("/api/v1/owner/settings").set("Cookie", cookie).send({}).expect(400);
    await request(app).post("/api/v1/owner/settings").set("Cookie", cookie)
      .send({ report_timezone: "" }).expect(400);
    await request(app).post("/api/v1/owner/settings").set("Cookie", cookie)
      .send({ report_timezone: "UTC", extra: true }).expect(400);
  });

  it("requires an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    await request(app).get("/api/v1/owner/settings").expect(401);
    await request(app).post("/api/v1/owner/settings").send({ report_timezone: "UTC" }).expect(401);
  });
});
