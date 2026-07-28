import type { Router } from "../../http/router";
import { jsonError } from "../../http/response";
import { requireUser } from "../auth/service";
import { currentContext } from "../reports/service";

export function registerContextRoutes(router: Router, defaultUserId: string): void {
  router.get("/api/v1/context/current", (req) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return Response.json(currentContext(defaultUserId));
  });

  router.get("/api/v1/devices", (req) => {
    const authError = requireUser(req);
    if (authError) return authError;
    return Response.json(currentContext(defaultUserId).devices);
  });

  router.get("/health", () => {
    return Response.json({ ok: true, server_time: new Date().toISOString() });
  });

  router.get("/api/v1/ping", () => {
    return Response.json({ ok: true, server_time: new Date().toISOString() });
  });
}
