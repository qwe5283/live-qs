import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_payment_events";
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
    console.warn(`[payment-events.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[payment-events.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
  }
});

beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
  await mongoose.connection.dropDatabase();
  await Promise.all([
    CredentialModel.syncIndexes(),
    EventModel.syncIndexes(),
    OwnerCredentialModel.syncIndexes(),
    OwnerSessionModel.syncIndexes(),
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
  privacyCeiling: "normal" | "sensitive" | "private" = "normal",
): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind, name: "支付测试凭据", scopes, privacy_ceiling: privacyCeiling })
    .expect(201);
  return response.body.token;
}

/** A structured expense fact extracted on-device from a WeChat Pay notification. */
function expenseItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "b2c3d4e5-0001-4000-8000-000000000001",
    event_type: "payment.transaction",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.wechatpay", record_id: "wechat-notification-aa01" },
    device: { id: "phone", platform: "android" },
    start_at: "2026-08-20T03:26:03.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "sensitive",
    revision: 1,
    finalization_state: "final",
    provenance: { collector_version: "0.3.0", observed_at: "2026-08-20T03:26:05.000Z" },
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

/** A structured income fact (money received). */
function incomeItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return expenseItem({
    event_id: "b2c3d4e5-0002-4000-8000-000000000002",
    source: { kind: "android.wechatpay", record_id: "wechat-notification-aa02" },
    start_at: "2026-08-20T09:10:00.000Z",
    payload: {
      amount: { value: 8800, currency: "CNY" },
      direction: "income",
      merchant: "张三",
      category: "transfer",
      pending_confirmation: false,
    },
    ...overrides,
  });
}

/** A foreground activity interval for scope-interaction assertions. */
function activityItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    event_id: "b2c3d4e5-0003-4000-8000-000000000003",
    event_type: "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "android.usagestats", record_id: "usage-session-tv.danmaku.bili-1754043000000" },
    device: { id: "phone", platform: "android" },
    start_at: "2026-08-01T13:30:00.000Z",
    end_at: "2026-08-01T14:05:00.000Z",
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: 1,
    finalization_state: "checkpoint",
    provenance: { collector_version: "0.3.0", observed_at: "2026-08-01T14:06:00.000Z" },
    invalidated: false,
    payload: {
      application_id: "tv.danmaku.bili",
      is_afk: false,
      duration: { value: 2_100_000, unit: "ms" },
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

const READ_RANGE = "from=2026-08-01T00:00:00Z&to=2026-08-22T00:00:00Z&timezone=Asia/Shanghai";

describe("payment transaction observations", () => {
  it("accepts extracted transaction facts and reads them back via the payment domain endpoint, idempotently", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"], "sensitive");

    const pending = expenseItem({
      event_id: "b2c3d4e5-0004-4000-8000-000000000004",
      source: { kind: "android.wechatpay", record_id: "wechat-notification-aa04" },
      start_at: "2026-08-20T03:31:03.000Z",
      payload: {
        amount: { value: 2150, currency: "CNY" },
        direction: "expense",
        merchant: "瑞幸咖啡",
        category: "food",
        pending_confirmation: true,
      },
    });
    const batch = await uploadBatch(device, [expenseItem(), incomeItem(), pending]);
    expect(batch.body.results.map((result: { status: string }) => result.status)).toEqual(["accepted", "accepted", "accepted"]);

    // Redelivery of the same source record stays one logical fact.
    const redelivery = await uploadBatch(device, [expenseItem()]);
    expect(redelivery.body.results[0]).toMatchObject({ event_id: expenseItem().event_id, revision: 1, status: "duplicate" });

    const paymentPage = await request(app)
      .get(`/api/v1/payment/events?${READ_RANGE}`)
      .set("Cookie", cookie)
      .expect(200);
    expect(paymentPage.body.data).toHaveLength(3);
    expect(paymentPage.body.context.provenance).toEqual(["android.wechatpay"]);
    expect(paymentPage.body.context.completeness).toBe("complete");
    const expense = paymentPage.body.data.find((event: { event_id: string }) => event.event_id === expenseItem().event_id);
    expect(expense.start_at).toBe("2026-08-20T03:26:03.000Z");
    expect(expense.end_at).toBeUndefined();
    expect(expense.source).toEqual({ kind: "android.wechatpay", record_id: "wechat-notification-aa01" });
    expect(expense.payload).toEqual({
      amount: { value: 2150, currency: "CNY" },
      direction: "expense",
      merchant: "瑞幸咖啡",
      category: "food",
      pending_confirmation: false,
    });
    expect(expense.privacy_level).toBe("sensitive");
    const flagged = paymentPage.body.data.find((event: { payload: { pending_confirmation: boolean } }) => event.payload.pending_confirmation);
    expect(flagged).toBeDefined();

    // The generic event read surfaces the same facts for the Owner session.
    const allPage = await request(app)
      .get(`/api/v1/events?${READ_RANGE}`)
      .set("Cookie", cookie)
      .expect(200);
    expect(allPage.body.data).toHaveLength(3);
  });

  it("enforces payment:write per item while permitted domains still progress in the same batch", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const activityOnly = await createCredential(cookie, "device_token", ["events:write"], "sensitive");
    const paymentOnly = await createCredential(cookie, "device_token", ["payment:write"], "sensitive");

    const mixed = await uploadBatch(activityOnly, [activityItem(), expenseItem()]);
    expect(mixed.body.results[0]).toMatchObject({ status: "accepted" });
    expect(mixed.body.results[1]).toMatchObject({ status: "rejected", error: { code: "insufficient_scope" } });

    const paymentUpload = await uploadBatch(paymentOnly, [expenseItem({ event_id: "c3d4e5f6-0001-4000-8000-000000000001" }), activityItem()]);
    expect(paymentUpload.body.results[0]).toMatchObject({ status: "accepted" });
    expect(paymentUpload.body.results[1]).toMatchObject({ status: "rejected", error: { code: "insufficient_scope" } });
  });

  it("restricts payment reads to payment:read tokens and never leaks them to other read scopes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["events:write", "payment:write"], "sensitive");
    await uploadBatch(device, [expenseItem(), incomeItem(), activityItem()]);

    const activityReader = await createCredential(cookie, "query_token", ["events:read"], "sensitive");
    const paymentDenied = await request(app)
      .get(`/api/v1/payment/events?${READ_RANGE}`)
      .set("Authorization", `Bearer ${activityReader}`)
      .expect(403);
    expect(paymentDenied.body.error.code).toBe("insufficient_scope");

    // The generic read is domain-scoped: payments are hidden and the context
    // reports the page as partial instead of pretending completeness.
    const genericPage = await request(app)
      .get(`/api/v1/events?${READ_RANGE}`)
      .set("Authorization", `Bearer ${activityReader}`)
      .expect(200);
    expect(genericPage.body.data.map((event: { event_type: string }) => event.event_type)).toEqual(["activity.interval"]);
    expect(genericPage.body.context.completeness).toBe("partial");

    const paymentReader = await createCredential(cookie, "query_token", ["events:read", "payment:read"], "sensitive");
    const paymentPage = await request(app)
      .get(`/api/v1/payment/events?${READ_RANGE}`)
      .set("Authorization", `Bearer ${paymentReader}`)
      .expect(200);
    expect(paymentPage.body.data).toHaveLength(2);
    expect(paymentPage.body.context.completeness).toBe("complete");

    // A payment-only token cannot use the generic read (events:read required).
    const paymentOnlyReader = await createCredential(cookie, "query_token", ["payment:read"], "sensitive");
    await request(app)
      .get(`/api/v1/events?${READ_RANGE}`)
      .set("Authorization", `Bearer ${paymentOnlyReader}`)
      .expect(403);
    const paymentOnlyPage = await request(app)
      .get(`/api/v1/payment/events?${READ_RANGE}`)
      .set("Authorization", `Bearer ${paymentOnlyReader}`)
      .expect(200);
    expect(paymentOnlyPage.body.data).toHaveLength(2);
  });

  it("defaults payment transactions to sensitive so a normal-ceiling credential cannot upload them", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"], "normal");

    const omitted = expenseItem();
    delete (omitted as Record<string, unknown>).privacy_level;
    const rejected = await uploadBatch(device, [omitted]);
    expect(rejected.body.results[0]).toMatchObject({ status: "rejected", error: { code: "privacy_ceiling_exceeded" } });

    const rows = await EventModel.countDocuments({ type: "payment.transaction" });
    expect(rows).toBe(0);
  });

  it("rejects invalid or free-text-carrying transaction payloads with stable diagnosable codes", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"], "sensitive");

    const fractionalAmount = expenseItem({ event_id: "d4e5f6a7-0001-4000-8000-000000000001" });
    (fractionalAmount.payload as { amount: { value: number } }).amount.value = 12.5;
    const zeroAmount = expenseItem({ event_id: "d4e5f6a7-0002-4000-8000-000000000002" });
    (zeroAmount.payload as { amount: { value: number } }).amount.value = 0;
    const lowerCurrency = expenseItem({ event_id: "d4e5f6a7-0003-4000-8000-000000000003" });
    (lowerCurrency.payload as { amount: { currency: string } }).amount.currency = "cny";
    const unknownDirection = expenseItem({ event_id: "d4e5f6a7-0004-4000-8000-000000000004" });
    (unknownDirection.payload as { direction: string }).direction = "paid";
    const freeTextCarrier = expenseItem({ event_id: "d4e5f6a7-0005-4000-8000-000000000005" });
    (freeTextCarrier.payload as Record<string, unknown>).notification_text = "微信支付凭证-付款21.50元";
    const pathMerchant = expenseItem({ event_id: "d4e5f6a7-0006-4000-8000-000000000006" });
    (pathMerchant.payload as { merchant: string }).merchant = "C:\\Users\\owner\\receipt.txt";
    const wrongSourceKind = expenseItem({
      event_id: "d4e5f6a7-0007-4000-8000-000000000007",
      source: { kind: "android.healthconnect", record_id: "hc-record-not-payment" },
    });
    const unknownType = expenseItem({ event_id: "d4e5f6a7-0008-4000-8000-000000000008", event_type: "payment.refund" });
    const instantWithEnd = expenseItem({ event_id: "d4e5f6a7-0009-4000-8000-000000000009", end_at: "2026-08-20T03:27:03.000Z" });

    const batch = await uploadBatch(device, [
      fractionalAmount,
      zeroAmount,
      lowerCurrency,
      unknownDirection,
      freeTextCarrier,
      pathMerchant,
      wrongSourceKind,
      unknownType,
      instantWithEnd,
    ]);
    expect(batch.body.results).toHaveLength(9);
    expect(batch.body.results.map((result: { status: string }) => result.status)).toEqual(Array.from({ length: 9 }, () => "rejected"));
    expect(batch.body.results.every((result: { error?: { code?: string } }) => result.error?.code === "invalid_event"
      || result.error?.code === "unknown_event_type")).toBe(true);
    expect(batch.body.results.at(-2).error?.code).toBe("unknown_event_type");

    const rows = await EventModel.countDocuments({});
    expect(rows).toBe(0);
  });

  it("reconciles amounts and source record counts against chosen samples, with duplicates never double-booking", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const device = await createCredential(cookie, "device_token", ["payment:write"], "sensitive");

    // Three real-shape samples: expenses of 21.50 and 45.00 CNY, income 88.00 CNY.
    const second = expenseItem({
      event_id: "e5f6a7b8-0001-4000-8000-000000000001",
      source: { kind: "android.wechatpay", record_id: "wechat-notification-bb01" },
      start_at: "2026-08-21T11:00:00.000Z",
      payload: {
        amount: { value: 4500, currency: "CNY" },
        direction: "expense",
        merchant: "美团外卖",
        category: "food",
        pending_confirmation: false,
      },
    });
    const firstUpload = await uploadBatch(device, [expenseItem(), second, incomeItem()]);
    const acks = firstUpload.body.results.map((result: { status: string }) => result.status);
    expect(acks).toEqual(["accepted", "accepted", "accepted"]);

    // Retry the whole batch: every item answers duplicate, nothing changes.
    const retry = await uploadBatch(device, [expenseItem(), second, incomeItem()]);
    expect(retry.body.results.map((result: { status: string }) => result.status)).toEqual(["duplicate", "duplicate", "duplicate"]);

    const page = await request(app)
      .get(`/api/v1/payment/events?${READ_RANGE}&page_size=200`)
      .set("Cookie", cookie)
      .expect(200);
    const transactions = page.body.data as Array<{
      payload: { amount: { value: number; currency: string }; direction: string };
    }>;
    // Source record count reconciles: three notifications in, three facts out.
    expect(transactions).toHaveLength(3);
    const expenseFen = transactions
      .filter((event) => event.payload.direction === "expense")
      .reduce((total, event) => total + event.payload.amount.value, 0);
    const incomeFen = transactions
      .filter((event) => event.payload.direction === "income")
      .reduce((total, event) => total + event.payload.amount.value, 0);
    expect(expenseFen).toBe(2150 + 4500);
    expect(incomeFen).toBe(8800);
    const currencies = new Set(transactions.map((event) => event.payload.amount.currency));
    expect(currencies).toEqual(new Set(["CNY"]));
  });
});
