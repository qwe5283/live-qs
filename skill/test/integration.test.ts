import { spawn } from "node:child_process";
import { createServer, connect as tcpConnect } from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { LiveQsApiError, LiveQsClient } from "../src/client.js";
import {
  describeDeviceStatus,
  describeEventPage,
  describeSyncDiagnostics,
  describeUsageDayReport,
} from "../src/render.js";

/**
 * Integration test at the real HTTP boundary with real MongoDB (the same
 * seam the server suite uses): this file spawns one LiveQs server process on
 * a free port with a dedicated test database, initializes the Owner, creates
 * a Device Token and a fully read-scoped Query Token, seeds observations
 * through the contract batch protocol, and then exercises the Skill exactly
 * as an AI agent would.
 */

const skillRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repositoryRoot = path.resolve(skillRoot, "..");
const serverRoot = path.join(repositoryRoot, "server");
const tsxCli = path.join(serverRoot, "node_modules", "tsx", "dist", "cli.mjs");
const ownerPassword = "correct horse battery staple";

async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address !== null ? address.port : 0;
      server.close(() => (port > 0 ? resolve(port) : reject(new Error("No free port."))));
    });
  });
}

let child: ReturnType<typeof spawn> | null = null;
let baseUrl = "";
let deviceToken = "";
let queryToken = "";
let dbReady = false;

beforeAll(async () => {
  // Skip cleanly when real MongoDB is not reachable, mirroring the server suite.
  const mongoReachable = await new Promise<boolean>((resolve) => {
    const socket = tcpConnect(27017, "127.0.0.1");
    socket.once("connect", () => {
      socket.destroy();
      resolve(true);
    });
    socket.once("error", () => resolve(false));
    socket.setTimeout(2000, () => {
      socket.destroy();
      resolve(false);
    });
  });
  if (!mongoReachable) {
    console.warn("[integration.test] Real MongoDB is not reachable at 127.0.0.1:27017. Start it with `docker compose up -d` inside server/. Skipping.");
    return;
  }

  const port = await freePort();
  const database = `live_qs_test_skill_${Date.now()}_${process.pid}`;
  baseUrl = `http://127.0.0.1:${port}`;
  child = spawn(process.execPath, [tsxCli, "src/main.ts"], {
    cwd: serverRoot,
    stdio: ["ignore", "pipe", "pipe"],
    env: {
      ...process.env,
      NODE_ENV: "test",
      HOST: "127.0.0.1",
      PORT: String(port),
      MONGODB_URI: `mongodb://127.0.0.1:27017/${database}`,
      HASH_SECRET: "test-secret-with-at-least-thirty-two-characters",
      DEFAULT_USER_ID: "test-user",
      SESSION_TTL_HOURS: "168",
      COOKIE_SECURE: "false",
      CORS_ORIGINS: "http://localhost:5173",
      RATE_LIMIT_PER_MINUTE: "120",
      QUERY_TOKEN_MAX_RANGE_DAYS: "366",
    },
  });
  child.stderr?.on("data", (chunk: Buffer) => process.stderr.write(`[server] ${chunk}`));

  // Wait for the HTTP boundary to answer.
  const deadline = Date.now() + 30_000;
  for (;;) {
    try {
      const probe = await fetch(`${baseUrl}/health`);
      if (probe.ok) break;
    } catch {
      // Not listening yet.
    }
    if (Date.now() > deadline) throw new Error("The spawned LiveQs server did not become ready in time.");
    await new Promise((resolve) => setTimeout(resolve, 300));
  }

  await setupOwnerAndCredentials();
  await seedObservations();
  dbReady = true;
}, 60_000);

// Each test skips when the environment lacks real MongoDB.
beforeEach(async (ctx) => {
  if (!dbReady) return ctx.skip();
});

afterAll(async () => {
  if (child !== null) {
    child.kill("SIGTERM");
    await new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        child?.kill("SIGKILL");
        resolve();
      }, 5000);
      child?.once("exit", () => {
        clearTimeout(timer);
        resolve();
      });
    });
    child = null;
  }
});

async function setupOwnerAndCredentials(): Promise<void> {
  const setup = await fetch(`${baseUrl}/api/v1/owner/setup`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password: ownerPassword }),
  });
  if (setup.status !== 204) throw new Error(`Owner setup failed with ${setup.status}.`);
  const login = await fetch(`${baseUrl}/api/v1/owner/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password: ownerPassword }),
  });
  const cookie = (login.headers.get("set-cookie") ?? "").split(";")[0] ?? "";
  if (login.status !== 204 || !cookie) throw new Error("Owner login failed.");

  deviceToken = await createCredential(cookie, {
    kind: "device_token",
    scopes: ["events:write", "health:write", "payment:write"],
    privacy_ceiling: "sensitive",
  });
  queryToken = await createCredential(cookie, {
    kind: "query_token",
    scopes: ["events:read", "health:read", "payment:read", "context:read"],
    privacy_ceiling: "sensitive",
  });
}

async function createCredential(cookie: string, body: Record<string, unknown>): Promise<string> {
  const response = await fetch(`${baseUrl}/api/v1/credentials`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Cookie: cookie },
    body: JSON.stringify({ name: "Integration", allowed_event_types: [], expires_at: null, ...body }),
  });
  if (response.status !== 201) throw new Error(`Credential creation failed with ${response.status}.`);
  const created = await response.json() as { token: string };
  return created.token;
}

function envelope(eventId: string, eventType: string, startAt: string, endAt: string | null, payload: Record<string, unknown>, privacy: string, sourceKind: string): Record<string, unknown> {
  return {
    event_id: eventId,
    event_type: eventType,
    schema_version: 1,
    revision: 1,
    owner_id: "test-user",
    source: { kind: sourceKind, record_id: eventId },
    device: { id: "integration", platform: "windows" },
    start_at: startAt,
    ...(endAt !== null ? { end_at: endAt } : {}),
    capture_timezone: "Asia/Shanghai",
    capture_offset_minutes: 480,
    privacy_level: privacy,
    finalization_state: "final",
    provenance: { collector_version: "0.1.0", observed_at: startAt },
    invalidated: false,
    payload,
  };
}

async function seedObservations(): Promise<void> {
  const events = [
    // 90 minutes of foreground activity inside 2026-09-01 (report day, Asia/Shanghai).
    envelope("11111111-1111-4111-8111-111111111111", "activity.interval", "2026-08-31T23:00:00Z", "2026-08-31T23:30:00Z",
      { application_id: "idea64.exe", application_label: "IntelliJ IDEA", is_afk: false, duration: { value: 1_800_000, unit: "ms" } }, "normal", "windows.foreground"),
    envelope("22222222-2222-4222-8222-222222222222", "activity.interval", "2026-08-31T23:30:00Z", "2026-08-31T23:45:00Z",
      { application_id: "chrome.exe", is_afk: true, duration: { value: 900_000, unit: "ms" } }, "normal", "windows.foreground"),
    envelope("33333333-3333-4333-8333-333333333333", "health.step.sample", "2026-08-31T10:00:00Z", "2026-08-31T10:30:00Z",
      { count: { value: 4200, unit: "steps" }, data_origin: "com.miui.health" }, "sensitive", "android.healthconnect"),
    envelope("44444444-4444-4444-8444-444444444444", "payment.transaction", "2026-08-31T12:00:00Z", null,
      { amount: { value: 2500, currency: "CNY" }, direction: "expense", merchant: "FamilyMart", category: "food", pending_confirmation: false }, "sensitive", "android.wechatpay"),
  ];
  const batch = await fetch(`${baseUrl}/api/v1/events/batch`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${deviceToken}` },
    body: JSON.stringify({ events }),
  });
  if (batch.status !== 200) throw new Error(`Batch upload failed with ${batch.status}.`);
  const acknowledged = await batch.json() as { results: Array<{ status: string }> };
  if (!acknowledged.results.every((result) => result.status === "accepted")) {
    throw new Error(`Batch upload was not fully accepted: ${JSON.stringify(acknowledged)}`);
  }

  await fetch(`${baseUrl}/api/v1/heartbeats`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${deviceToken}` },
    body: JSON.stringify({ platform: "windows", device_name: "Integration Desk", captured_at: new Date().toISOString(), activity: { application_id: "Code.exe", application_label: "VS Code", is_afk: false } }),
  }).then((response) => {
    if (response.status !== 204) throw new Error("Heartbeat failed.");
  });

  await fetch(`${baseUrl}/api/v1/diagnostics/sync`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${deviceToken}` },
    body: JSON.stringify({ platform: "windows", device_name: "Integration Desk", pending_count: 0, permanent_failure_count: 0, recent_errors: [] }),
  }).then((response) => {
    if (response.status !== 204) throw new Error("Diagnostics push failed.");
  });
}

describe("the read-only Skill against a real LiveQs service", () => {
  const client = () => new LiveQsClient({ baseUrl, token: queryToken });

  it("reads the current context (status and sync diagnostics)", async () => {
    const statuses = await client().getStatus();
    expect(statuses.devices).toHaveLength(1);
    expect(statuses.devices[0]!.online).toBe(true);
    const rendered = describeDeviceStatus(statuses).join("\n");
    expect(rendered).toContain("Integration Desk");
    expect(rendered).toMatch(/not historical evidence/);

    const diagnostics = await client().getSyncDiagnostics();
    expect(diagnostics.devices).toHaveLength(1);
    expect(describeSyncDiagnostics(diagnostics).join("\n")).toMatch(/pending 0/);
  });

  it("sees observed usage on the seeded day and no_data on an empty day", async () => {
    const busy = await client().getUsageDay("2026-09-01", "Asia/Shanghai");
    expect(busy.context.data_state).toBe("observed");
    expect(busy.metrics.device_minutes).toBe(45);
    expect(busy.metrics.active_minutes).toBe(30);
    const busyText = describeUsageDayReport(busy).join("\n");
    expect(busyText).toContain("Device minutes: 45");

    const empty = await client().getUsageDay("2026-08-20", "Asia/Shanghai");
    expect(empty.context.data_state).toBe("no_data");
    const emptyText = describeUsageDayReport(empty).join("\n");
    expect(emptyText).toMatch(/Device minutes: unavailable/);
    expect(emptyText).toMatch(/missing data, not zero/);
    expect(emptyText).not.toMatch(/Device minutes: 0/);
  });

  it("reads domain pages and preserves the honest rendering of an empty range", async () => {
    const events = await client().listEvents({ from: "2026-08-31T00:00:00Z", to: "2026-09-01T00:00:00Z", timezone: "Asia/Shanghai" });
    expect(events.data).toHaveLength(4);
    expect(events.context.data_state).toBe("observed");
    expect(describeEventPage("Activity events", events).join("\n")).toMatch(/4 event\(s\)/);

    const missing = await client().listEvents({ from: "2026-08-20T00:00:00Z", to: "2026-08-21T00:00:00Z", timezone: "Asia/Shanghai" });
    expect(missing.data).toHaveLength(0);
    expect(missing.context.data_state).toBe("no_data");
    expect(describeEventPage("Activity events", missing).join("\n")).toMatch(/MISSING, never as zero/);
  });

  it("is rejected when the requested range exceeds the credential bound", async () => {
    const error = await client().listEvents({ from: "2024-01-01T00:00:00Z", to: "2026-09-01T00:00:00Z", timezone: "UTC" })
      .catch((caught: unknown) => caught) as LiveQsApiError;
    expect(error).toBeInstanceOf(LiveQsApiError);
    expect(error.status).toBe(400);
    expect(error.code).toBe("range_too_large");
  });

  it("cannot reach any mutation or administration surface", async () => {
    const auth = { Authorization: `Bearer ${queryToken}`, "Content-Type": "application/json" };
    expect((await fetch(`${baseUrl}/api/v1/events/batch`, { method: "POST", headers: auth, body: JSON.stringify({ events: [] }) })).status).toBe(403);
    expect((await fetch(`${baseUrl}/api/v1/events/44444444-4444-4444-8444-444444444444/corrections`, { method: "POST", headers: auth, body: JSON.stringify({ fields: [], reason: null, invalidate: false }) })).status).toBe(401);
    expect((await fetch(`${baseUrl}/api/v1/credentials`, { method: "POST", headers: auth, body: JSON.stringify({ kind: "query_token", name: "x", scopes: ["events:read"] }) })).status).toBe(401);
    expect((await fetch(`${baseUrl}/api/v1/owner/settings`, { method: "POST", headers: auth, body: JSON.stringify({ report_timezone: "UTC" }) })).status).toBe(401);
    expect((await fetch(`${baseUrl}/api/v1/source-policy`, { method: "PUT", headers: auth, body: JSON.stringify({ entries: [] }) })).status).toBe(401);
    expect((await fetch(`${baseUrl}/api/v1/admin/events/delete`, { method: "POST", headers: auth, body: JSON.stringify({}) })).status).toBe(401);
  });
});
