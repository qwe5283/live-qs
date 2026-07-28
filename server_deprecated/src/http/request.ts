import { jsonObject } from "../shared/validation";

export async function readJson(req: Request): Promise<Record<string, unknown>> {
  return jsonObject(await req.json());
}

export function parseDateRange(url: URL): { start: Date; end: Date } | null {
  const startParam = url.searchParams.get("start");
  const endParam = url.searchParams.get("end");
  if (!startParam || !endParam) return null;

  const start = new Date(startParam);
  const end = new Date(endParam);
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime()) || end <= start) {
    return null;
  }
  return { start, end };
}
