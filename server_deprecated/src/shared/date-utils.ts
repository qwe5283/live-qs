export function isValidDateText(date: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return false;
  const value = new Date(`${date}T00:00:00.000Z`);
  return !Number.isNaN(value.getTime()) && value.toISOString().slice(0, 10) === date;
}

export function addUtcDays(date: string, days: number): string | null {
  if (!isValidDateText(date)) return null;
  const value = new Date(`${date}T00:00:00.000Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}

export function datesEndingOn(endDate: string, days = 7): string[] | null {
  if (!Number.isInteger(days) || days <= 0 || days > 31) return null;
  const startDate = addUtcDays(endDate, -(days - 1));
  if (!startDate) return null;

  const dates: string[] = [];
  for (let offset = 0; offset < days; offset++) {
    const date = addUtcDays(startDate, offset);
    if (!date) return null;
    dates.push(date);
  }
  return dates;
}

export function datesBetweenInclusive(startDate: string, endDate: string, maxDays = 366): string[] | null {
  if (!isValidDateText(startDate) || !isValidDateText(endDate)) return null;
  if (!Number.isInteger(maxDays) || maxDays <= 0) return null;

  const startMs = Date.parse(`${startDate}T00:00:00.000Z`);
  const endMs = Date.parse(`${endDate}T00:00:00.000Z`);
  if (endMs < startMs) return null;

  const dates: string[] = [];
  let current = startDate;
  while (current <= endDate) {
    dates.push(current);
    if (dates.length > maxDays) return null;
    const next = addUtcDays(current, 1);
    if (!next) return null;
    current = next;
  }
  return dates;
}

interface DateParts {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  second: number;
}

const dateTimeFormatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(timezone: string): Intl.DateTimeFormat {
  const cached = dateTimeFormatters.get(timezone);
  if (cached) return cached;

  const formatter = new Intl.DateTimeFormat("en-US", {
    timeZone: timezone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23",
  });
  dateTimeFormatters.set(timezone, formatter);
  return formatter;
}

function zonedParts(date: Date, timezone: string): DateParts | null {
  try {
    const values = new Map(formatterFor(timezone).formatToParts(date).map((part) => [part.type, part.value]));
    const year = Number(values.get("year"));
    const month = Number(values.get("month"));
    const day = Number(values.get("day"));
    const hour = Number(values.get("hour"));
    const minute = Number(values.get("minute"));
    const second = Number(values.get("second"));
    if ([year, month, day, hour, minute, second].some((value) => !Number.isInteger(value))) return null;
    return { year, month, day, hour, minute, second };
  } catch {
    return null;
  }
}

function timezoneOffsetMs(instantMs: number, timezone: string): number | null {
  const parts = zonedParts(new Date(instantMs), timezone);
  if (!parts) return null;
  const asUtcMs = Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second);
  return asUtcMs - instantMs;
}

function offsetText(offsetMs: number): string {
  const sign = offsetMs >= 0 ? "+" : "-";
  const totalMinutes = Math.round(Math.abs(offsetMs) / 60000);
  const hours = String(Math.floor(totalMinutes / 60)).padStart(2, "0");
  const minutes = String(totalMinutes % 60).padStart(2, "0");
  return `${sign}${hours}:${minutes}`;
}

export function zonedDayRange(date: string, timezone: string): { start: Date; end: Date } | null {
  if (!isValidDateText(date)) return null;
  const nextDate = addUtcDays(date, 1);
  if (!nextDate) return null;

  const start = zonedLocalDateTimeToUtc(`${date}T00:00:00.000`, timezone);
  const end = zonedLocalDateTimeToUtc(`${nextDate}T00:00:00.000`, timezone);
  return start && end && end > start ? { start, end } : null;
}

export function zonedLocalDateTimeToUtc(localDateTime: string, timezone: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{3})$/.exec(localDateTime);
  if (!match) return null;

  const [, yearText, monthText, dayText, hourText, minuteText, secondText, millisecondText] = match;
  const localMs = Date.UTC(
    Number(yearText),
    Number(monthText) - 1,
    Number(dayText),
    Number(hourText),
    Number(minuteText),
    Number(secondText),
    Number(millisecondText),
  );

  const firstOffset = timezoneOffsetMs(localMs, timezone);
  if (firstOffset === null) return null;
  const firstGuess = localMs - firstOffset;
  const secondOffset = timezoneOffsetMs(firstGuess, timezone);
  if (secondOffset === null) return null;
  return new Date(localMs - secondOffset);
}

export function formatZonedIso(iso: string | null, timezone: string): string | null {
  if (!iso) return null;
  const instantMs = new Date(iso).getTime();
  if (Number.isNaN(instantMs)) return null;

  const offset = timezoneOffsetMs(instantMs, timezone);
  if (offset === null) return null;

  const localIso = new Date(instantMs + offset).toISOString().replace("Z", "");
  return `${localIso}${offsetText(offset)}`;
}
