import { Router } from "express";
import type { Env } from "../../config/env.js";
import { parseDateRange } from "../../shared/validation.js";
import { healthSummary, healthTimeline } from "../reports/service.js";

export function healthRouter(env: Env): Router {
  const router = Router();
  router.get("/health/summary", async (req, res) => { const range = parseDateRange(req); res.json(await healthSummary(env.DEFAULT_USER_ID, range.start, range.end)); });
  router.get("/health/timeline", async (req, res) => { const range = parseDateRange(req); res.json(await healthTimeline(env.DEFAULT_USER_ID, range.start, range.end)); });
  return router;
}
