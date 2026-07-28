import { createHmac } from "node:crypto";

export type PrivacyRuleAction = "allow_title" | "hash_title" | "hide_title" | "drop_event" | "category_only";
export interface PrivacyRuleInput { target_type: string; pattern: string; action: PrivacyRuleAction }

const focusHints = ["code", "studio", "idea", "pycharm", "webstorm", "goland", "cursor", "zed"];

export function hmacText(secret: string, value: string): string {
  return createHmac("sha256", secret).update(value).digest("hex");
}

export function applyEventPrivacy(
  secret: string,
  type: string,
  data: Record<string, unknown>,
  rules: PrivacyRuleInput[],
): { data: Record<string, unknown>; drop: boolean } {
  if (type !== "app.foreground" && type !== "app.heartbeat") return { data, drop: false };

  const appId = typeof data.app_id === "string" ? data.app_id.trim().slice(0, 160) : "";
  const appName = typeof data.app_name === "string" && data.app_name.trim()
    ? data.app_name.trim().slice(0, 160)
    : appId.replace(/\.exe$/i, "");
  const rawTitle = typeof data.window_title === "string" ? data.window_title.slice(0, 256) : "";
  const clientHash = typeof data.window_title_hash === "string" && /^[a-f0-9]{64}$/i.test(data.window_title_hash)
    ? data.window_title_hash.toLowerCase()
    : "";
  const action = matchingAction({ app_id: appId, app_name: appName, window_title: rawTitle, event_type: type }, rules);
  if (action === "drop_event") return { data: {}, drop: true };

  const category = focusHints.some((hint) => `${appId} ${appName}`.toLowerCase().includes(hint)) ? "coding" : null;
  const sanitized: Record<string, unknown> = {};
  if (action !== "category_only") {
    if (appId) sanitized.app_id = appId;
    if (appName) sanitized.app_name = appName;
    if (rawTitle || clientHash) {
      sanitized.title_present = true;
      if (action === "allow_title" && rawTitle) sanitized.window_title = rawTitle;
      else if (action !== "hide_title") sanitized.title_hash = rawTitle ? hmacText(secret, rawTitle.toLowerCase().trim()) : clientHash;
    }
    for (const key of ["is_afk", "is_audio_playing", "is_fullscreen", "battery_charging"]) {
      if (typeof data[key] === "boolean") sanitized[key] = data[key];
    }
    for (const key of ["idle_seconds", "battery_percent"]) {
      if (typeof data[key] === "number" && Number.isFinite(data[key])) sanitized[key] = Math.max(0, Math.round(data[key]));
    }
  }
  if (category) sanitized.category = category;
  return { data: sanitized, drop: false };
}

function matchingAction(data: Record<string, string>, rules: PrivacyRuleInput[]): PrivacyRuleAction | null {
  const matches = rules.filter((rule) => {
    const pattern = rule.pattern.trim().toLowerCase();
    if (!pattern) return false;
    if (rule.target_type === "app") return `${data.app_id} ${data.app_name}`.toLowerCase().includes(pattern);
    return (data[rule.target_type] ?? "").toLowerCase().includes(pattern);
  }).map((rule) => rule.action);
  return (["drop_event", "category_only", "hide_title", "allow_title", "hash_title"] as const).find((action) => matches.includes(action)) ?? null;
}
