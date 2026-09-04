import { randomUUID } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { AuditLogModel, CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_classification";
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
    console.warn(`[classification-rules.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[classification-rules.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
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
    AuditLogModel.syncIndexes(),
  ]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

function setCookieFor(response: Response, name: string): string | undefined {
  const values = response.headers["set-cookie"];
  const list = Array.isArray(values) ? values : values ? [values] : [];
  return list.find((value) => value.startsWith(`${name}=`));
}

async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).then((response) => {
    if (response.status !== 204 && response.status !== 409) {
      throw new Error(`Owner setup failed with status ${response.status}.`);
    }
  });
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  return sessionCookieValue(setCookieFor(login, "liveqs_session") ?? "");
}

async function createCredential(cookie: string, kind: "device_token" | "query_token", scopes: string[]): Promise<string> {
  const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
    .send({ kind, name: `cred ${randomUUID()}`, scopes, privacy_ceiling: "sensitive" })
    .expect(201);
  return created.body.token as string;
}

const BILI_RULE_SET = {
  entities: [
    { entity_id: "svc.bilibili", kind: "service", name: "哔哩哔哩" },
    { entity_id: "project.liveqs", kind: "project", name: "LiveQs" },
  ],
  rules: [
    {
      rule_id: "edge.bilibili.title",
      platform: "windows",
      kind: "title_keyword",
      pattern: "bilibili",
      priority: 10,
      subject_entity_id: "svc.bilibili",
    },
    {
      rule_id: "android.bilibili.package",
      kind: "application",
      pattern: "tv.danmaku.bili",
      subject_entity_id: "svc.bilibili",
    },
    {
      rule_id: "rider.projects.title",
      platform: "windows",
      kind: "title_regex",
      pattern: "^[^\\\\]+\\\\([^\\\\]+)\\\\",
      subject_entity_id: "project.liveqs",
      confidence: 0.7,
    },
  ],
};

/** One Windows Edge activity interval whose local title hit the Bilibili keyword rule. */
function edgeBilibiliItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "c4d5e6f7-0001-4000-8000-000000000001",
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "windows.foreground", record_id: "segment-1" },
    device: { id: "desktop", platform: "windows" },
    start_at: "2026-08-21T10:00:00.000Z",
    end_at: "2026-08-21T10:20:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 3,
    finalization_state: "final",
    provenance: { collector_version: "0.3.0", observed_at: "2026-08-21T10:20:05.000Z" },
    invalidated: false,
    payload: {
      application_id: "msedge.exe",
      application_label: "Microsoft Edge",
      subject_id: "svc.bilibili",
      is_afk: false,
      duration: { value: 1_200_000, unit: "ms" },
      classification: { rule_id: "edge.bilibili.title", rule_version: 1, confidence: 0.8 },
    },
    ...overrides,
  };
}

/** One Android UsageStats interval for the Bilibili package, same subject, own device lane. */
function androidBilibiliItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "c4d5e6f7-0002-4000-8000-000000000002",
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.usagestats", record_id: "usage-session-tv.danmaku.bili-1755764400000" },
    device: { id: "phone", platform: "android" },
    start_at: "2026-08-21T10:05:00.000Z",
    end_at: "2026-08-21T10:35:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 1,
    finalization_state: "checkpoint",
    provenance: { collector_version: "0.3.0", observed_at: "2026-08-21T10:36:00.000Z" },
    invalidated: false,
    payload: {
      application_id: "tv.danmaku.bili",
      subject_id: "svc.bilibili",
      is_afk: false,
      duration: { value: 1_800_000, unit: "ms" },
      classification: { rule_id: "android.bilibili.package", rule_version: 1, confidence: 1 },
    },
    ...overrides,
  };
}

describe("classification rule set distribution", () => {
  it("serves an empty version-0 rule set until the Owner publishes one", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "rules:read"]);

    const ownerView = await request(app).get("/api/v1/classification/ruleset").set("Cookie", cookie).expect(200);
    expect(ownerView.body).toEqual({ rule_set_version: 0, updated_at: null, entities: [], rules: [] });

    const deviceView = await request(app).get("/api/v1/classification/ruleset")
      .set("Authorization", `Bearer ${device}`).expect(200);
    expect(deviceView.body.rule_set_version).toBe(0);
  });

  it("publishes the rule set with server-managed per-rule versions and audits the change", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();

    const published = await request(app).put("/api/v1/classification/ruleset")
      .set("Cookie", cookie).send(BILI_RULE_SET).expect(200);
    expect(published.body.rule_set_version).toBe(1);
    const rules = published.body.rules as Array<{ rule_id: string; version: number; confidence: number; platform: string }>;
    // Distributed in execution order: priority first, rule_id as the tiebreak.
    expect(rules.map((rule) => rule.rule_id)).toEqual(["edge.bilibili.title", "android.bilibili.package", "rider.projects.title"]);
    expect(rules.every((rule) => rule.version === 1)).toBe(true);
    // Defaults materialize: platform=any and the kind-based confidence.
    const packageRule = rules.find((rule) => rule.rule_id === "android.bilibili.package");
    expect(packageRule).toMatchObject({ platform: "any", confidence: 1, subject_entity_id: "svc.bilibili", dynamic: false });
    const keywordRule = rules.find((rule) => rule.rule_id === "edge.bilibili.title");
    expect(keywordRule).toMatchObject({ confidence: 0.8, updated_at: expect.any(String) });

    const audit = await AuditLogModel.findOne({ action: "classification_rules.update" }).lean<{ details: Record<string, unknown> }>();
    expect(audit?.details).toMatchObject({ from_version: 0, to_version: 1, rules_added: 3, rules_updated: 0, rules_removed: 0 });

    // The device token with rules:read reads the identical document.
    const device = await createCredential(cookie, "device_token", ["events:write", "rules:read"]);
    const deviceView = await request(app).get("/api/v1/classification/ruleset")
      .set("Authorization", `Bearer ${device}`).expect(200);
    expect(deviceView.body).toEqual(published.body);
  });

  it("keeps unchanged rule versions, bumps changed and new rules, and removes deleted rules", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).put("/api/v1/classification/ruleset").set("Cookie", cookie).send(BILI_RULE_SET).expect(200);

    const secondPublish = await request(app).put("/api/v1/classification/ruleset")
      .set("Cookie", cookie)
      .send({
        entities: BILI_RULE_SET.entities,
        rules: [
          // Unchanged: keeps version 1.
          BILI_RULE_SET.rules[0],
          // Changed pattern: bumps to version 2.
          { ...BILI_RULE_SET.rules[1], pattern: "tv.danmaku.bili", priority: 5 },
          // New rule: starts at version 1.
          { rule_id: "ide.rider.package", kind: "application", pattern: "rider64.exe", subject_entity_id: "project.liveqs" },
        ],
      })
      .expect(200);
    expect(secondPublish.body.rule_set_version).toBe(2);
    const versions = Object.fromEntries(
      (secondPublish.body.rules as Array<{ rule_id: string; version: number }>).map((rule) => [rule.rule_id, rule.version]),
    );
    expect(versions).toEqual({ "edge.bilibili.title": 1, "android.bilibili.package": 2, "ide.rider.package": 1 });
    // The removed discovery rule no longer distributes.
    expect(secondPublish.body.rules).toHaveLength(3);

    const audit = await AuditLogModel.findOne({ action: "classification_rules.update" }).sort({ created_at: -1 })
      .lean<{ details: Record<string, unknown> }>();
    expect(audit?.details).toMatchObject({ from_version: 1, to_version: 2, rules_added: 1, rules_updated: 1, rules_removed: 1 });
  });

  it("rejects inconsistent rule sets with stable codes and never bumps the version", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).put("/api/v1/classification/ruleset").set("Cookie", cookie).send(BILI_RULE_SET).expect(200);

    const cases: Array<{ body: Record<string, unknown>; code: string }> = [
      {
        body: { entities: BILI_RULE_SET.entities, rules: [{ rule_id: "x", kind: "application", pattern: "a.exe", subject_entity_id: "svc.unknown" }] },
        code: "unknown_entity",
      },
      {
        body: { entities: BILI_RULE_SET.entities, rules: [{ rule_id: "x", kind: "application", pattern: "a.exe" }] },
        code: "invalid_rule_target",
      },
      {
        body: {
          entities: BILI_RULE_SET.entities,
          rules: [{ rule_id: "x", kind: "application", pattern: "a.exe", subject_entity_id: "svc.bilibili", dynamic: true }],
        },
        code: "invalid_rule_target",
      },
      {
        body: { entities: BILI_RULE_SET.entities, rules: [{ rule_id: "x", kind: "title_keyword", pattern: "a", dynamic: true }] },
        code: "invalid_dynamic_rule",
      },
      {
        body: {
          entities: BILI_RULE_SET.entities,
          rules: [{ rule_id: "x", kind: "title_regex", pattern: "no-capture-group", dynamic: true }],
        },
        code: "invalid_dynamic_rule",
      },
      {
        body: { entities: BILI_RULE_SET.entities, rules: [{ rule_id: "x", kind: "title_regex", pattern: "([unclosed", subject_entity_id: "svc.bilibili" }] },
        code: "invalid_pattern",
      },
      {
        body: {
          entities: BILI_RULE_SET.entities,
          rules: [
            { rule_id: "dup", kind: "application", pattern: "a", subject_entity_id: "svc.bilibili" },
            { rule_id: "dup", kind: "application", pattern: "b", subject_entity_id: "svc.bilibili" },
          ],
        },
        code: "duplicate_rule",
      },
      {
        body: {
          entities: [
            { entity_id: "svc.dup", kind: "service", name: "x" },
            { entity_id: "svc.dup", kind: "service", name: "y" },
          ],
          rules: [],
        },
        code: "duplicate_entity",
      },
    ];
    for (const testCase of cases) {
      const rejection = await request(app).put("/api/v1/classification/ruleset")
        .set("Cookie", cookie).send(testCase.body).expect(400);
      expect(rejection.body.error.code).toBe(testCase.code);
    }
    // Schema-shape violations (bad slug, oversized name) share the same stable code.
    const badSlug = await request(app).put("/api/v1/classification/ruleset")
      .set("Cookie", cookie)
      .send({ entities: [{ entity_id: "Bad Slug", kind: "service", name: "x" }], rules: [] })
      .expect(400);
    expect(badSlug.body.error.code).toBe("invalid_rule_set");

    const untouched = await request(app).get("/api/v1/classification/ruleset").set("Cookie", cookie).expect(200);
    expect(untouched.body.rule_set_version).toBe(1);
  });

  it("denies rule reads without rules:read and rule writes to any bearer credential", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const deviceWithoutRules = await createCredential(cookie, "device_token", ["events:write"]);
    const queryToken = await createCredential(cookie, "query_token", ["events:read"]);
    const deviceWithRules = await createCredential(cookie, "device_token", ["rules:read"]);

    await request(app).get("/api/v1/classification/ruleset")
      .set("Authorization", `Bearer ${deviceWithoutRules}`).expect(403);
    const queryDenial = await request(app).get("/api/v1/classification/ruleset")
      .set("Authorization", `Bearer ${queryToken}`).expect(403);
    expect(queryDenial.body.error.code).toBe("insufficient_scope");
    await request(app).get("/api/v1/classification/ruleset").expect(401);

    // Management is Owner-session-only: even a rules:read device token cannot write.
    await request(app).put("/api/v1/classification/ruleset")
      .set("Authorization", `Bearer ${deviceWithRules}`).send(BILI_RULE_SET).expect(401);
    await request(app).put("/api/v1/classification/ruleset").send(BILI_RULE_SET).expect(401);
  });
});

describe("cross-platform subject mapping golden samples", () => {
  it("aggregates an Edge session and an Android package under one subject while keeping device and source lanes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windowsDevice = await createCredential(cookie, "device_token", ["events:write", "rules:read"]);
    const androidDevice = await createCredential(cookie, "device_token", ["events:write", "rules:read"]);

    await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${windowsDevice}`)
      .send({ events: [edgeBilibiliItem()] })
      .expect(200);
    await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${androidDevice}`)
      .send({ events: [androidBilibiliItem()] })
      .expect(200);

    const range = "from=2026-08-21T00:00:00Z&to=2026-08-22T00:00:00Z&timezone=Asia/Shanghai";
    const page = await request(app).get(`/api/v1/events?${range}`).set("Cookie", cookie).expect(200);
    const subjects = (page.body.data as Array<Record<string, any>>).map((event) => ({
      subject: event.payload.subject_id,
      rule: event.payload.classification,
      device: event.device.id,
      platform: event.device.platform,
      source: event.source.kind,
    }));
    expect(subjects).toHaveLength(2);
    // One semantic subject, two independent observations with intact provenance.
    expect(new Set(subjects.map((entry) => entry.subject))).toEqual(new Set(["svc.bilibili"]));
    expect(subjects[0]).toEqual({
      subject: "svc.bilibili",
      rule: { rule_id: "edge.bilibili.title", rule_version: 1, confidence: 0.8 },
      device: expect.any(String),
      platform: "windows",
      source: "windows.foreground",
    });
    expect(subjects[1]).toEqual({
      subject: "svc.bilibili",
      rule: { rule_id: "android.bilibili.package", rule_version: 1, confidence: 1 },
      device: expect.any(String),
      platform: "android",
      source: "android.usagestats",
    });
    // Server-bound device lanes stay independent: each observation keeps its own device identity.
    expect(subjects[0]?.device).not.toBe(subjects[1]?.device);
  });

  it("accepts unclassified intervals and device-opaque unapproved project identifiers verbatim", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write"]);

    const noMatch = edgeBilibiliItem({
      event_id: "c4d5e6f7-0003-4000-8000-000000000003",
      source: { kind: "windows.foreground", record_id: "segment-3" },
      // No rule matched: no subject and no classification travel with the event.
      payload: {
        application_id: "msedge.exe",
        is_afk: false,
        duration: { value: 60_000, unit: "ms" },
      },
      revision: 1,
      finalization_state: "checkpoint",
      end_at: "2026-08-21T10:01:00.000Z",
    });
    const unapproved = edgeBilibiliItem({
      event_id: "c4d5e6f7-0004-4000-8000-000000000004",
      source: { kind: "windows.foreground", record_id: "segment-4" },
      // Dynamic discovery before approval: an opaque device-secret HMAC identifier.
      payload: {
        application_id: "rider64.exe",
        subject_id: "unapproved-9f86d081884c7d65",
        is_afk: false,
        duration: { value: 60_000, unit: "ms" },
        classification: { rule_id: "rider.discover.title", rule_version: 2, confidence: 0.9 },
      },
      revision: 1,
      finalization_state: "checkpoint",
      end_at: "2026-08-21T10:01:00.000Z",
    });
    const batch = await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device}`)
      .send({ events: [noMatch, unapproved] })
      .expect(200);
    expect(batch.body.results.map((result: { status: string }) => result.status)).toEqual(["accepted", "accepted"]);

    const range = "from=2026-08-21T00:00:00Z&to=2026-08-22T00:00:00Z&timezone=Asia/Shanghai";
    const page = await request(app).get(`/api/v1/events?${range}`).set("Cookie", cookie).expect(200);
    const byId = new Map((page.body.data as Array<Record<string, any>>).map((event) => [event.event_id, event]));
    expect(byId.get(noMatch.event_id)?.payload).not.toHaveProperty("subject_id");
    expect(byId.get(noMatch.event_id)?.payload).not.toHaveProperty("classification");
    expect(byId.get(unapproved.event_id)?.payload.subject_id).toBe("unapproved-9f86d081884c7d65");
  });

  it("lets Owner corrections re-target the subject while the automatic classification provenance stays intact", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write"]);
    await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${device}`)
      .send({ events: [edgeBilibiliItem()] })
      .expect(200);

    const corrected = await request(app).post("/api/v1/events/c4d5e6f7-0001-4000-8000-000000000001/corrections")
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.subject_id", value: "project.liveqs" }], reason: "这是项目调研，不是娱乐" })
      .expect(200);
    expect(corrected.body.revision).toBeGreaterThanOrEqual(1_000_000_001);
    expect(corrected.body.event.payload.subject_id).toBe("project.liveqs");
    // The automatic interpretation stays explainable: the correction never rewrites it.
    expect(corrected.body.event.payload.classification).toEqual({
      rule_id: "edge.bilibili.title",
      rule_version: 1,
      confidence: 0.8,
    });
    expect(corrected.body.changed_fields).toEqual(["payload.subject_id"]);
  });
});
