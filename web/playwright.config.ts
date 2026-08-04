import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  // Every spec here drives the same .NET API and the same database, so concurrency produces
  // contention rather than coverage — and the canvas specs additionally handshake with identical
  // fingerprint signals from one IP, which is precisely what MaxCoarseFanout is built to refuse.
  // These are integration tests; run them in order.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: "list",
  use: {
    baseURL: "http://localhost:3000",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    // Requires the .NET API (src/Sol) to already be running on :5298 —
    // these are cross-service smoke tests, not mocked unit tests.
    command: "npm run dev",
    url: "http://localhost:3000",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
