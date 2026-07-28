import "dotenv/config";
import { z } from "zod";

const schema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]).default("development"),
  PORT: z.coerce.number().int().min(1).max(65535).default(8787),
  MONGODB_URI: z.string().min(1).default("mongodb://127.0.0.1:27017/live_qs"),
  HASH_SECRET: z.string().min(32, "HASH_SECRET must contain at least 32 characters"),
  DEFAULT_USER_ID: z.string().min(1).default("local"),
  USER_TOKEN: z.string().min(1),
});

export type Env = z.infer<typeof schema> & { deviceTokens: Record<string, string> };

export function loadEnv(source: NodeJS.ProcessEnv = process.env): Env {
  const parsed = schema.parse(source);
  const deviceTokens: Record<string, string> = {};
  for (const [key, value] of Object.entries(source)) {
    if (key.startsWith("DEVICE_TOKEN_") && value) deviceTokens[key] = value;
  }
  return { ...parsed, deviceTokens };
}
