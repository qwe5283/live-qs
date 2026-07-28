import { createHmac } from "node:crypto";

const HASH_SECRET = process.env.HASH_SECRET || "";
if (!HASH_SECRET) {
  console.error("[server] FATAL: HASH_SECRET is required for privacy-safe hashing.");
  console.error("[server] Generate one with: openssl rand -hex 32");
  process.exit(1);
}

const focusHints = ["code", "studio", "idea", "pycharm", "webstorm", "goland", "cursor", "zed"];

export type PrivacyRuleAction = "allow_title" | "hash_title" | "hide_title" | "drop_event" | "category_only";

export interface PrivacyRuleInput {
  target_type: string;
  pattern: string;
  action: PrivacyRuleAction;
}

export interface PrivacyDecision {
  data: Record<string, unknown>;
  drop: boolean;
}

export function hmacText(value: string): string {
  return createHmac("sha256", HASH_SECRET).update(value).digest("hex");
}

export function sanitizeAppData(data: Record<string, unknown>, rules: PrivacyRuleInput[] = []): Record<string, unknown> {
  return applyAppPrivacy(data, rules).data;
}

export function applyAppPrivacy(data: Record<string, unknown>, rules: PrivacyRuleInput[] = []): PrivacyDecision {
  const sanitized: Record<string, unknown> = {};

  const appId = typeof data.app_id === "string" ? data.app_id.trim().slice(0, 160) : "";
  const appName =
    typeof data.app_name === "string" && data.app_name.trim()
      ? data.app_name.trim().slice(0, 160)
      : appId.replace(/\.exe$/i, "");
  const rawTitle = typeof data.window_title === "string" ? data.window_title.slice(0, 256) : "";
  const clientTitleHash =
    typeof data.window_title_hash === "string" && /^[a-f0-9]{64}$/i.test(data.window_title_hash)
      ? data.window_title_hash.toLowerCase()
      : "";
  const ruleAction = matchingAction(
    {
      app_id: appId,
      app_name: appName,
      window_title: rawTitle,
      event_type: "app",
    },
    rules,
  );

  if (ruleAction === "drop_event") return { data: {}, drop: true };

  const lowerName = `${appId} ${appName}`.toLowerCase();
  const inferredCategory = focusHints.some((hint) => lowerName.includes(hint)) ? "coding" : null;

  if (ruleAction === "category_only") {
    if (inferredCategory) sanitized.category = inferredCategory;
    return { data: sanitized, drop: false };
  }

  if (appId) sanitized.app_id = appId;
  if (appName) sanitized.app_name = appName;

  if (rawTitle || clientTitleHash) {
    sanitized.title_present = true;
    if (ruleAction === "allow_title") {
      if (rawTitle) sanitized.window_title = rawTitle;
    } else if (ruleAction !== "hide_title") {
      sanitized.title_hash = rawTitle ? hmacText(rawTitle.toLowerCase().trim()) : clientTitleHash;
    }
  }

  for (const key of ["is_afk", "is_audio_playing", "is_fullscreen", "battery_charging"]) {
    if (typeof data[key] === "boolean") sanitized[key] = data[key];
  }

  for (const key of ["idle_seconds", "battery_percent"]) {
    if (typeof data[key] === "number" && Number.isFinite(data[key])) {
      sanitized[key] = Math.max(0, Math.round(data[key]));
    }
  }

  if (inferredCategory) {
    sanitized.category = inferredCategory;
  }

  return { data: sanitized, drop: false };
}

export function applyEventPrivacy(type: string, data: Record<string, unknown>, rules: PrivacyRuleInput[] = []): PrivacyDecision {
  if (type === "app.foreground" || type === "app.heartbeat") {
    return applyAppPrivacy(data, rules);
  }
  return { data, drop: false };
}

export function sanitizeEventData(type: string, data: Record<string, unknown>, rules: PrivacyRuleInput[] = []): Record<string, unknown> {
  return applyEventPrivacy(type, data, rules).data;
}

function matchingAction(data: Record<string, string>, rules: PrivacyRuleInput[]): PrivacyRuleAction | null {
  const matches = rules.filter((rule) => privacyRuleMatches(data, rule)).map((rule) => rule.action);
  if (matches.includes("drop_event")) return "drop_event";
  if (matches.includes("category_only")) return "category_only";
  if (matches.includes("hide_title")) return "hide_title";
  if (matches.includes("allow_title")) return "allow_title";
  if (matches.includes("hash_title")) return "hash_title";
  return null;
}

function privacyRuleMatches(data: Record<string, string>, rule: PrivacyRuleInput): boolean {
  const pattern = rule.pattern.trim().toLowerCase();
  if (!pattern) return false;

  if (rule.target_type === "app") {
    return `${data.app_id} ${data.app_name}`.toLowerCase().includes(pattern);
  }
  if (rule.target_type === "app_id" || rule.target_type === "app_name" || rule.target_type === "window_title") {
    return (data[rule.target_type] ?? "").toLowerCase().includes(pattern);
  }
  if (rule.target_type === "event_type") {
    return (data.event_type ?? "").toLowerCase().includes(pattern);
  }
  return false;
}
