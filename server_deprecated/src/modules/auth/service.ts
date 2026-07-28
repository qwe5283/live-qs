import type { DeviceIdentity, Platform } from "@ai-life/shared";

const platforms = new Set(["windows", "android", "macos"]);
const deviceTokens = new Map<string, DeviceIdentity>();

for (const [key, value] of Object.entries(process.env)) {
  if (!key.startsWith("DEVICE_TOKEN_") || !value) continue;
  const parts = value.split(":");
  if (parts.length < 4) continue;

  const token = parts[0] ?? "";
  const deviceId = parts[1] ?? "";
  const platform = parts[parts.length - 1] ?? "";
  const deviceName = parts.slice(2, -1).join(":");
  if (!token || !deviceId || !deviceName || !platforms.has(platform)) continue;

  deviceTokens.set(token, {
    userId: process.env.DEFAULT_USER_ID || "local",
    deviceId,
    deviceName,
    platform: platform as Platform,
  });
}

if (deviceTokens.size === 0) {
  console.warn("[auth] No device tokens configured. Set DEVICE_TOKEN_N env vars.");
}

function bearerToken(authHeader: string | null): string | null {
  const match = authHeader?.match(/^Bearer\s+(.+)$/i);
  return match?.[1] ?? null;
}

export function authenticateDevice(authHeader: string | null): DeviceIdentity | null {
  const token = bearerToken(authHeader);
  if (!token) return null;
  return deviceTokens.get(token) ?? null;
}

export function authenticateUser(authHeader: string | null): boolean {
  const configured = process.env.USER_TOKEN;
  if (!configured) return false;
  return bearerToken(authHeader) === configured;
}

export function requireUser(req: Request): Response | null {
  if (authenticateUser(req.headers.get("authorization"))) return null;
  return Response.json({ error: "Unauthorized" }, { status: 401 });
}
