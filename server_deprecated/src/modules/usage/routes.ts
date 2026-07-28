import type { Router } from "../../http/router";
import { parseDateRange } from "../../http/request";
import { jsonError } from "../../http/response";
import { requireUser } from "../auth/service";
import { usageApps, usageSummary, usageTimeline } from "./service";

export function registerUsageRoutes(router: Router): void {
  router.get("/api/v1/usage/summary", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(usageSummary(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });

  router.get("/api/v1/usage/apps", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(usageApps(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });

  router.get("/api/v1/usage/timeline", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(usageTimeline(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });

  router.get("/api/v1/screen/summary", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const range = parseDateRange(url);
    return range ? Response.json(usageSummary(range.start, range.end)) : jsonError("start and end ISO timestamps required", 400);
  });
}
