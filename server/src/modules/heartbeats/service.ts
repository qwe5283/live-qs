import type { DeviceStatus, DeviceStatusList, HeartbeatActivity, Platform } from "../../generated/contract-models.js";
import { DeviceStatusModel } from "../../db/models.js";
import type { CredentialAuthContext } from "../credentials/service.js";
import type { Clock } from "../../shared/clock.js";
import { AppError } from "../../shared/errors.js";
import { isDuplicateKeyError } from "../../shared/mongo.js";

/** A device shows offline once its latest heartbeat capture time is at least this old. */
export const OFFLINE_AFTER_SECONDS = 60;

/**
 * Capture times more than this far in the future are rejected: the monotonic
 * guard would otherwise freeze a poisoned projection and reject every later
 * real heartbeat until the TTL backstop purges it.
 */
const MAX_FUTURE_SKEW_MS = 300_000;

/** One accepted heartbeat projection as stored. */
export interface HeartbeatInput {
  platform: Platform;
  deviceName: string | null;
  capturedAt: Date;
  activity: HeartbeatActivity;
}

interface DeviceStatusRow {
  device_key: string;
  user_id: string;
  platform: string;
  device_name: string | null;
  captured_at: Date;
  activity: unknown;
}

/**
 * Records one heartbeat as the device current-state projection. A heartbeat
 * whose capture time is older than the stored projection is acknowledged
 * without regressing it: duplicates and delayed deliveries from an offline
 * queue can never roll a device back to an earlier state.
 */
export async function recordHeartbeat(credential: CredentialAuthContext, input: HeartbeatInput, clock: Clock): Promise<void> {
  if (input.capturedAt.getTime() > clock.now().getTime() + MAX_FUTURE_SKEW_MS) {
    throw new AppError(400, "captured_at must not be more than five minutes in the future.", "invalid_request");
  }
  try {
    await DeviceStatusModel.create({
      device_key: credential.id,
      user_id: credential.userId,
      platform: input.platform,
      device_name: input.deviceName,
      captured_at: input.capturedAt,
      activity: input.activity,
    });
    return;
  } catch (error) {
    if (!isDuplicateKeyError(error)) throw error;
  }
  // The device already has a projection; replace it only while the stored
  // capture time is not newer than the incoming one.
  await DeviceStatusModel.updateOne(
    { device_key: credential.id, captured_at: { $lte: input.capturedAt } },
    {
      $set: {
        platform: input.platform,
        device_name: input.deviceName,
        captured_at: input.capturedAt,
        activity: input.activity,
      },
    },
  );
}

/**
 * Lists every device projection of the Owner, each device independent. Ages
 * and online flags derive from the latest capture time, never from a stored
 * online flag: devices whose heartbeats stop show offline within sixty
 * seconds and stay listed until the TTL backstop purges the projection.
 */
export async function listDeviceStatuses(userId: string, clock: Clock): Promise<DeviceStatusList> {
  const now = clock.now();
  const rows = await DeviceStatusModel.find({ user_id: userId }).sort({ captured_at: 1 }).lean<DeviceStatusRow[]>();
  return {
    server_time: now.toISOString(),
    devices: rows.map((row) => toDeviceStatus(row, now)),
  };
}

function toDeviceStatus(row: DeviceStatusRow, now: Date): DeviceStatus {
  const ageSeconds = Math.max(0, Math.floor((now.getTime() - row.captured_at.getTime()) / 1000));
  return {
    device_id: row.device_key,
    device_name: row.device_name ?? null,
    platform: row.platform as DeviceStatus["platform"],
    online: ageSeconds < OFFLINE_AFTER_SECONDS,
    age_seconds: ageSeconds,
    captured_at: row.captured_at.toISOString(),
    activity: row.activity as HeartbeatActivity,
  };
}
