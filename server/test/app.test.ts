import request from "supertest";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";

const env: Env = {
  NODE_ENV: "test",
  HOST: "127.0.0.1",
  PORT: 8787,
  MONGODB_URI: "mongodb://unused/live_qs",
  HASH_SECRET: "test-secret-with-at-least-thirty-two-characters",
  DEFAULT_USER_ID: "test-user",
  SESSION_TTL_HOURS: 168,
  COOKIE_SECURE: false,
  CORS_ORIGINS: "http://localhost:5173",
};

describe("application", () => {
  it("serves a public health probe", async () => {
    const response = await request(createApp(env)).get("/health").expect(200);
    expect(response.body.ok).toBe(true);
  });

  it("fails closed on protected APIs when the database is unreachable", async () => {
    const response = await request(createApp(env)).get("/api/v1/context/current").expect(503);
    expect(response.body.error.code).toBe("service_unavailable");
    expect(typeof response.body.request_id).toBe("string");
  });

  it("reflects only explicitly allowed CORS origins with credentials", async () => {
    const app = createApp(env);
    const allowed = await request(app).get("/health").set("Origin", "http://localhost:5173").expect(200);
    expect(allowed.headers["access-control-allow-origin"]).toBe("http://localhost:5173");
    expect(allowed.headers["access-control-allow-credentials"]).toBe("true");

    const unlisted = await request(app).get("/health").set("Origin", "http://untrusted.example").expect(200);
    expect(unlisted.headers["access-control-allow-origin"]).toBeUndefined();
  });
});
