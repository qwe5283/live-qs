import { OwnerSettingsModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";
import { isValidTimezone } from "../../shared/date-utils.js";

/**
 * The report timezone defines default day and week boundaries for every
 * report. It defaults to UTC so boundaries stay deterministic until the Owner
 * configures a timezone; browser timezones never influence a report.
 */
export const DEFAULT_REPORT_TIMEZONE = "UTC";

interface OwnerSettingsRecord {
  user_id: string;
  report_timezone: string;
  updated_at: Date;
}

export async function getReportTimezone(userId: string): Promise<string> {
  const record = await OwnerSettingsModel.findOne({ user_id: userId }).lean<OwnerSettingsRecord | null>();
  return record?.report_timezone ?? DEFAULT_REPORT_TIMEZONE;
}

/**
 * Persists the report timezone after validating it as an IANA timezone.
 * Stored events keep their UTC instants and capture timezone context; only
 * future report boundaries resolve in the new timezone.
 */
export async function setReportTimezone(userId: string, timezone: string): Promise<string> {
  if (!isValidTimezone(timezone)) {
    throw new AppError(400, "report_timezone must be a valid IANA timezone.", "invalid_timezone");
  }
  await OwnerSettingsModel.updateOne(
    { user_id: userId },
    { $set: { report_timezone: timezone, updated_at: new Date() } },
    { upsert: true },
  );
  return timezone;
}
