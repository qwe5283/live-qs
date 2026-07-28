import { EventModel } from "./models.js";
import type { EventRow } from "../types/contracts.js";

export async function eventsInRange(userId: string, start: Date, end: Date, types?: string[]): Promise<EventRow[]> {
  const query: Record<string, unknown> = {
    user_id: userId,
    start_at: { $lt: end },
    $or: [
      { end_at: { $gte: start } },
      { end_at: null, start_at: { $gte: start } },
    ],
  };
  if (types) query.type = { $in: types };
  return EventModel.find(query).sort({ start_at: 1 }).lean<EventRow[]>().exec();
}

export const healthEventTypes = ["health.steps", "health.heart_rate", "health.sleep", "health.exercise"];
