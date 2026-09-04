import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // Every integration test shares one real MongoDB: parallel test files
    // contend for dropDatabase and index builds (especially with the live
    // smoke collector writing concurrently), so files run sequentially.
    fileParallelism: false,
    hookTimeout: 30_000,
  },
});
