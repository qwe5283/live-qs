import type { Router } from "../../http/router";
import { requireUser } from "../auth/service";
import {
  cleanupRetention,
  createPrivacyRule,
  deleteEvents,
  deletePrivacyRule,
  exportUserData,
  listAuditLogs,
  listPrivacyRules,
} from "./data-governance";

export function registerAdminRoutes(router: Router, defaultUserId: string): void {
  router.get("/api/v1/export", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return exportUserData(defaultUserId, url);
  });

  router.get("/api/v1/admin/audit-logs", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return listAuditLogs(defaultUserId, url);
  });

  router.get("/api/v1/admin/privacy-rules", (req) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return listPrivacyRules(defaultUserId);
  });

  router.post("/api/v1/admin/privacy-rules", async (req) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return createPrivacyRule(defaultUserId, req);
  });

  router.delete("/api/v1/admin/privacy-rules/*", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    const id = decodeURIComponent(url.pathname.slice("/api/v1/admin/privacy-rules/".length));
    return deletePrivacyRule(defaultUserId, id);
  });

  router.post("/api/v1/admin/events/delete", async (req) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return deleteEvents(defaultUserId, req);
  });

  router.post("/api/v1/admin/retention/cleanup", (req, url) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return cleanupRetention(defaultUserId, url);
  });
}
