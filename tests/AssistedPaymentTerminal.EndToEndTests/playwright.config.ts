import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./specs",
  reporter: [["list"]],
  timeout: 30000,
  expect: {
    timeout: 10000,
  },
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "off",
  },
  webServer: {
    command: "node tests/AssistedPaymentTerminal.EndToEndTests/serve-dist.mjs",
    cwd: "../..",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: false,
    timeout: 30000,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1366, height: 900 } },
    },
  ],
});
