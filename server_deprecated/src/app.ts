import { loadEnv } from "./config/env";
import { cors, jsonError, notFound } from "./http/response";
import { Router } from "./http/router";
import { registerAdminRoutes } from "./modules/admin/routes";
import { registerContextRoutes } from "./modules/context/routes";
import { registerHealthRoutes } from "./modules/health/routes";
import { registerIngestRoutes } from "./modules/ingest/routes";
import { registerReportRoutes } from "./modules/reports/routes";
import { registerUsageRoutes } from "./modules/usage/routes";

export function createApp() {
  const env = loadEnv();
  const router = new Router();

  registerContextRoutes(router, env.defaultUserId);
  registerIngestRoutes(router);
  registerUsageRoutes(router);
  registerHealthRoutes(router);
  registerReportRoutes(router, env.defaultUserId);
  registerAdminRoutes(router, env.defaultUserId);

  return {
    port: env.port,
    async fetch(req: Request): Promise<Response> {
      const url = new URL(req.url);
      if (req.method === "OPTIONS") return cors(new Response(null, { status: 204 }));

      const handler = router.match(req.method, url.pathname);
      if (!handler) return cors(notFound());

      try {
        return cors(await handler(req, url));
      } catch (error) {
        const message = error instanceof Error ? error.message : "Internal error";
        const status = message.includes("required") || message.includes("too many") || message.includes("too large") ? 400 : 500;
        if (status === 500) console.error("[server] Unhandled error:", error);
        return cors(jsonError(message, status));
      }
    },
  };
}
