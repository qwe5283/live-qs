import type { Router } from "../../http/router";
import { readJson } from "../../http/request";
import { jsonError } from "../../http/response";
import { authenticateDevice } from "../auth/service";
import { ingestEvents, ingestHeartbeat } from "./service";

export function registerIngestRoutes(router: Router): void {
  router.post("/api/v1/ingest/heartbeat", async (req) => {
    const device = authenticateDevice(req.headers.get("authorization"));
    if (!device) return jsonError("Unauthorized", 401);
    return Response.json({ ok: true, ...ingestHeartbeat(device, (await readJson(req)) as never) });
  });

  router.post("/api/v1/ingest/events", async (req) => {
    const device = authenticateDevice(req.headers.get("authorization"));
    if (!device) return jsonError("Unauthorized", 401);
    return Response.json({ ok: true, ...ingestEvents(device, (await readJson(req)) as never) });
  });
}
