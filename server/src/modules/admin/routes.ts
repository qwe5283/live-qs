import { Router } from "express";
import type { Env } from "../../config/env.js";
import {
  cleanupRetention, createPrivacyRule, deleteEvents, deletePrivacyRule, exportUserData,
  listAuditLogs, listPrivacyRules,
} from "./service.js";

export function adminRouter(env: Env): Router {
  const router = Router();
  const userId = env.DEFAULT_USER_ID;
  router.get("/export", async (req, res) => res.json(await exportUserData(userId, req.query)));
  router.get("/admin/audit-logs", async (req, res) => res.json(await listAuditLogs(userId, req.query)));
  router.get("/admin/privacy-rules", async (_req, res) => res.json(await listPrivacyRules(userId)));
  router.post("/admin/privacy-rules", async (req, res) => res.json(await createPrivacyRule(userId, req.body)));
  router.delete("/admin/privacy-rules/:id", async (req, res) => res.json(await deletePrivacyRule(userId, req.params.id ?? "")));
  router.post("/admin/events/delete", async (req, res) => res.json(await deleteEvents(userId, req.body)));
  router.post("/admin/retention/cleanup", async (req, res) => res.json(await cleanupRetention(userId, req.query)));
  return router;
}
