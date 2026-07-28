import { useSettingsStore } from "../stores/settings";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

export async function apiGet<T>(path: string, params?: Record<string, string>): Promise<T> {
  const settings = useSettingsStore();
  const url = new URL(path, `${settings.apiBase}/`);
  for (const [key, value] of Object.entries(params ?? {})) {
    url.searchParams.set(key, value);
  }

  const response = await fetch(url, {
    headers: settings.userToken ? { Authorization: `Bearer ${settings.userToken}` } : {},
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new ApiError(typeof body.error === "string" ? body.error : `HTTP ${response.status}`, response.status);
  }
  return response.json() as Promise<T>;
}

export function isoRange(start: Date, end: Date): Record<string, string> {
  return {
    start: start.toISOString(),
    end: end.toISOString(),
  };
}
