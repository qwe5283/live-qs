import "dotenv/config";
import { z } from "zod";

const schema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]).default("development"),
  HOST: z.string().min(1).default("0.0.0.0"),
  PORT: z.coerce.number().int().min(1).max(65535).default(8787),
  MONGODB_URI: z.string().min(1).default("mongodb://127.0.0.1:27017/live_qs"),
  HASH_SECRET: z.string().min(32, "HASH_SECRET must contain at least 32 characters"),
  DEFAULT_USER_ID: z.string().min(1).default("local"),
  SESSION_TTL_HOURS: z.coerce.number().int().min(1).max(8760).default(168),
  COOKIE_SECURE: z.enum(["true", "false"]).default("false").transform((value) => value === "true"),
  CORS_ORIGINS: z.string().default("http://localhost:5173"),
});

export type Env = z.infer<typeof schema>;

export function loadEnv(source: NodeJS.ProcessEnv = process.env): Env {
  return schema.parse(source);
}
