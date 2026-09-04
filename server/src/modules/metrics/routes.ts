import { Router } from "express";
import type { Request } from "express";
import type { Env } from "../../config/env.js";
import { sessionOrCredentialAuth } from "../../middleware/auth.js";
import { recordQueryAudit } from "../../shared/audit.js";
import { isValidTimezone } from "../../shared/date-utils.js";
import { AppError } from "../../shared/errors.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import { getReportTimezone } from "../owner/settings.js";
import { usageDayReport, usageWeekReport } from "./service.js";
import type { MetricsReadOptions } from "./service.js";

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

function requiredDate(value: unknown): string {
  if (typeof value !== "string" || !DATE_PATTERN.test(value)) {
    throw new AppError(400, "The query parameter date must be a local calendar date (YYYY-MM-DD).", "invalid_request");
  }
  return value;
}

/**
 * Resolves the report timezone: the request timezone overrides the Owner
 * report timezone per query. Both must be valid IANA timezone names, so a
 * report's boundaries never depend on a browser timezone.
 */
async function resolveReportTimezone(req: Request, env: Env): Promise<string> {
  const requested = req.query.timezone;
  if (requested !== undefined) {
    if (typeof requested !== "string" || !isValidTimezone(requested)) {
      throw new AppError(400, "The query parameter timezone must be a valid IANA timezone.", "invalid_timezone");
    }
    return requested;
  }
  return getReportTimezone(env.DEFAULT_USER_ID);
}

function metricsReadOptions(credential: CredentialAuthContext | undefined): MetricsReadOptions {
  if (!credential) return {};
  return { privacyCeiling: credential.privacy_ceiling, allowedEventTypes: credential.allowed_event_types };
}

export function metricsRouter(env: Env): Router {
  const router = Router();
  const auth = sessionOrCredentialAuth(env, { scope: "events:read" });

  router.get("/usage/day", auth, async (req, res) => {
    const date = requiredDate(req.query.date);
    const timezone = await resolveReportTimezone(req, env);
    const credential = res.locals.credential as CredentialAuthContext | undefined;
    const report = await usageDayReport(env.DEFAULT_USER_ID, date, timezone, metricsReadOptions(credential));
    if (!report) throw new AppError(400, "The query parameter date must be a real calendar date.", "invalid_request");
    await recordQueryAudit({
      userId: env.DEFAULT_USER_ID,
      credential,
      path: req.path,
      from: report.context.from,
      to: report.context.to,
      timezone,
      dataTypes: ["usage.metrics"],
      resultCount: 1,
      completeness: report.context.completeness,
    });
    res.json(report);
  });

  router.get("/usage/week", auth, async (req, res) => {
    const date = requiredDate(req.query.date);
    const timezone = await resolveReportTimezone(req, env);
    const credential = res.locals.credential as CredentialAuthContext | undefined;
    const report = await usageWeekReport(env.DEFAULT_USER_ID, date, timezone, metricsReadOptions(credential));
    if (!report) throw new AppError(400, "The query parameter date must be a real calendar date.", "invalid_request");
    await recordQueryAudit({
      userId: env.DEFAULT_USER_ID,
      credential,
      path: req.path,
      from: report.context.from,
      to: report.context.to,
      timezone,
      dataTypes: ["usage.metrics"],
      resultCount: 1,
      completeness: report.context.completeness,
    });
    res.json(report);
  });

  return router;
}
