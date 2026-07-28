import { createApp } from "./app";

const app = createApp();
const server = Bun.serve({
  port: app.port,
  fetch: app.fetch,
});

console.log(`[server] AI Life Context API listening on http://localhost:${server.port}`);
