import type { Router } from "../../http/router";
import { parseDateRange } from "../../http/request";
import { jsonError } from "../../http/response";
import { requireUser } from "../auth/service";
import { healthRangeSummary, healthTimeline } from "./service";

export function registerHealthRoutes(router: Router): void {
  router.get("/api/v1/health/summary", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range
      ? Response.json(healthRangeSummary(range.start, range.end))
      : jsonError("start and end ISO timestamps required", 400);
  });

  router.get("/api/v1/health/timeline", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(healthTimeline(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });
}
