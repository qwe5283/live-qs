export interface ServerEnv {
  port: number;
  defaultUserId: string;
}

export function loadEnv(): ServerEnv {
  const port = Number.parseInt(process.env.PORT || "8787", 10);
  return {
    port: Number.isFinite(port) ? port : 8787,
    defaultUserId: process.env.DEFAULT_USER_ID || "local",
  };
}
