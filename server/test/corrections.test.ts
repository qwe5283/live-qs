import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { AuditLogModel, CredentialModel, EventModel, EventRevisionModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import { CORRECTION_REVISION_BASE } from "../src/modules/events/payload-registry.js";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_corrections";
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
    console.warn(`[corrections.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[corrections.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    CredentialModel.syncIndexes(),
    EventModel.syncIndexes(),
    EventRevisionModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
    AuditLogModel.syncIndexes(),
  ]);
  app = createApp(buildEnv());
});

afterAll(async () => {
  if (dbReady) await disconnectDatabase();
});

async function ownerCookie(): Promise<string> {
  await request(app).post("/api/v1/owner/setup").send({ password: ownerPassword }).expect(204);
  const login = await request(app).post("/api/v1/owner/login").send({ password: ownerPassword }).expect(204);
  const setCookie = Array.isArray(login.headers["set-cookie"]) ? login.headers["set-cookie"].at(0) : login.headers["set-cookie"];
  return sessionCookieValue(setCookie ?? "");
}

async function createCredential(
  cookie: string,
  kind: "device_token" | "query_token",
  scopes: string[],
  privacyCeiling: "normal" | "sensitive" | "private" = "sensitive",
): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind, name: "修正测试凭据", scopes, privacy_ceiling: privacyCeiling })
    .expect(201);
  return response.body.token;
}

/** A structured expense fact extracted on-device from a WeChat Pay notification. */
function expenseItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0001-4000-8000-000000000001",
    event_type: "payment.transaction",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.wechatpay", record_id: "wechat-notification-cc01" },
    device: { id: "phone", platform: "android" },
    start_at: "2026-09-01T03:26:03.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.3.0", observed_at: "2026-09-01T03:26:05.000Z" },
    invalidated: false,
    payload: {
      amount: { value: 2150, currency: "CNY" },
      direction: "expense",
      merchant: "瑞幸咖啡",
      category: "food",
      pending_confirmation: false,
    },
    ...overrides,
  };
}

/** A one-hour Windows foreground interval on 2026-09-01 (18:00–19:00 Asia/Shanghai). */
function activityItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "a1b2c3d4-0002-4000-8000-000000000002",
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "windows.foreground", record_id: "segment-2001" },
    device: { id: "workstation", platform: "windows" },
    start_at: "2026-09-01T10:00:00.000Z",
    end_at: "2026-09-01T11:00:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.1.0", observed_at: "2026-09-01T11:00:01.000Z" },
    invalidated: false,
    payload: {
      application_id: "msedge.exe",
      application_label: "Microsoft Edge",
      is_afk: false,
      duration: { value: 3_600_000, unit: "ms" },
    },
    ...overrides,
  };
}

async function uploadBatch(token: string, events: Record<string, unknown>[]) {
  return request(app)
    .post("/api/v1/events/batch")
    .set("Authorization", `Bearer ${token}`)
    .send({ events })
    .expect(200);
}

function correctionPath(eventId: string): string {
  return `/api/v1/events/${eventId}/corrections`;
}

describe("auditable owner corrections", () => {
  it("corrects contract-approved structured fields with an optional reason, keeping identity and archiving the original", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"]);
    await uploadBatch(device, [expenseItem()]);

    const response = await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Cookie", cookie)
      .send({
        fields: [
          { path: "payload.amount.value", value: 9900 },
          { path: "payload.merchant", value: "星巴克" },
        ],
        reason: "金额识别错误",
      })
      .expect(200);

    expect(response.body.revision).toBe(CORRECTION_REVISION_BASE + 1);
    expect(response.body.changed_fields).toEqual(["payload.amount.value", "payload.merchant"]);
    expect(response.body.reason).toBe("金额识别错误");
    expect(response.body.invalidated).toBe(false);
    expect(response.body.event.event_id).toBe(expenseItem().event_id);
    expect(response.body.event.payload).toEqual({
      amount: { value: 9900, currency: "CNY" }, // currency is preserved by the leaf-path merge
      direction: "expense",
      merchant: "星巴克",
      category: "food",
      pending_confirmation: false,
    });
    expect(response.body.event.correction.reason).toBe("金额识别错误");
    expect(response.body.event.correction.corrected_at).toBeTruthy();
    expect(response.body.impact).toEqual([
      {
        metric: "payment.transaction_totals",
        timezone: "UTC",
        affected_ranges: [{ from: "2026-09-01", to: "2026-09-01" }],
        result_count: 1,
      },
    ]);

    // Default queries return the corrected (manual) interpretation with its provenance.
    const page = await request(app)
      .get("/api/v1/payment/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(page.body.data).toHaveLength(1);
    expect(page.body.data[0].payload.amount.value).toBe(9900);
    expect(page.body.data[0].payload.merchant).toBe("星巴克");
    expect(page.body.data[0].correction.reason).toBe("金额识别错误");

    // The original structured observation and its source remain archived.
    const archived = await EventRevisionModel.find({ event_id: expenseItem().event_id }).lean();
    expect(archived).toHaveLength(1);
    expect(archived[0]!.revision).toBe(1);
    expect(archived[0]!.document.data.amount.value).toBe(2150);
    expect(archived[0]!.document.data.merchant).toBe("瑞幸咖啡");
    expect(archived[0]!.document.source_kind).toBe("android.wechatpay");

    // The audit record shows actor, time, changed fields, and reason.
    const audit = await AuditLogModel.find({ action: "event.corrected" }).lean();
    expect(audit).toHaveLength(1);
    expect(audit[0]!.actor_type).toBe("user");
    expect(audit[0]!.details.event_id).toBe(expenseItem().event_id);
    expect(audit[0]!.details.from_revision).toBe(1);
    expect(audit[0]!.details.to_revision).toBe(CORRECTION_REVISION_BASE + 1);
    expect(audit[0]!.details.changed_fields).toEqual(["payload.amount.value", "payload.merchant"]);
    expect(audit[0]!.details.reason).toBe("金额识别错误");
    expect(audit[0]!.details.result_count).toBe(1);
  });

  it("answers stale_revision to later device uploads so a human correction is never overwritten", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"]);
    await uploadBatch(device, [expenseItem()]);
    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.amount.value", value: 9900 }] })
      .expect(200);

    // A checkpoint-style higher device revision arrives after the correction.
    const retry = await uploadBatch(device, [expenseItem({ revision: 2 })]);
    expect(retry.body.results[0]).toMatchObject({ event_id: expenseItem().event_id, status: "stale_revision" });

    const page = await request(app)
      .get("/api/v1/payment/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(page.body.data[0].revision).toBe(CORRECTION_REVISION_BASE + 1);
    expect(page.body.data[0].payload.amount.value).toBe(9900);

    // Device revisions may never reach into the reserved manual-correction space.
    const hijack = await uploadBatch(device, [expenseItem({ revision: CORRECTION_REVISION_BASE })]);
    expect(hijack.body.results[0]).toMatchObject({ status: "rejected", error: { code: "revision_reserved" } });
  });

  it("marks false positives invalid so they leave default timeline views and statistics but stay archived", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write"]);
    const second = activityItem({
      event_id: "a1b2c3d4-0003-4000-8000-000000000003",
      source: { kind: "windows.foreground", record_id: "segment-2002" },
      start_at: "2026-09-01T12:00:00.000Z",
      end_at: "2026-09-01T13:00:00.000Z",
    });
    await uploadBatch(device, [activityItem(), second]);

    const response = await request(app)
      .post(correctionPath(activityItem().event_id as string))
      .set("Cookie", cookie)
      .send({ invalidate: true, reason: "误报：其实是锁屏" })
      .expect(200);
    expect(response.body.invalidated).toBe(true);
    expect(response.body.changed_fields).toEqual([]);
    expect(response.body.revision).toBe(CORRECTION_REVISION_BASE + 1);
    expect(response.body.impact[0]).toMatchObject({ metric: "usage.app_minutes", result_count: 1 });

    // The default timeline no longer returns the invalidated observation...
    const timeline = await request(app)
      .get("/api/v1/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(timeline.body.data.map((event: { event_id: string }) => event.event_id)).toEqual([second.event_id]);

    // ...and the derived usage statistics rebuild without it.
    const metrics = await request(app)
      .get("/api/v1/metrics/usage/day?date=2026-09-01&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(metrics.body.metrics.device_minutes).toBe(60);

    // The fact is retained, not erased.
    const stored = await EventModel.findOne({ id: activityItem().event_id }).lean();
    expect(stored?.invalidated).toBe(true);
    expect(stored?.data.application_id).toBe("msedge.exe");
    const archived = await EventRevisionModel.find({ event_id: activityItem().event_id }).lean();
    expect(archived).toHaveLength(1);
    expect(archived[0]!.document.invalidated).toBe(false);
  });

  it("confirms a pending-confirmation payment through a higher revision and drops it from the pending count", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"]);
    await uploadBatch(device, [expenseItem({ payload: { ...expenseItem().payload as Record<string, unknown>, pending_confirmation: true } })]);

    const before = await request(app)
      .get("/api/v1/payment/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(before.body.context.pending_confirmation_count).toBe(1);

    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.pending_confirmation", value: false }], reason: "确认为独立消费" })
      .expect(200);

    const after = await request(app)
      .get("/api/v1/payment/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(after.body.context.pending_confirmation_count).toBe(0);
    expect(after.body.data[0].payload.pending_confirmation).toBe(false);
    expect(after.body.data[0].correction).toBeTruthy();
  });

  it("rebuilds derived summaries after key time corrections and reports the affected report days", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write"]);
    await uploadBatch(device, [activityItem()]);

    const before = await request(app)
      .get("/api/v1/metrics/usage/day?date=2026-09-01&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(before.body.metrics.device_minutes).toBe(60);

    const response = await request(app)
      .post(correctionPath(activityItem().event_id as string))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "end_at", value: "2026-09-01T12:00:00.000Z" }], reason: "区间漏采" })
      .expect(200);
    // The declared duration is re-derived from the corrected bounds.
    expect(response.body.event.payload.duration).toEqual({ value: 7_200_000, unit: "ms" });
    expect(response.body.impact).toEqual([
      { metric: "usage.app_minutes", timezone: "UTC", affected_ranges: [{ from: "2026-09-01", to: "2026-09-01" }], result_count: 1 },
    ]);

    const after = await request(app)
      .get("/api/v1/metrics/usage/day?date=2026-09-01&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(after.body.metrics.device_minutes).toBe(120);
    expect(after.body.metrics.active_minutes).toBe(120);
  });

  it("rejects corrections to non-approved fields, invalid merged payloads, and no-op requests", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"]);
    await uploadBatch(device, [expenseItem()]);

    const reject = (body: Record<string, unknown>, code: string) =>
      request(app)
        .post(correctionPath(expenseItem().event_id as string))
        .set("Cookie", cookie)
        .send(body)
        .expect(400)
        .then((response) => expect(response.body.error.code).toBe(code));

    // Free text can never be smuggled in: the path itself is not correctable.
    await reject({ fields: [{ path: "payload.notification_text", value: "微信支付凭证" }] }, "field_not_correctable");
    // Interpretation fields outside the type's approved set are refused.
    await reject({ fields: [{ path: "payload.is_afk", value: true }] }, "field_not_correctable");
    // Identity, source, and provenance are never correctable.
    await reject({ fields: [{ path: "source.kind", value: "windows.foreground" }] }, "field_not_correctable");
    await reject({ fields: [{ path: "provenance.collector_version", value: "9.9.9" }] }, "field_not_correctable");
    // The merged payload must satisfy the registered schema.
    await reject({ fields: [{ path: "payload.amount.value", value: 0 }] }, "invalid_correction");
    await reject({ fields: [{ path: "payload.merchant", value: "C:\\Users\\receipt.txt" }] }, "invalid_correction");
    await reject({ fields: [{ path: "payload.category", value: "gambling" }] }, "invalid_correction");
    await reject({ fields: [{ path: "start_at", value: "not-an-instant" }] }, "invalid_correction");
    // A request that changes nothing is not a correction.
    await reject({ fields: [] }, "invalid_correction");
    await reject({ fields: [{ path: "payload.merchant", value: "瑞幸咖啡" }] }, "invalid_correction");

    const revisions = await EventModel.distinct("revision", { id: expenseItem().event_id });
    expect(revisions).toEqual([1]);
  });

  it("forbids corrections to unauthenticated callers, device tokens, and query tokens", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "payment:write"]);
    const query = await createCredential(cookie, "query_token", ["events:read", "payment:read"]);
    await uploadBatch(device, [expenseItem()]);

    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .send({ fields: [{ path: "payload.merchant", value: "星巴克" }] })
      .expect(401);
    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Authorization", `Bearer ${device}`)
      .send({ fields: [{ path: "payload.merchant", value: "星巴克" }] })
      .expect(401);
    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Authorization", `Bearer ${query}`)
      .send({ fields: [{ path: "payload.merchant", value: "星巴克" }] })
      .expect(401);

    const audit = await AuditLogModel.countDocuments({ action: "event.corrected" });
    expect(audit).toBe(0);
  });

  it("answers 404 for unknown events and allocates increasing revisions across repeated corrections", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"]);
    await uploadBatch(device, [expenseItem()]);

    await request(app)
      .post(correctionPath("f0000000-0000-4000-8000-000000000000"))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.merchant", value: "星巴克" }] })
      .expect(404);

    await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.merchant", value: "星巴克" }] })
      .expect(200);
    const second = await request(app)
      .post(correctionPath(expenseItem().event_id as string))
      .set("Cookie", cookie)
      .send({ fields: [{ path: "payload.category", value: "transport" }] })
      .expect(200);
    expect(second.body.revision).toBe(CORRECTION_REVISION_BASE + 2);

    const archived = await EventRevisionModel.find({ event_id: expenseItem().event_id }).sort({ revision: 1 }).lean();
    expect(archived.map((row) => row.revision)).toEqual([1, CORRECTION_REVISION_BASE + 1]);
    expect(archived[0]!.document.data.merchant).toBe("瑞幸咖啡");
    expect(archived[1]!.document.data.merchant).toBe("星巴克");
  });

  it("reinstates an invalidated observation only through an auditable owner revision", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write"]);
    await uploadBatch(device, [activityItem()]);

    await request(app)
      .post(correctionPath(activityItem().event_id as string))
      .set("Cookie", cookie)
      .send({ invalidate: true })
      .expect(200);
    await request(app)
      .post(correctionPath(activityItem().event_id as string))
      .set("Cookie", cookie)
      .send({ invalidate: false, reason: "复核后恢复" })
      .expect(200);

    const timeline = await request(app)
      .get("/api/v1/events?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&timezone=Asia/Shanghai")
      .set("Cookie", cookie)
      .expect(200);
    expect(timeline.body.data).toHaveLength(1);
    expect(timeline.body.data[0].invalidated).toBe(false);
    expect(timeline.body.data[0].correction.reason).toBe("复核后恢复");

    // A device re-upload of the original revision stays stale throughout.
    const retry = await uploadBatch(device, [activityItem()]);
    expect(retry.body.results[0]).toMatchObject({ status: "stale_revision" });
  });
});
