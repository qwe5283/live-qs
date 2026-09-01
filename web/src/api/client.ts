import { useSettingsStore } from "../stores/settings";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string | null = null,
  ) {
    super(message);
  }
}

let unauthorizedHandler: (() => void) | null = null;

export function onUnauthorized(handler: () => void): void {
  unauthorizedHandler = handler;
}

async function request<T>(url: URL, init: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, credentials: "include" });
  if (response.status === 204) {
    return undefined as T;
  }
  if (!response.ok) {
    const body = (await response.json().catch(() => ({}))) as { error?: unknown };
    if (response.status === 401) unauthorizedHandler?.();
    throw new ApiError(describeError(body, response.status), response.status, errorCodeOf(body));
  }
  return response.json() as Promise<T>;
}

export async function apiGet<T>(path: string, params?: Record<string, string>): Promise<T> {
  const settings = useSettingsStore();
  const url = new URL(path, `${settings.apiBase}/`);
  for (const [key, value] of Object.entries(params ?? {})) {
    url.searchParams.set(key, value);
  }
  return request<T>(url, {});
}

export async function apiPost<T>(path: string, body?: unknown): Promise<T> {
  const settings = useSettingsStore();
  const url = new URL(path, `${settings.apiBase}/`);
  return request<T>(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {}),
  });
}

function describeError(body: { error?: unknown }, status: number): string {
  const error = body.error;
  if (typeof error === "string") return error;
  if (typeof error === "object" && error !== null && typeof (error as { message?: unknown }).message === "string") {
    return (error as { message: string }).message;
  }
  return `HTTP ${status}`;
}

function errorCodeOf(body: { error?: unknown }): string | null {
  const error = body.error;
  if (typeof error === "object" && error !== null && typeof (error as { code?: unknown }).code === "string") {
    return (error as { code: string }).code;
  }
  return null;
}

export function isoRange(start: Date, end: Date): Record<string, string> {
  return {
    start: start.toISOString(),
    end: end.toISOString(),
  };
}
