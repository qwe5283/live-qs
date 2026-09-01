import { Router } from "express";
import type { Env } from "../../config/env.js";
import { AppError } from "../../shared/errors.js";
import { parseDateRange } from "../../shared/validation.js";
import { recordAuditLog } from "../../shared/audit.js";
import {
  backfillDayRollups, daySummary, focusBlocksReport, lifestyleAnomalies, persistDayRollup,
  sleepReport, spendingSummary, weekSummary,
} from "./service.js";

export function reportsRouter(env: Env): Router {
  const router = Router();
  const dateQuery = (value: unknown) => typeof value === "string" ? value : new Date().toISOString().slice(0, 10);
  const timezoneQuery = (value: unknown) => typeof value === "string" ? value : "UTC";

  router.get("/reports/day", async (req, res) => {
    const result = await daySummary(env.DEFAULT_USER_ID, dateQuery(req.query.date), timezoneQuery(req.query.tz));
    if (!result) throw new AppError(400, "Invalid date");
    res.json(result);
  });
  router.get("/reports/week", async (req, res) => {
    const result = await weekSummary(env.DEFAULT_USER_ID, dateQuery(req.query.end_date), timezoneQuery(req.query.tz));
    if (!result) throw new AppError(400, "Invalid end_date");
    res.json(result);
  });
  router.get("/reports/anomalies", async (req, res) => {
    const result = await lifestyleAnomalies(env.DEFAULT_USER_ID, dateQuery(req.query.date), timezoneQuery(req.query.tz));
    if (!result) throw new AppError(400, "Invalid date");
    res.json(result);
  });
  router.get("/reports/sleep", async (req, res) => {
    const result = await sleepReport(env.DEFAULT_USER_ID, dateQuery(req.query.date), timezoneQuery(req.query.tz));
    if (!result) throw new AppError(400, "Invalid date");
    res.json(result);
  });
  router.get("/reports/focus-blocks", async (req, res) => {
    const result = await focusBlocksReport(env.DEFAULT_USER_ID, dateQuery(req.query.date), timezoneQuery(req.query.tz));
    if (!result) throw new AppError(400, "Invalid date");
    res.json(result);
  });
  router.get("/spending/summary", async (req, res) => {
    const { start, end } = parseDateRange(req);
    res.json(await spendingSummary(env.DEFAULT_USER_ID, start, end));
  });
  router.post("/admin/rollups/day", async (req, res) => {
    const date = dateQuery(req.query.date);
    const timezone = timezoneQuery(req.query.tz);
    const result = await persistDayRollup(env.DEFAULT_USER_ID, date, timezone);
    if (!result) throw new AppError(400, "Invalid date");
    await recordAuditLog({ userId: env.DEFAULT_USER_ID, action: "rollups.day", details: { date, timezone, created: result.created } });
    res.json({ ok: true, ...result });
  });
  router.post("/admin/rollups/backfill", async (req, res) => {
    const startDate = typeof req.query.start_date === "string" ? req.query.start_date : "";
    const endDate = typeof req.query.end_date === "string" ? req.query.end_date : "";
    const timezone = timezoneQuery(req.query.tz);
    const result = await backfillDayRollups(env.DEFAULT_USER_ID, startDate, endDate, timezone);
    if (!result) throw new AppError(400, "Valid start_date and end_date required; max range is 366 days");
    await recordAuditLog({ userId: env.DEFAULT_USER_ID, action: "rollups.backfill",
      details: { start_date: startDate, end_date: endDate, timezone, upserted: result.upserted } });
    res.json({ ok: true, ...result });
  });
  return router;
}
