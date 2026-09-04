import { randomUUID } from "node:crypto";
import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import type { Response } from "supertest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import {
  AuditLogModel,
  CredentialModel,
  EventModel,
  EventRevisionModel,
  OwnerCredentialModel,
  OwnerSessionModel,
  ReclassificationTaskModel,
} from "../src/db/models.js";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_reclassification";
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
    console.warn(`[reclassification.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[reclassification.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
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
    ReclassificationTaskModel.syncIndexes(),
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

interface TestDevice {
  token: string;
  credentialId: string;
}

async function createDevice(cookie: string, scopes: string[] = ["events:write", "rules:read"]): Promise<TestDevice> {
  const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
    .send({ kind: "device_token", name: `device ${randomUUID()}`, scopes, privacy_ceiling: "sensitive" })
    .expect(201);
  return { token: created.body.token as string, credentialId: created.body.credential.credential_id as string };
}

async function createQueryToken(cookie: string): Promise<string> {
  const created = await request(app).post("/api/v1/credentials").set("Cookie", cookie)
    .send({ kind: "query_token", name: `query ${randomUUID()}`, scopes: ["events:read"] })
    .expect(201);
  return created.body.token as string;
}

const RULE_SET_V1 = {
  entities: [{ entity_id: "svc.bilibili", kind: "service", name: "哔哩哔哩" }],
  rules: [
    {
      rule_id: "edge.bilibili.title",
      platform: "windows",
      kind: "title_keyword",
      pattern: "bilibili",
      priority: 10,
      subject_entity_id: "svc.bilibili",
    },
  ],
};

/** The Owner improved the rule: the same title keyword now maps to a project subject. */
const RULE_SET_V2 = {
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
      subject_entity_id: "project.liveqs",
    },
  ],
};

let eventSeq = 0;

/** One finalized Windows activity interval uploaded through the batch contract. */
function activityItem(device: TestDevice, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  eventSeq += 1;
  const id = `c4d5e6f7-0000-4000-8000-${String(eventSeq).padStart(12, "0")}`;
  return {
    event_id: id,
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "windows.foreground", record_id: `segment-${eventSeq}` },
    device: { id: device.credentialId, platform: "windows" },
    start_at: "2026-08-21T10:00:00.000Z",
    end_at: "2026-08-21T10:20:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 1,
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

async function upload(device: TestDevice, items: Record<string, unknown>[]): Promise<void> {
  await request(app).post("/api/v1/events/batch")
    .set("Authorization", `Bearer ${device.token}`)
    .send({ events: items })
    .expect(200);
}

async function publishRuleSet(cookie: string, ruleSet: object): Promise<number> {
  const published = await request(app).put("/api/v1/classification/ruleset")
    .set("Cookie", cookie).send(ruleSet).expect(200);
  return published.body.rule_set_version as number;
}

describe("reclassification estimate", () => {
  it("counts exactly the finalized non-AFK device-space activity and reports per-device ranges", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    const android = await createDevice(cookie);

    await upload(windows, [
      activityItem(windows, {
        start_at: "2026-08-20T02:00:00.000Z",
        end_at: "2026-08-20T02:10:00.000Z",
        payload: {
          application_id: "msedge.exe",
          application_label: "Microsoft Edge",
          subject_id: "svc.bilibili",
          is_afk: false,
          duration: { value: 600_000, unit: "ms" },
          classification: { rule_id: "edge.bilibili.title", rule_version: 1, confidence: 0.8 },
        },
      }),
      // Excluded from scope: an AFK interval has no classifiable activity.
      activityItem(windows, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000a1",
        start_at: "2026-08-21T03:00:00.000Z",
        end_at: "2026-08-21T03:10:00.000Z",
        payload: { application_id: "idle.exe", is_afk: true, duration: { value: 600_000, unit: "ms" } },
      }),
      // Excluded from scope: an open checkpoint is still owned by the live checkpoint stream.
      activityItem(windows, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000a2",
        start_at: "2026-08-21T04:00:00.000Z",
        end_at: "2026-08-21T04:10:00.000Z",
        revision: 4,
        finalization_state: "checkpoint",
        payload: {
          application_id: "msedge.exe",
          subject_id: "svc.bilibili",
          is_afk: false,
          duration: { value: 600_000, unit: "ms" },
          classification: { rule_id: "edge.bilibili.title", rule_version: 1, confidence: 0.8 },
        },
      }),
      // Excluded from scope: a manually corrected event sits in the reserved revision space.
      activityItem(windows, { event_id: "c4d5e6f7-0000-4000-8000-0000000000a3" }),
      // Excluded from scope: an invalidated false positive left the default views.
      activityItem(windows, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000a4",
        invalidated: true,
      }),
    ]);
    await request(app).post(`/api/v1/events/c4d5e6f7-0000-4000-8000-0000000000a3/corrections`)
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.subject_id", value: "project.liveqs" }], reason: "人工裁定" })
      .expect(200);
    await upload(android, [
      activityItem(android, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000b1",
        device: { id: android.credentialId, platform: "android" },
        source: { kind: "android.usagestats", record_id: "usage-1" },
        start_at: "2026-08-21T05:00:00.000Z",
        end_at: "2026-08-21T05:30:00.000Z",
        payload: {
          application_id: "tv.danmaku.bili",
          subject_id: "svc.bilibili",
          is_afk: false,
          duration: { value: 1_800_000, unit: "ms" },
          classification: { rule_id: "edge.bilibili.title", rule_version: 1, confidence: 1 },
        },
      }),
    ]);

    const estimate = await request(app).get("/api/v1/classification/reclassification/estimate")
      .set("Cookie", cookie).expect(200);
    expect(estimate.body.total_events).toBe(2);
    const devices = estimate.body.devices as Array<Record<string, unknown>>;
    expect(devices).toHaveLength(2);
    const byDevice = new Map(devices.map((device) => [device.device_id, device]));
    expect(byDevice.get(windows.credentialId)).toMatchObject({
      platform: "windows",
      event_count: 1,
      earliest_start_at: "2026-08-20T02:00:00.000Z",
      latest_start_at: "2026-08-20T02:00:00.000Z",
    });
    expect(byDevice.get(android.credentialId)).toMatchObject({
      platform: "android",
      event_count: 1,
      earliest_start_at: "2026-08-21T05:00:00.000Z",
      latest_start_at: "2026-08-21T05:00:00.000Z",
    });
  });

  it("honors the requested time bounds and rejects an inverted range", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await upload(windows, [
      activityItem(windows),
      activityItem(windows, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000c1",
        start_at: "2026-09-01T09:00:00.000Z",
        end_at: "2026-09-01T09:20:00.000Z",
      }),
    ]);

    const bounded = await request(app)
      .get("/api/v1/classification/reclassification/estimate?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z")
      .set("Cookie", cookie).expect(200);
    expect(bounded.body.total_events).toBe(1);
    expect((bounded.body.devices as Array<Record<string, unknown>>)[0]).toMatchObject({ device_id: windows.credentialId });

    const inverted = await request(app)
      .get("/api/v1/classification/reclassification/estimate?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z")
      .set("Cookie", cookie).expect(400);
    expect(inverted.body.error.code).toBe("invalid_time_range");
  });

  it("is inaccessible without an Owner session", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createDevice(cookie);
    const query = await createQueryToken(cookie);

    await request(app).get("/api/v1/classification/reclassification/estimate").expect(401);
    await request(app).get("/api/v1/classification/reclassification/estimate")
      .set("Authorization", `Bearer ${device}`).expect(401);
    await request(app).get("/api/v1/classification/reclassification/estimate")
      .set("Authorization", `Bearer ${query}`).expect(401);
  });
});

describe("saving rules never changes existing cloud classifications", () => {
  it("leaves every stored event and archive untouched across a rule set publication", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    await upload(windows, [activityItem(windows)]);

    const before = await EventModel.find({}).lean();
    await publishRuleSet(cookie, RULE_SET_V2);

    const after = await EventModel.find({}).lean();
    expect(after).toHaveLength(before.length);
    expect(after.map((row) => JSON.stringify(row))).toEqual(before.map((row) => JSON.stringify(row)));
    expect(await EventRevisionModel.countDocuments()).toBe(0);
  });
});

describe("reclassification task lifecycle", () => {
  it("creates a task with the published target version, a frozen estimate, and an audit record", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    await upload(windows, [activityItem(windows)]);

    const created = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie)
      .send({ from: "2026-08-01T00:00:00Z", to: "2026-09-01T00:00:00Z" })
      .expect(200);
    expect(created.body.status).toBe("open");
    expect(created.body.target_rule_set_version).toBe(1);
    expect(created.body.from).toBe("2026-08-01T00:00:00.000Z");
    expect(created.body.to).toBe("2026-09-01T00:00:00.000Z");
    expect(created.body.estimate.total_events).toBe(1);
    expect(created.body.estimate.devices).toHaveLength(1);
    expect(created.body.progress).toEqual({
      devices_reported: 0, scanned: 0, reclassified: 0, unchanged: 0, failed: 0, unrecoverable: 0,
    });
    expect(created.body.device_reports).toEqual([]);

    const audit = await AuditLogModel.findOne({ action: "reclassification.task_started" })
      .lean<{ actor_type: string; actor_id: string | null; details: Record<string, unknown> }>();
    expect(audit?.actor_type).toBe("user");
    expect(audit?.actor_id).toBeTruthy();
    expect(audit?.details).toMatchObject({
      target_rule_set_version: 1,
      from: "2026-08-01T00:00:00.000Z",
      to: "2026-09-01T00:00:00.000Z",
      estimated_events: 1,
      estimated_devices: 1,
    });
  });

  it("rejects invalid targets and ranges, and allows only one open task", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await publishRuleSet(cookie, RULE_SET_V1);

    const zero = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({ target_rule_set_version: 0 }).expect(400);
    expect(zero.body.error.code).toBe("invalid_target_version");
    const unpublished = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({ target_rule_set_version: 5 }).expect(400);
    expect(unpublished.body.error.code).toBe("invalid_target_version");
    const inverted = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie)
      .send({ from: "2026-09-02T00:00:00Z", to: "2026-09-01T00:00:00Z" }).expect(400);
    expect(inverted.body.error.code).toBe("invalid_time_range");

    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
    const second = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(409);
    expect(second.body.error.code).toBe("task_already_open");
  });

  it("returns no content while no task was ever created", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    await request(app).get("/api/v1/classification/reclassification/tasks/current")
      .set("Cookie", cookie).expect(204);
  });

  it("serves the open task to devices with rules:read until they report, then hides it", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const reporter = await createDevice(cookie);
    const other = await createDevice(cookie);
    const withoutRules = await createDevice(cookie, ["events:write"]);
    const query = await createQueryToken(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);

    const assignment = await request(app).get("/api/v1/classification/reclassification/tasks/assignment")
      .set("Authorization", `Bearer ${reporter.token}`).expect(200);
    expect(assignment.body.target_rule_set_version).toBe(1);
    expect(assignment.body.from).toBeNull();
    expect(assignment.body.to).toBeNull();
    expect(assignment.body.task_id).toBeTruthy();

    await request(app).get("/api/v1/classification/reclassification/tasks/assignment")
      .set("Authorization", `Bearer ${withoutRules.token}`).expect(403);
    await request(app).get("/api/v1/classification/reclassification/tasks/assignment")
      .set("Authorization", `Bearer ${query}`).expect(403);
    await request(app).get("/api/v1/classification/reclassification/tasks/assignment").expect(401);

    await request(app).post(`/api/v1/classification/reclassification/tasks/${assignment.body.task_id}/device-reports`)
      .set("Authorization", `Bearer ${reporter.token}`)
      .send({ platform: "windows", scanned: 1, reclassified: 0, unchanged: 1, failed: 0 })
      .expect(204);

    // The reporting device no longer sees the task; another device still does.
    await request(app).get("/api/v1/classification/reclassification/tasks/assignment")
      .set("Authorization", `Bearer ${reporter.token}`).expect(204);
    const otherAssignment = await request(app).get("/api/v1/classification/reclassification/tasks/assignment")
      .set("Authorization", `Bearer ${other.token}`).expect(200);
    expect(otherAssignment.body.task_id).toBe(assignment.body.task_id);
  });

  it("aggregates device reports with server-computed unrecoverable counts and replaces repeats", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    const android = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    await upload(windows, [
      activityItem(windows),
      activityItem(windows, {
        event_id: "c4d5e6f7-0000-4000-8000-0000000000d1",
        start_at: "2026-08-22T02:00:00.000Z",
        end_at: "2026-08-22T02:20:00.000Z",
      }),
    ]);
    await upload(android, [activityItem(android, {
      event_id: "c4d5e6f7-0000-4000-8000-0000000000d2",
      device: { id: android.credentialId, platform: "android" },
      source: { kind: "android.usagestats", record_id: "usage-2" },
    })]);
    const created = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
    const taskId = created.body.task_id as string;

    // The Windows device still holds one of its two in-scope events locally.
    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: 2, reclassified: 1, unchanged: 1, failed: 0 })
      .expect(204);
    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: 1, reclassified: 1, unchanged: 0, failed: 0 })
      .expect(204);
    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${android.token}`)
      .send({ platform: "android", scanned: 1, reclassified: 0, unchanged: 1, failed: 0 })
      .expect(204);

    const status = await request(app).get("/api/v1/classification/reclassification/tasks/current")
      .set("Cookie", cookie).expect(200);
    expect(status.body.progress).toEqual({
      devices_reported: 2, scanned: 2, reclassified: 1, unchanged: 1, failed: 0, unrecoverable: 1,
    });
    const reports = status.body.device_reports as Array<Record<string, unknown>>;
    expect(reports).toHaveLength(2);
    const reportByDevice = new Map(reports.map((report) => [report.device_id, report]));
    expect(reportByDevice.get(android.credentialId)).toMatchObject({
      platform: "android", scanned: 1, reclassified: 0, unchanged: 1, failed: 0, unrecoverable: 0,
    });
    expect(reportByDevice.get(windows.credentialId)).toMatchObject({
      platform: "windows", scanned: 1, reclassified: 1, unchanged: 0, failed: 0, unrecoverable: 1,
    });
  });

  it("rejects malformed and misplaced device reports", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    const created = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
    const taskId = created.body.task_id as string;

    const negative = await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: -1, reclassified: 0, unchanged: 0, failed: 0 })
      .expect(400);
    expect(negative.body.error.code).toBe("invalid_request");

    const unknownTask = await request(app)
      .post(`/api/v1/classification/reclassification/tasks/${randomUUID()}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: 1, reclassified: 0, unchanged: 0, failed: 0 })
      .expect(404);
    expect(unknownTask.body.error.code).toBe("not_found");

    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Cookie", cookie)
      .send({ platform: "windows", scanned: 0, reclassified: 0, unchanged: 0, failed: 0 })
      .expect(401);
  });

  it("closes a task, audits the actual impact counts, and allows the next task", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await upload(windows, [activityItem(windows)]);
    await publishRuleSet(cookie, RULE_SET_V1);
    const created = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
    const taskId = created.body.task_id as string;

    await request(app)
      .post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: 1, reclassified: 1, unchanged: 0, failed: 0 })
      .expect(204);

    const closed = await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/close`)
      .set("Cookie", cookie).expect(200);
    expect(closed.body.status).toBe("closed");
    expect(closed.body.closed_at).toBeTruthy();
    expect(closed.body.progress).toMatchObject({ devices_reported: 1, reclassified: 1, unrecoverable: 0 });

    const audit = await AuditLogModel.findOne({ action: "reclassification.task_closed" })
      .lean<{ details: Record<string, unknown> }>();
    expect(audit?.details).toMatchObject({
      task_id: taskId,
      target_rule_set_version: 1,
      timezone: "UTC",
      devices_reported: 1,
      scanned: 1,
      reclassified: 1,
      unchanged: 0,
      failed: 0,
      unrecoverable: 0,
    });

    // Closing twice is idempotent and audits only once.
    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/close`)
      .set("Cookie", cookie).expect(200);
    expect(await AuditLogModel.countDocuments({ action: "reclassification.task_closed" })).toBe(1);

    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
  });

  it("refuses reports for an already closed task", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    const created = await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);
    const taskId = created.body.task_id as string;
    await request(app).post(`/api/v1/classification/reclassification/tasks/${taskId}/close`)
      .set("Cookie", cookie).expect(200);

    const late = await request(app)
      .post(`/api/v1/classification/reclassification/tasks/${taskId}/device-reports`)
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ platform: "windows", scanned: 1, reclassified: 0, unchanged: 1, failed: 0 })
      .expect(409);
    expect(late.body.error.code).toBe("task_not_open");
  });

  it("keeps owner-only control over task management endpoints", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    const query = await createQueryToken(cookie);

    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Authorization", `Bearer ${windows.token}`).send({}).expect(401);
    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Authorization", `Bearer ${query}`).send({}).expect(401);
    await request(app).get("/api/v1/classification/reclassification/tasks/current")
      .set("Authorization", `Bearer ${query}`).expect(401);
    await request(app).post(`/api/v1/classification/reclassification/tasks/${randomUUID()}/close`)
      .set("Authorization", `Bearer ${windows.token}`).expect(401);
  });
});

describe("reclassification submissions through the batch protocol", () => {
  it("re-issues the same event identity at a higher revision without duplicating durations", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    await publishRuleSet(cookie, RULE_SET_V1);
    const original = activityItem(windows);
    await upload(windows, [original]);

    await publishRuleSet(cookie, RULE_SET_V2);
    await request(app).post("/api/v1/classification/reclassification/tasks")
      .set("Cookie", cookie).send({}).expect(200);

    // The device re-runs its local engine under v2 and submits the same
    // event identity with a higher revision from the device revision space.
    const reissued = activityItem(windows, {
      event_id: original.event_id,
      source: original.source,
      revision: 2,
      payload: {
        ...(original.payload as Record<string, unknown>),
        subject_id: "project.liveqs",
        classification: { rule_id: "edge.bilibili.title", rule_version: 2, confidence: 0.8 },
      },
    }) as Record<string, unknown>;
    const batch = await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ events: [reissued] }).expect(200);
    expect(batch.body.results[0]).toMatchObject({ event_id: original.event_id, status: "accepted", revision: 2 });

    // One logical fact: exactly one event, the latest revision, no duplicated minutes.
    const range = "from=2026-08-21T00:00:00Z&to=2026-08-22T00:00:00Z&timezone=Asia/Shanghai";
    const page = await request(app).get(`/api/v1/events?${range}`).set("Cookie", cookie).expect(200);
    expect(page.body.data).toHaveLength(1);
    expect(page.body.data[0]).toMatchObject({ event_id: original.event_id, revision: 2 });
    expect(page.body.data[0].payload.subject_id).toBe("project.liveqs");
    expect(page.body.data[0].payload.classification).toMatchObject({ rule_version: 2 });

    const day = await request(app).get("/api/v1/metrics/usage/day?date=2026-08-21&timezone=Asia/Shanghai")
      .set("Cookie", cookie).expect(200);
    expect(day.body.metrics).toEqual({ device_minutes: 20, active_minutes: 20 });

    // The superseded interpretation stays archived and auditable.
    const archived = await EventRevisionModel.findOne({ event_id: original.event_id }).lean<{ revision: number }>();
    expect(archived?.revision).toBe(1);
  });

  it("never lets a reclassification overwrite a manual Owner correction", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const windows = await createDevice(cookie);
    const original = activityItem(windows);
    await upload(windows, [original]);

    const corrected = await request(app).post(`/api/v1/events/${original.event_id}/corrections`)
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.subject_id", value: "project.liveqs" }], reason: "这是工作不是娱乐" })
      .expect(200);
    expect(corrected.body.revision).toBeGreaterThanOrEqual(1_000_000_001);

    // The device cannot know about the correction; its higher-than-device
    // revision still sits below the reserved space, so the batch compare
    // answers stale_revision and the human decision stands.
    const reissued = activityItem(windows, {
      event_id: original.event_id,
      source: original.source,
      revision: 2,
      payload: {
        ...(original.payload as Record<string, unknown>),
        subject_id: "svc.bilibili",
        classification: { rule_id: "edge.bilibili.title", rule_version: 2, confidence: 0.8 },
      },
    }) as Record<string, unknown>;
    const batch = await request(app).post("/api/v1/events/batch")
      .set("Authorization", `Bearer ${windows.token}`)
      .send({ events: [reissued] }).expect(200);
    expect(batch.body.results[0]).toMatchObject({ event_id: original.event_id, status: "stale_revision" });

    const range = "from=2026-08-21T00:00:00Z&to=2026-08-22T00:00:00Z&timezone=Asia/Shanghai";
    const page = await request(app).get(`/api/v1/events?${range}`).set("Cookie", cookie).expect(200);
    expect(page.body.data[0].payload.subject_id).toBe("project.liveqs");
    expect(page.body.data[0].correction).toBeTruthy();

    // Manually corrected events are outside the reclassification scope: they
    // are neither estimated nor reported as unrecoverable.
    const estimate = await request(app).get("/api/v1/classification/reclassification/estimate")
      .set("Cookie", cookie).expect(200);
    expect(estimate.body.total_events).toBe(0);
  });
});
