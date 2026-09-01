import mongoose from "mongoose";
import request from "supertest";
import { beforeAll, afterAll, beforeEach, describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { Env } from "../src/config/env.js";
import { connectDatabase, disconnectDatabase } from "../src/db/connection.js";
import { CredentialModel, EventModel, EventRevisionModel, OwnerCredentialModel, OwnerSessionModel } from "../src/db/models.js";
import type { Response } from "supertest";

// Tests use a dedicated database so the developer database is never touched.
const testUri = "mongodb://127.0.0.1:27017/live_qs_test_revisions";
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
    console.warn(`[events-revisions.test] Real MongoDB is not reachable at ${testUri}.`);
    console.warn("[events-revisions.test] Start it with `docker compose up -d` inside server/. Skipping these tests.");
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

async function createDeviceToken(cookie: string): Promise<string> {
  const response = await request(app)
    .post("/api/v1/credentials")
    .set("Cookie", cookie)
    .send({ kind: "device_token", name: "测试设备", scopes: ["events:write"] })
    .expect(201);
  return response.body.token;
}

const EVENT_ID = "018f62d6-4f34-7c82-9085-57c8af1d7a44";

interface EnvelopeOverrides {
  event_id?: string;
  event_type?: string;
  start_at?: string;
  end_at?: string | null;
  revision?: number;
  finalization_state?: "checkpoint" | "final";
  payload?: Record<string, unknown>;
  provenance?: { collector_version: string; observed_at: string };
}

/** A final five-minute Edge interval starting at 05:00, matching the contract example. */
function envelopeItem(overrides: EnvelopeOverrides = {}): Record<string, unknown> {
  return {
    event_id: overrides.event_id ?? EVENT_ID,
    event_type: overrides.event_type ?? "activity.interval",
    schema_version: 1,
    owner_id: "test-user",
    source: { kind: "windows.foreground", record_id: "segment-1042" },
    device: { id: "windows-workstation", platform: "windows" },
    start_at: overrides.start_at ?? "2026-07-28T01:00:00.000Z",
    end_at: overrides.end_at === undefined ? "2026-07-28T01:05:00.000Z" : overrides.end_at,
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: "normal",
    revision: overrides.revision ?? 1,
    finalization_state: overrides.finalization_state ?? "final",
    provenance: overrides.provenance ?? { collector_version: "0.1.0", observed_at: "2026-07-28T01:05:01.000Z" },
    invalidated: false,
    payload: overrides.payload ?? {
      application_id: "msedge.exe",
      application_label: "Microsoft Edge",
      is_afk: false,
      duration: { value: 300_000, unit: "ms" },
    },
  };
}

async function uploadBatch(token: string, events: Record<string, unknown>[]) {
  return request(app)
    .post("/api/v1/events/batch")
    .set("Authorization", `Bearer ${token}`)
    .send({ events })
    .expect(200);
}

async function readEvents(cookie: string): Promise<{ data: Array<Record<string, unknown>> }> {
  const response = await request(app)
    .get("/api/v1/events?from=2026-07-27T00:00:00Z&to=2026-07-29T00:00:00Z&timezone=Asia/Shanghai")
    .set("Cookie", cookie)
    .expect(200);
  return response.body;
}

function firstEvent(page: { data: Array<Record<string, unknown>> }): Record<string, unknown> {
  const first = page.data.at(0);
  if (!first) throw new Error("expected at least one event");
  return first;
}

describe("batch revision semantics", () => {
  it("keeps exactly one latest logical event when the same revision arrives ten times", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const statuses: string[] = [];
    for (let attempt = 0; attempt < 10; attempt++) {
      const upload = await uploadBatch(token, [envelopeItem()]);
      statuses.push(upload.body.results[0].status);
    }
    expect(statuses.at(0)).toBe("accepted");
    expect(statuses.slice(1)).toEqual(Array.from({ length: 9 }, () => "duplicate"));

    const rows = await EventModel.countDocuments({ id: EVENT_ID });
    expect(rows).toBe(1);
    const page = await readEvents(cookie);
    expect(page.data).toHaveLength(1);
    expect(firstEvent(page).revision).toBe(1);
  });

  it("accepts a higher revision as the replacement of the same logical event", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const checkpoint = await uploadBatch(token, [
      envelopeItem({
        end_at: "2026-07-28T01:02:00.000Z",
        revision: 1,
        finalization_state: "checkpoint",
        payload: {
          application_id: "msedge.exe",
          application_label: "Microsoft Edge",
          is_afk: false,
          duration: { value: 120_000, unit: "ms" },
        },
      }),
    ]);
    expect(checkpoint.body.results[0].status).toBe("accepted");

    const final = await uploadBatch(token, [envelopeItem({ revision: 2 })]);
    expect(final.body.results[0]).toMatchObject({ event_id: EVENT_ID, revision: 2, status: "accepted" });

    // The superseded revision is archived, never destroyed.
    const archived = await EventRevisionModel.find({ event_id: EVENT_ID }).sort({ revision: 1 }).lean();
    expect(archived.map((entry) => entry.revision)).toEqual([1]);
    expect((archived[0]?.document as { data: { duration: { value: number } } }).data.duration.value).toBe(120_000);

    const rows = await EventModel.countDocuments({ id: EVENT_ID });
    expect(rows).toBe(1);
    const page = await readEvents(cookie);
    expect(page.data).toHaveLength(1);
    expect(firstEvent(page).revision).toBe(2);
    expect(firstEvent(page).finalization_state).toBe("final");
    expect((firstEvent(page).payload as { duration: { value: number } }).duration.value).toBe(300_000);
  });

  it("answers stale_revision for an older revision and never overwrites the newer fact", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);
    await uploadBatch(token, [envelopeItem({ revision: 2 })]);

    const stale = await uploadBatch(token, [
      envelopeItem({
        end_at: "2026-07-28T01:00:01.000Z",
        revision: 1,
        payload: {
          application_id: "explorer.exe",
          is_afk: true,
          duration: { value: 1_000, unit: "ms" },
        },
      }),
    ]);
    expect(stale.body.results[0]).toMatchObject({ event_id: EVENT_ID, revision: 1, status: "stale_revision" });

    const rows = await EventModel.countDocuments({ id: EVENT_ID });
    expect(rows).toBe(1);
    const page = await readEvents(cookie);
    expect(page.data).toHaveLength(1);
    expect(firstEvent(page).revision).toBe(2);
    expect((firstEvent(page).payload as { application_id: string }).application_id).toBe("msedge.exe");
  });

  it("returns per-item outcomes so one batch can partially succeed", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);
    await uploadBatch(token, [envelopeItem({ revision: 3 })]);

    const secondEvent = "018f62d6-4f34-7c82-9085-57c8af1d7a45";
    const upload = await uploadBatch(token, [
      envelopeItem({ event_id: secondEvent }), // fresh event, accepted
      envelopeItem({ revision: 3 }), // identical redelivery, duplicate
      envelopeItem({ revision: 2 }), // older revision, stale
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a46", event_type: "not.registered" }), // rejected
    ]);
    expect(upload.body.results.map((result: { status: string }) => result.status)).toEqual([
      "accepted",
      "duplicate",
      "stale_revision",
      "rejected",
    ]);

    const page = await readEvents(cookie);
    expect(page.data.map((event) => event.event_id).sort()).toEqual([EVENT_ID, secondEvent].sort());
  });
});

describe("activity.interval payload registry", () => {
  it("rejects payloads that miss required fields, misuse units, or smuggle raw context", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const upload = await uploadBatch(token, [
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a51", payload: { is_afk: false, duration: { value: 300_000, unit: "ms" } } }),
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a52", payload: { application_id: "msedge.exe", is_afk: "no", duration: { value: 300_000, unit: "ms" } } }),
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a53", payload: { application_id: "msedge.exe", is_afk: false, duration: { value: 300, unit: "s" } } }),
      // Raw window titles have no contract field; unknown payload keys are rejected.
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a54", payload: {
        application_id: "msedge.exe", is_afk: false, duration: { value: 300_000, unit: "ms" },
        window_title: "Bilibili - 某视频",
      } }),
    ]);
    expect(upload.body.results.every((result: { status: string }) => result.status === "rejected")).toBe(true);
    const page = await readEvents(cookie);
    expect(page.data).toHaveLength(0);
  });

  it("rejects executable paths hiding in application_id", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const upload = await uploadBatch(token, [
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a55", payload: {
        application_id: "C:\Program Files\Vendor\app.exe", is_afk: false, duration: { value: 300_000, unit: "ms" },
      } }),
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a56", payload: {
        application_id: "/usr/bin/app", is_afk: false, duration: { value: 300_000, unit: "ms" },
      } }),
    ]);
    expect(upload.body.results.every((result: { error?: { message?: string } }) =>
      (result.error?.message ?? "").includes("never a path"))).toBe(true);
  });

  it("enforces interval semantics for finalized and bounded events", async (ctx) => {
    if (!dbReady) return ctx.skip();
    const cookie = await ownerCookie();
    const token = await createDeviceToken(cookie);

    const upload = await uploadBatch(token, [
      // final without end_at
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a57", end_at: null }),
      // end_at before start_at
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a58", end_at: "2026-07-28T00:59:00.000Z" }),
      // duration does not match the interval bounds
      envelopeItem({ event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a59", payload: {
        application_id: "msedge.exe", is_afk: false, duration: { value: 5, unit: "ms" },
      } }),
    ]);
    expect(upload.body.results.every((result: { status: string }) => result.status === "rejected")).toBe(true);

    // A checkpoint carrying a consistent end_at is accepted.
    const checkpoint = await uploadBatch(token, [
      envelopeItem({
        revision: 1,
        finalization_state: "checkpoint",
        payload: { application_id: "msedge.exe", is_afk: false, duration: { value: 120_000, unit: "ms" } },
        end_at: "2026-07-28T01:02:00.000Z",
      }),
    ]);
    expect(checkpoint.body.results[0]).toMatchObject({ status: "accepted" });

    // The contract example shape (with classification) is accepted.
    const classified = await uploadBatch(token, [
      envelopeItem({
        event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a5a",
        payload: {
          application_id: "msedge.exe",
          application_label: "Microsoft Edge",
          subject_id: "service:bilibili",
          is_afk: false,
          duration: { value: 300_000, unit: "ms" },
          classification: { rule_id: "rule-bilibili-window-title", rule_version: 3, confidence: 1 },
        },
      }),
    ]);
    expect(classified.body.results[0]).toMatchObject({ status: "accepted" });
  });
});
