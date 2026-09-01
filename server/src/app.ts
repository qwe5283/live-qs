import cors from "cors";
import express from "express";
import helmet from "helmet";
import type { Env } from "./config/env.js";
import { ownerAuth } from "./middleware/auth.js";
import { errorHandler, notFound } from "./middleware/errors.js";
import { assignRequestId } from "./middleware/request-id.js";
import { adminRouter } from "./modules/admin/routes.js";
import { contextRouter, publicRouter } from "./modules/context/routes.js";
import { credentialsRouter } from "./modules/credentials/routes.js";
import { eventsRouter } from "./modules/events/routes.js";
import { healthRouter } from "./modules/health/routes.js";
import { metricsRouter } from "./modules/metrics/routes.js";
import { ownerRouter } from "./modules/owner/routes.js";
import { reportsRouter } from "./modules/reports/routes.js";
import { usageRouter } from "./modules/usage/routes.js";

export function createApp(env: Env) {
  const app = express();
  app.disable("x-powered-by");
  app.use(assignRequestId());
  app.use(helmet());
  app.use(cors({
    origin: corsOriginAllowlist(env),
    credentials: true,
    allowedHeaders: ["Content-Type", "Authorization"],
    methods: ["GET", "POST", "DELETE", "OPTIONS"],
  }));
  app.use(express.json({ limit: "1mb" }));

  app.use(publicRouter());
  app.use("/api/v1/owner", ownerRouter(env));
  app.use("/api/v1/credentials", credentialsRouter(env));
  app.use("/api/v1/events", eventsRouter(env));
  app.use("/api/v1/metrics", metricsRouter(env));
  app.use("/api/v1", ownerAuth());
  app.use("/api/v1", contextRouter(env));
  app.use("/api/v1", usageRouter(env));
  app.use("/api/v1", healthRouter(env));
  app.use("/api/v1", reportsRouter(env));
  app.use("/api/v1", adminRouter(env));

  app.use(notFound);
  app.use(errorHandler);
  return app;
}

function corsOriginAllowlist(env: Env): string[] | boolean {
  const origins = env.CORS_ORIGINS.split(",").map((origin) => origin.trim()).filter(Boolean);
  return origins.length > 0 ? origins : false;
}
