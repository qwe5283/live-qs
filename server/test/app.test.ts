import request from "supertest";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";

const env: Env = {
  NODE_ENV: "test",
  PORT: 8787,
  MONGODB_URI: "mongodb://unused/live_qs",
  HASH_SECRET: "test-secret-with-at-least-thirty-two-characters",
  DEFAULT_USER_ID: "test-user",
  USER_TOKEN: "user-token",
  deviceTokens: {},
};

describe("application", () => {
  it("serves a public health probe", async () => {
    const response = await request(createApp(env)).get("/health").expect(200);
    expect(response.body.ok).toBe(true);
  });

  it("protects query APIs", async () => {
    await request(createApp(env)).get("/api/v1/context/current").expect(401);
  });
});
