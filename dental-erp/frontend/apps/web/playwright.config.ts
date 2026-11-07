import { defineConfig, devices } from "@playwright/test";

/**
 * e2e REAL backend-ə qarşı işləyir (infra/dev/run-stack.sh): Gateway, Identity, ClinicCore, Billing, Notify, Workers, PostgreSQL, RabbitMQ.
 * Next.js istehsal rejimində qaldırılır; `API_ORIGIN` və `NEXT_PUBLIC_DEV_TENANT` BUILD zamanı sabitlənir (pnpm e2e:build).
 */
const port = Number(process.env.E2E_PORT ?? 3100);
const apiOrigin = process.env.E2E_API_ORIGIN ?? "http://127.0.0.1:5100";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: [["list"]],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    trace: "retain-on-failure",
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM } : {},
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `pnpm exec next start -p ${port} -H 127.0.0.1`,
    url: `http://127.0.0.1:${port}/login`,
    reuseExistingServer: !process.env.CI,
    env: { API_ORIGIN: apiOrigin, DEV_TENANT: process.env.E2E_TENANT ?? "demo" },
  },
});
