import { Router } from "express";
import type { Env } from "../../config/env.js";
import { parseDateRange } from "../../shared/validation.js";
import { usageApps, usageSummary, usageTimeline } from "../reports/service.js";

export function usageRouter(env: Env): Router {
  const router = Router();
  router.get("/usage/summary", async (req, res) => { const range = parseDateRange(req); res.json(await usageSummary(env.DEFAULT_USER_ID, range.start, range.end)); });
  router.get("/screen/summary", async (req, res) => { const range = parseDateRange(req); res.json(await usageSummary(env.DEFAULT_USER_ID, range.start, range.end)); });
  router.get("/usage/apps", async (req, res) => { const range = parseDateRange(req); res.json(await usageApps(env.DEFAULT_USER_ID, range.start, range.end)); });
  router.get("/usage/timeline", async (req, res) => { const range = parseDateRange(req); res.json(await usageTimeline(env.DEFAULT_USER_ID, range.start, range.end)); });
  return router;
}
