import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // The integration test spawns one server process against real MongoDB;
    // keep files sequential like the server suite does.
    fileParallelism: false,
    hookTimeout: 60_000,
    testTimeout: 30_000,
  },
});
