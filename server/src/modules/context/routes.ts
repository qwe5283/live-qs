import { Router } from "express";
import type { Env } from "../../config/env.js";
import { databaseState } from "../../db/connection.js";
import { currentContext } from "../reports/service.js";

export function publicRouter(): Router {
  const router = Router();
  router.get("/health", (_req, res) => res.json({ ok: true, database: databaseState(), server_time: new Date().toISOString() }));
  router.get("/api/v1/ping", (_req, res) => res.json({ ok: true, server_time: new Date().toISOString() }));
  return router;
}

export function contextRouter(env: Env): Router {
  const router = Router();
  router.get("/context/current", async (_req, res) => res.json(await currentContext(env.DEFAULT_USER_ID)));
  router.get("/devices", async (_req, res) => res.json((await currentContext(env.DEFAULT_USER_ID)).devices));
  return router;
}
