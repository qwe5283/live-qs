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
  const start = addUtcDays(endDate, -(days - 1));
  if (!start) return null;
  return Array.from({ length: days }, (_, index) => addUtcDays(start, index)).filter((date): date is string => Boolean(date));
}

export function datesBetweenInclusive(startDate: string, endDate: string, maxDays = 366): string[] | null {
  if (!isValidDateText(startDate) || !isValidDateText(endDate) || endDate < startDate) return null;
  const dates: string[] = [];
  for (let current: string | null = startDate; current && current <= endDate; current = addUtcDays(current, 1)) {
    dates.push(current);
    if (dates.length > maxDays) return null;
  }
  return dates;
}

function offsetMs(instant: Date, timezone: string): number | null {
  try {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone: timezone,
      year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
      hourCycle: "h23",
    }).formatToParts(instant);
    const value = Object.fromEntries(parts.map((part) => [part.type, part.value]));
    return Date.UTC(Number(value.year), Number(value.month) - 1, Number(value.day), Number(value.hour), Number(value.minute), Number(value.second)) - instant.getTime();
  } catch {
    return null;
  }
}

function localMidnight(date: string, timezone: string): Date | null {
  const localMs = Date.parse(`${date}T00:00:00.000Z`);
  const first = offsetMs(new Date(localMs), timezone);
  if (first === null) return null;
  const guess = new Date(localMs - first);
  const second = offsetMs(guess, timezone);
  return second === null ? null : new Date(localMs - second);
}

export function zonedDayRange(date: string, timezone: string): { start: Date; end: Date } | null {
  const next = addUtcDays(date, 1);
  if (!next) return null;
  const start = localMidnight(date, timezone);
  const end = localMidnight(next, timezone);
  return start && end && end > start ? { start, end } : null;
}

/** Validates an IANA timezone name by asking Intl to resolve it. */
export function isValidTimezone(timezone: string): boolean {
  if (!timezone) return false;
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: timezone });
    return true;
  } catch {
    return false;
  }
}

/**
 * Resolves the Monday-start week containing the local calendar day in the
 * given timezone. Day-of-week is a property of the calendar date, so the
 * weekday offset uses UTC math on the date text while the boundaries come from
 * zonedDayRange (which handles DST transitions per midnight). Returns the UTC
 * boundary instants plus the seven local calendar dates Monday through Sunday.
 */
export function zonedWeekRange(date: string, timezone: string): { start: Date; end: Date; dates: string[] } | null {
  if (!isValidDateText(date)) return null;
  const weekday = new Date(`${date}T00:00:00.000Z`).getUTCDay(); // 0 = Sunday
  const daysSinceMonday = (weekday + 6) % 7;
  const weekStart = addUtcDays(date, -daysSinceMonday);
  if (!weekStart) return null;
  const dates: string[] = [];
  for (let index = 0; index < 7; index++) {
    const day = addUtcDays(weekStart, index);
    if (!day) return null;
    dates.push(day);
  }
  const weekEnd = addUtcDays(weekStart, 7);
  if (!weekEnd) return null;
  const start = localMidnight(weekStart, timezone);
  const end = localMidnight(weekEnd, timezone);
  return start && end && end > start ? { start, end, dates } : null;
}

export function formatZonedIso(value: Date | null, timezone: string): string | null {
  if (!value) return null;
  const offset = offsetMs(value, timezone);
  if (offset === null) return null;
  const total = Math.round(Math.abs(offset) / 60_000);
  const sign = offset >= 0 ? "+" : "-";
  const suffix = `${sign}${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
  return `${new Date(value.getTime() + offset).toISOString().replace("Z", "")}${suffix}`;
}
