import cors from "cors";
import express from "express";
import helmet from "helmet";
import type { Env } from "./config/env.js";
import { userAuth } from "./middleware/auth.js";
import { errorHandler, notFound } from "./middleware/errors.js";
import { adminRouter } from "./modules/admin/routes.js";
import { contextRouter, publicRouter } from "./modules/context/routes.js";
import { healthRouter } from "./modules/health/routes.js";
import { ingestRouter } from "./modules/ingest/routes.js";
import { reportsRouter } from "./modules/reports/routes.js";
import { usageRouter } from "./modules/usage/routes.js";

export function createApp(env: Env) {
  const app = express();
  app.disable("x-powered-by");
  app.use(helmet());
  app.use(cors({ origin: true, allowedHeaders: ["Content-Type", "Authorization"], methods: ["GET", "POST", "DELETE", "OPTIONS"] }));
  app.use(express.json({ limit: "1mb" }));

  app.use(publicRouter());
  app.use("/api/v1/ingest", ingestRouter(env));
  app.use("/api/v1", userAuth(env));
  app.use("/api/v1", contextRouter(env));
  app.use("/api/v1", usageRouter(env));
  app.use("/api/v1", healthRouter(env));
  app.use("/api/v1", reportsRouter(env));
  app.use("/api/v1", adminRouter(env));

  app.use(notFound);
  app.use(errorHandler);
  return app;
}
