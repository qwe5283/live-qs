import type {
  DeviceStatusList,
  EventPage,
  ErrorResponse,
  SyncDiagnosticList,
  UsageDayReport,
  UsageWeekReport,
} from "./generated/contract-models.js";

/**
 * Query parameters of the range-read operations. All of them are required or
 * optional contract parameters passed through verbatim; this wrapper never
 * computes or filters anything itself.
 */
export interface RangeParams {
  /** Inclusive UTC instant. */
  from: string;
  /** Exclusive UTC instant, later than `from`. */
  to: string;
  /** IANA timezone used to interpret reporting boundaries. */
  timezone: string;
  /** Optional exact event-type filter. */
  event_type?: string;
  /** Opaque continuation cursor returned by the preceding page. */
  cursor?: string;
  /** Page size, 1 to 200; the server defaults to 50. */
  page_size?: number;
}

export interface LiveQsClientOptions {
  /** Service base URL, such as http://localhost:8787. */
  baseUrl: string;
  /** Query Token plaintext (lqqry_...), shown once at creation. */
  token: string;
  /** Injectable transport; defaults to the global fetch (Node 20+). */
  fetch?: (input: string, init?: RequestInit) => Promise<Response>;
}

/** Error returned by the service with its stable code, plus Retry-After on 429. */
export class LiveQsApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly retryAfterSeconds?: number,
  ) {
    super(`LiveQs API ${status} ${code}: ${message}`);
    this.name = "LiveQsApiError";
  }
}

/**
 * Thin, deterministic read-only client over the public LiveQs OpenAPI query
 * surface. It wraps HTTP calls and passes responses through untouched: every
 * statistic, source selection, and completeness statement is the server's,
 * never this client's. The credential is a Query Token, which structurally
 * cannot mutate, correct, classify, administer, or execute anything.
 */
export class LiveQsClient {
  private readonly baseUrl: string;
  private readonly token: string;
  private readonly fetchImpl: (input: string, init?: RequestInit) => Promise<Response>;

  constructor(options: LiveQsClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.token = options.token;
    this.fetchImpl = options.fetch ?? fetch;
  }

  /** Current-context read (context:read): every device's latest heartbeat projection. */
  async getStatus(): Promise<DeviceStatusList> {
    return this.get("/api/v1/status");
  }

  /** Current-context read (context:read): every device's latest sync diagnostics snapshot. */
  async getSyncDiagnostics(): Promise<SyncDiagnosticList> {
    return this.get("/api/v1/diagnostics/sync");
  }

  /** Activity-domain read (events:read): latest valid activity interval revisions. */
  async listEvents(params: RangeParams): Promise<EventPage> {
    return this.get(`/api/v1/events?${queryString(params)}`);
  }

  /** Health-domain read (health:read): steps, heart rate, and sleep observations. */
  async listHealthEvents(params: RangeParams): Promise<EventPage> {
    return this.get(`/api/v1/health/events?${queryString(params)}`);
  }

  /** Payment-domain read (payment:read): structured transaction facts. */
  async listPaymentEvents(params: RangeParams): Promise<EventPage> {
    return this.get(`/api/v1/payment/events?${queryString(params)}`);
  }

  /** Day usage metrics (events:read) with device and active minutes. */
  async getUsageDay(date: string, timezone?: string): Promise<UsageDayReport> {
    return this.get(`/api/v1/metrics/usage/day?${queryString({ date, ...(timezone !== undefined ? { timezone } : {}) })}`);
  }

  /** Week usage metrics (events:read) for the Monday-start week containing the date. */
  async getUsageWeek(date: string, timezone?: string): Promise<UsageWeekReport> {
    return this.get(`/api/v1/metrics/usage/week?${queryString({ date, ...(timezone !== undefined ? { timezone } : {}) })}`);
  }

  /**
   * Follows `next_cursor` to yield every page of a range read, one request
   * per page. Pagination stays an HTTP-level concern: each yielded page is
   * the untouched server response, and rate limits still apply per request.
   */
  async *iteratePages(params: RangeParams, fetchPage: (paged: RangeParams) => Promise<EventPage>): AsyncGenerator<EventPage> {
    let cursor: string | undefined = params.cursor;
    let first = true;
    while (first || cursor !== undefined) {
      const page = await fetchPage({ ...params, ...(cursor !== undefined ? { cursor } : {}) });
      yield page;
      first = false;
      cursor = page.page.next_cursor ?? undefined;
    }
  }

  private async get<T>(path: string): Promise<T> {
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, {
      headers: { Authorization: `Bearer ${this.token}` },
    });
    if (!response.ok) {
      throw await apiError(response);
    }
    return await response.json() as T;
  }
}

function queryString(params: RangeParams | { date: string; timezone?: string }): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) search.set(key, String(value));
  }
  return search.toString();
}

async function apiError(response: Response): Promise<LiveQsApiError> {
  const retryAfter = response.headers.get("Retry-After");
  let code = `http_${response.status}`;
  let message = response.statusText || "The request failed.";
  try {
    const body = await response.json() as ErrorResponse;
    if (body.error) {
      code = body.error.code;
      message = body.error.message;
    }
  } catch {
    // Non-JSON body: fall back to the HTTP status text.
  }
  const retryAfterSeconds = retryAfter !== null && Number.isInteger(Number(retryAfter)) ? Number(retryAfter) : undefined;
  return new LiveQsApiError(response.status, code, message, response.status === 429 ? retryAfterSeconds : undefined);
}
