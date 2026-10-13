import { defineConfig } from "vitest/config";

export default defineConfig({
  test: { coverage: { provider: "v8", include: ["src/index.ts"], reporter: ["text-summary", "lcov"], thresholds: { statements: 90, branches: 70, functions: 90, lines: 90 } } },
});
