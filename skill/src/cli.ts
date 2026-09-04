#!/usr/bin/env node
import { LiveQsClient, LiveQsApiError } from "./client.js";
import {
  describeDeviceStatus,
  describeEventPage,
  describeSyncDiagnostics,
  describeUsageDayReport,
  describeUsageWeekReport,
} from "./render.js";

/**
 * Read-only command-line surface of the LiveQs AI Skill. It fetches evidence
 * through the public query API and renders it with the honesty rules of
 * render.ts; it never computes statistics, never writes anything, and never
 * accepts or stores prompt text.
 */
interface CliOptions {
  command: string;
  flags: Map<string, string>;
}

function parseArgs(argv: string[]): CliOptions {
  const [command = "", ...rest] = argv;
  const flags = new Map<string, string>();
  for (let index = 0; index < rest.length; index += 2) {
    const flag = rest[index];
    const value = rest[index + 1];
    if (!flag?.startsWith("--") || value === undefined) {
      throw new UsageError(`Each --flag needs a value: ${flag ?? "<missing flag>"}.`);
    }
    flags.set(flag.slice(2), value);
  }
  return { command, flags };
}

export class UsageError extends Error {}

function requireFlag(flags: Map<string, string>, name: string): string {
  const value = flags.get(name);
  if (value === undefined) {
    throw new UsageError(`Missing required --${name}.`);
  }
  return value;
}

function optionalFlag(flags: Map<string, string>, name: string): string | undefined {
  return flags.get(name);
}

async function main(): Promise<number> {
  const baseUrl = process.env.LIVEQS_BASE_URL;
  const token = process.env.LIVEQS_QUERY_TOKEN;
  if (!baseUrl || !token) {
    console.error("Set LIVEQS_BASE_URL (service URL) and LIVEQS_QUERY_TOKEN (lqqry_... token, shown once at creation).");
    return 2;
  }
  const { command, flags } = parseArgs(process.argv.slice(2));
  const client = new LiveQsClient({ baseUrl, token });
  const asJson = flags.has("json");

  const print = (lines: string[]): void => console.log(lines.join("\n"));
  const eventType = optionalFlag(flags, "event-type");
  const pageSize = optionalFlag(flags, "page-size");
  const rangeParams = () => ({
    from: requireFlag(flags, "from"),
    to: requireFlag(flags, "to"),
    timezone: requireFlag(flags, "timezone"),
    ...(eventType !== undefined ? { event_type: eventType } : {}),
    ...(pageSize !== undefined ? { page_size: Number(pageSize) } : {}),
  });
  const dateParams = (): { date: string; timezone?: string } => {
    const timezone = optionalFlag(flags, "timezone");
    return { date: requireFlag(flags, "date"), ...(timezone !== undefined ? { timezone } : {}) };
  };
  const renderJson = (value: unknown): void => console.log(JSON.stringify(value, null, 2));

  switch (command) {
    case "status": {
      const statuses = await client.getStatus();
      asJson ? renderJson(statuses) : print(describeDeviceStatus(statuses));
      return 0;
    }
    case "diagnostics": {
      const diagnostics = await client.getSyncDiagnostics();
      asJson ? renderJson(diagnostics) : print(describeSyncDiagnostics(diagnostics));
      return 0;
    }
    case "usage-day": {
      const { date, timezone } = dateParams();
      const report = await client.getUsageDay(date, timezone);
      asJson ? renderJson(report) : print(describeUsageDayReport(report));
      return 0;
    }
    case "usage-week": {
      const { date, timezone } = dateParams();
      const report = await client.getUsageWeek(date, timezone);
      asJson ? renderJson(report) : print(describeUsageWeekReport(report));
      return 0;
    }
    case "events": {
      const page = await client.listEvents(rangeParams());
      asJson ? renderJson(page) : print(describeEventPage("Activity events", page));
      return 0;
    }
    case "health-events": {
      const page = await client.listHealthEvents(rangeParams());
      asJson ? renderJson(page) : print(describeEventPage("Health events", page));
      return 0;
    }
    case "payment-events": {
      const page = await client.listPaymentEvents(rangeParams());
      asJson ? renderJson(page) : print(describeEventPage("Payment events", page));
      return 0;
    }
    case "briefing": {
      // Assembles the evidence an analysis needs: current context, sync
      // health, day usage, and the raw observations of one bounded range.
      // Purely an orchestration of the read commands above.
      const statuses = await client.getStatus();
      const diagnostics = await client.getSyncDiagnostics();
      const day = await client.getUsageDay(dateParams().date, dateParams().timezone);
      const from = optionalFlag(flags, "from") ?? day.context.from;
      const to = optionalFlag(flags, "to") ?? day.context.to;
      const timezone = optionalFlag(flags, "timezone") ?? day.context.timezone;
      const events = await client.listEvents({ from, to, timezone });
      const health = await client.listHealthEvents({ from, to, timezone });
      const payment = await client.listPaymentEvents({ from, to, timezone });
      print([
        `LiveQs evidence briefing for ${day.date}:`,
        "",
        "== Current context ==",
        ...describeDeviceStatus(statuses),
        "",
        "== Sync health ==",
        ...describeSyncDiagnostics(diagnostics),
        "",
        "== Usage ==",
        ...describeUsageDayReport(day),
        "",
        "== Observations ==",
        ...describeEventPage("Activity events", events),
        "",
        ...describeEventPage("Health events", health),
        "",
        ...describeEventPage("Payment events", payment),
      ]);
      return 0;
    }
    default:
      throw new UsageError(
        `Unknown command "${command}". Commands: status, diagnostics, usage-day, usage-week, events, health-events, payment-events, briefing.`,
      );
  }
}

main().catch((error: unknown) => {
  if (error instanceof UsageError) {
    console.error(error.message);
    process.exitCode = 2;
    return;
  }
  if (error instanceof LiveQsApiError && error.code === "rate_limited" && error.retryAfterSeconds !== undefined) {
    console.error(`${error.message} Wait ${error.retryAfterSeconds} second(s) and retry.`);
    process.exitCode = 1;
    return;
  }
  if (error instanceof Error && error.message.includes("fetch failed")) {
    console.error(`Cannot reach the LiveQs service. Check LIVEQS_BASE_URL and that the service is running. (${error.message})`);
    process.exitCode = 1;
    return;
  }
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
