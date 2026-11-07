import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  test: { environment: "jsdom", setupFiles: ["./test/setup.ts"], globals: false, css: false,
    coverage: { provider: "v8", include: ["src/**"], reporter: ["text-summary", "lcov"], thresholds: { statements: 90, branches: 85, functions: 90, lines: 90 } } },
});
