import { recordAuditLog } from "../admin/audit";
import type { Router } from "../../http/router";
import { parseDateRange } from "../../http/request";
import { jsonError } from "../../http/response";
import { requireUser } from "../auth/service";
import { backfillDayRollups, persistDayRollup } from "./rollups";
import {
  daySummary,
  focusBlocksReport,
  lifestyleAnomalies,
  sleepReport,
  spendingSummary,
  weekSummary,
} from "./service";

export function registerReportRoutes(router: Router, defaultUserId: string): void {
  router.get("/api/v1/reports/day", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const date = url.searchParams.get("date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const summary = daySummary(date, timezone);
    return summary ? Response.json(summary) : jsonError("Invalid date", 400);
  });

  router.get("/api/v1/reports/week", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const endDate = url.searchParams.get("end_date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const summary = weekSummary(endDate, timezone);
    return summary ? Response.json(summary) : jsonError("Invalid end_date", 400);
  });

  router.get("/api/v1/reports/anomalies", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const date = url.searchParams.get("date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const report = lifestyleAnomalies(date, timezone);
    return report ? Response.json(report) : jsonError("Invalid date", 400);
  });

  router.get("/api/v1/reports/sleep", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const date = url.searchParams.get("date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const report = sleepReport(date, timezone);
    return report ? Response.json(report) : jsonError("Invalid date", 400);
  });

  router.get("/api/v1/reports/focus-blocks", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const date = url.searchParams.get("date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const report = focusBlocksReport(date, timezone);
    return report ? Response.json(report) : jsonError("Invalid date", 400);
  });

  router.get("/api/v1/spending/summary", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(spendingSummary(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });

  router.post("/api/v1/admin/rollups/day", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const date = url.searchParams.get("date") || new Date().toISOString().slice(0, 10);
    const timezone = url.searchParams.get("tz") || "UTC";
    const result = persistDayRollup(defaultUserId, date, timezone);
    if (result) {
      recordAuditLog({
        userId: defaultUserId,
        action: "rollups.day",
        details: { date, timezone, created: result.created },
      });
    }
    return result ? Response.json({ ok: true, ...result }) : jsonError("Invalid date", 400);
  });

  router.post("/api/v1/admin/rollups/backfill", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const startDate = url.searchParams.get("start_date");
    const endDate = url.searchParams.get("end_date");
    const timezone = url.searchParams.get("tz") || "UTC";
    const result = startDate && endDate ? backfillDayRollups(defaultUserId, startDate, endDate, timezone) : null;
    if (result) {
      recordAuditLog({
        userId: defaultUserId,
        action: "rollups.backfill",
        details: {
          start_date: result.start_date,
          end_date: result.end_date,
          timezone: result.timezone,
          upserted: result.upserted,
        },
      });
    }
    return result
      ? Response.json({ ok: true, ...result })
      : jsonError("Valid start_date and end_date required; max range is 366 days", 400);
  });
}
