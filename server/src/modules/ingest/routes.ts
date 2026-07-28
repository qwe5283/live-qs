import { Router } from "express";
import type { Env } from "../../config/env.js";
import { deviceAuth } from "../../middleware/auth.js";
import type { BatchEventPayload, DeviceIdentity, HeartbeatPayload } from "../../types/contracts.js";
import { ingestEvents, ingestHeartbeat } from "./service.js";

export function ingestRouter(env: Env): Router {
  const router = Router();
  router.use(deviceAuth(env));

  router.post("/heartbeat", async (req, res) => {
    const result = await ingestHeartbeat(env, res.locals.device as DeviceIdentity, req.body as HeartbeatPayload);
    res.json({ ok: true, ...result });
  });
  router.post("/events", async (req, res) => {
    const result = await ingestEvents(env, res.locals.device as DeviceIdentity, req.body as BatchEventPayload);
    res.json({ ok: true, ...result });
  });
  return router;
}
