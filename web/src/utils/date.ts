export function startOfToday(): Date {
  const date = new Date();
  date.setHours(0, 0, 0, 0);
  return date;
}

export function startOfDaysAgo(days: number): Date {
  const date = startOfToday();
  date.setDate(date.getDate() - days);
  return date;
}

export function endOfToday(): Date {
  const date = new Date();
  date.setHours(23, 59, 59, 999);
  return date;
}

export function formatMinutes(minutes: number): string {
  if (minutes < 60) return `${Math.round(minutes)} min`;
  const h = Math.floor(minutes / 60);
  const m = Math.round(minutes % 60);
  return m > 0 ? `${h}h ${m}m` : `${h}h`;
}

export function formatDateInput(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function dateFromInput(value: string, end = false): Date {
  const date = new Date(`${value}T00:00:00`);
  if (end) date.setHours(23, 59, 59, 999);
  return date;
}

/** Today's calendar date (YYYY-MM-DD) in the given IANA timezone, independent of the browser timezone. */
export function todayInTimezone(timezone: string): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: timezone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(new Date());
}

/** Renders a UTC instant as plain text so the timeline never depends on the browser timezone. */
export function formatUtcText(instant: string): string {
  return `${instant.slice(0, 19).replace("T", " ")} UTC`;
}

/** Renders the capture timezone context reported by the collector, e.g. "Asia/Shanghai UTC+08:00". */
export function formatCaptureZone(captureTimezone: string, captureOffsetMinutes: number): string {
  const sign = captureOffsetMinutes < 0 ? "-" : "+";
  const total = Math.abs(captureOffsetMinutes);
  const hours = String(Math.floor(total / 60)).padStart(2, "0");
  const minutes = String(total % 60).padStart(2, "0");
  return `${captureTimezone} UTC${sign}${hours}:${minutes}`;
}

/** IANA timezones offered for the report-timezone setting; empty when the runtime cannot enumerate them. */
export function ianaTimezoneOptions(): string[] {
  const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const zones = typeof supported === "function" ? supported("timeZone") : [];
  return zones.includes("UTC") ? zones : ["UTC", ...zones];
}
