import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  test: { environment: "jsdom", setupFiles: ["./test/setup.ts"], include: ["test/**/*.test.{ts,tsx}"], css: false,
    coverage: { provider: "v8", include: ["src/**"], reporter: ["text-summary", "lcov"], thresholds: { statements: 70, branches: 55, functions: 62, lines: 70 } } },
});
