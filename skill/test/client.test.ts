import { describe, expect, it } from "vitest";
import { LiveQsApiError, LiveQsClient } from "../src/client.js";
import type { RangeParams } from "../src/client.js";
import type { EventPage } from "../src/generated/contract-models.js";

/** Offline client tests: a stub transport verifies request shapes and error mapping. */
type Transport = (input: string, init?: RequestInit) => Promise<Response>;

function json(response: { status?: number; body: unknown; headers?: Record<string, string> }): () => Promise<Response> {
  return async () => {
    const headers = new Headers(response.headers ?? {});
    headers.set("Content-Type", "application/json");
    return new Response(JSON.stringify(response.body), { status: response.status ?? 200, headers });
  };
}

function page(eventIds: string[], nextCursor: string | null): EventPage {
  return {
    data: eventIds.map((id) => ({
      event_id: id,
      event_type: "activity.interval",
      schema_version: 1,
      owner_id: "test-user",
      source: { kind: "windows.foreground", record_id: "r" },
      device: { id: "cred", platform: "windows" },
      start_at: "2026-09-01T01:00:00.000Z",
      end_at: "2026-09-01T01:01:00.000Z",
      capture_timezone: "UTC",
      capture_offset_minutes: 0,
      privacy_level: "normal",
      revision: 1,
      finalization_state: "final",
      provenance: { collector_version: "0.1.0", observed_at: "2026-09-01T01:01:00.000Z" },
      invalidated: false,
      payload: { application_id: "Code.exe", is_afk: false, duration: { value: 60_000, unit: "ms" } },
    })),
    page: { page_size: eventIds.length, next_cursor: nextCursor },
    context: {
      from: "2026-09-01T00:00:00.000Z",
      to: "2026-09-02T00:00:00.000Z",
      timezone: "UTC",
      provenance: ["windows.foreground"],
      completeness: "complete",
      data_state: eventIds.length > 0 ? "observed" : "no_data",
    },
  };
}

describe("LiveQsClient over a stub transport", () => {
  const range: RangeParams = {
    from: "2026-09-01T00:00:00.00Z",
    to: "2026-09-02T00:00:00Z",
    timezone: "Asia/Shanghai",
    event_type: "activity.interval",
    page_size: 10,
  };

  it("sends the bearer token and the query parameters verbatim", async () => {
    const seen: { url: string; auth: string } = { url: "", auth: "" };
    const transport: Transport = async (input, init) => {
      seen.url = input;
      seen.auth = new Headers(init?.headers).get("Authorization") ?? "";
      return json({ body: page(["a"], null) })();
    };
    const client = new LiveQsClient({ baseUrl: "http://host:8787/", token: "lqqry_secret", fetch: transport });
    await client.listEvents(range);

    expect(seen.url).toBe("http://host:8787/api/v1/events?from=2026-09-01T00%3A00%3A00.00Z&to=2026-09-02T00%3A00%3A00Z&timezone=Asia%2FShanghai&event_type=activity.interval&page_size=10");
    expect(seen.auth).toBe("Bearer lqqry_secret");
  });

  it("maps an error body to a typed error with the stable code", async () => {
    const client = new LiveQsClient({
      baseUrl: "http://host:8787",
      token: "lqqry_x",
      fetch: json({ status: 403, body: { error: { code: "insufficient_scope", message: "nope" }, request_id: "r" } }),
    });
    await expect(client.getStatus()).rejects.toMatchObject({
      name: "LiveQsApiError",
      status: 403,
      code: "insufficient_scope",
    });
  });

  it("exposes Retry-After on 429 so the caller can wait", async () => {
    const client = new LiveQsClient({
      baseUrl: "http://host:8787",
      token: "lqqry_x",
      fetch: json({ status: 429, body: { error: { code: "rate_limited", message: "slow down" }, request_id: "r" }, headers: { "Retry-After": "23" } }),
    });
    const error = await client.listEvents(range).catch((caught: unknown) => caught) as LiveQsApiError;
    expect(error).toBeInstanceOf(LiveQsApiError);
    expect(error.retryAfterSeconds).toBe(23);
  });

  it("follows next_cursor across pages until the server reports the last page", async () => {
    const pages: EventPage[] = [page(["a", "b"], "cursor-1"), page(["c"], "cursor-2"), page([], null)];
    const requests: string[] = [];
    const transport: Transport = async (input) => {
      requests.push(input);
      const response = json({ body: pages[requests.length - 1]! });
      return response();
    };
    const client = new LiveQsClient({ baseUrl: "http://host:8787", token: "lqqry_x", fetch: transport });

    const collected: string[][] = [];
    for await (const pageResponse of client.iteratePages(range, (paged) => client.listEvents(paged))) {
      collected.push(pageResponse.data.map((event) => event.event_id));
    }

    expect(collected).toEqual([["a", "b"], ["c"], []]);
    expect(requests).toHaveLength(3);
    expect(requests[1]).toContain("cursor=cursor-1");
    expect(requests[2]).toContain("cursor=cursor-2");
  });
});
