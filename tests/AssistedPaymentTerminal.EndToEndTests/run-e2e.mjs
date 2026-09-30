import { chromium, expect } from "@playwright/test";
import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";
import { installLocalJournalBridgeFixture } from "./local-journal-fixture.mjs";

const root = path.resolve(process.cwd(), "src/AssistedPaymentTerminal.App/dist");
const requestedPort = Number.parseInt(process.env.APT_E2E_PORT ?? "4173", 10);
const port = Number.isInteger(requestedPort) && requestedPort > 0 && requestedPort <= 65535 ? requestedPort : 4173;
const baseUrl = `http://127.0.0.1:${port}`;
const fixtureUrl = `${baseUrl}/?humanSessionFixture=1`;
const e2eConfig = {
  APT_PROFILE: "CASHIER_ASSISTED_TERMINAL",
  APT_TERMINAL_ID: "APT-DEV-001",
  APT_TERMINAL_DISPLAY_NAME: "Development Cashier Terminal 1",
  APT_SITE_ID: "11111111-1111-1111-1111-111111111111",
  APT_SITE_NAME: "ExitPass Demo Parking",
  APT_SITE_GROUP_ID: "22222222-2222-2222-2222-222222222222",
  APT_POS_SERVER_ID: "POS-DEV-001",
  CENTRAL_PMS_BASE_URL: "https://central-pms.example.invalid",
  USE_MOCK_CENTRAL_PMS: "true",
  APT_WEB_UI_URL: baseUrl,
  CENTRAL_PMS_VENDOR_SYSTEM_ID: "VENDOR-PMS-DEV",
};

const server = await startStaticServer();
const browser = await chromium.launch();

try {
  await runUnauthenticatedStartup();
  await runActiveAndExpiredWorkflow();
  await runNoActiveShiftWorkflow();
  await runUnsupportedProfileRefusal();
  await runServiceUnavailableFailure();
  console.log("Playwright E2E passed: 5 scenarios");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}

async function runUnauthenticatedStartup() {
  const page = await newPage();
  await page.goto(baseUrl);

  await expect(page.getByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
  await expect(page.getByRole("heading", { name: "Cashier sign in" })).toBeVisible();
  await expect(page.getByTestId("apt-terminal-shell")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Collect payment" })).toHaveCount(0);
  await page.close();
}

async function runActiveAndExpiredWorkflow() {
  const page = await newPage({ activeShift: true, activeCustody: true });
  await page.goto(fixtureUrl);

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByText("Development Cashier", { exact: true })).toBeVisible();
  await expect(page.getByText("Development Cashier Terminal 1")).toBeVisible();
  await expect(page.getByTestId("cashier-header-shift")).toHaveText("CLOSED");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("CLOSED");
  await page.getByRole("button", { name: "Open Shift" }).click();
  await page.getByRole("button", { name: "Open Cash Custody" }).click();
  await expect(page.getByTestId("cashier-header-shift")).toHaveText("OPEN");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("OPEN");
  await expect(page.getByRole("button", { name: "Cashier Session" })).toHaveAttribute("aria-expanded", "false");

  await page.getByLabel("Ticket number").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByRole("heading", { name: "Parking session details" })).toBeVisible();
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Continue to Cash" })).toHaveCount(0);
  await expect(page.getByText("Local cash capture is disabled in this terminal profile.")).toHaveCount(0);
  await expect(page.getByText("Site ordinance availability")).toHaveCount(0);
  await expect(page.getByText("Source Unavailable")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retry ordinance availability" })).toHaveCount(0);

  await page.getByLabel("Ticket number").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Parking fee has expired. The current amount will be refreshed before cash is recorded.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
  await page.close();
}

async function runNoActiveShiftWorkflow() {
  const page = await newPage({ activeShift: false, activeCustody: false });
  await page.goto(fixtureUrl);

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByTestId("cashier-header-shift")).toHaveText("CLOSED");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("CLOSED");
  await expect(page.getByRole("button", { name: "Cashier Session" })).toHaveAttribute("aria-expanded", "true");

  await page.getByLabel("Ticket number").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toContainText("Open or resume your cashier shift.");
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
  await page.close();
}

async function expectActivePayableBasisReady(page) {
  await expect(page.getByTestId("payable-basis-amount")).toHaveText("₱125.00");
  await expect(page.getByText("Total Amount")).toBeVisible();
  await expect(page.getByTestId("session-readiness-value")).toHaveCount(0);
}

async function runUnsupportedProfileRefusal() {
  const page = await newPage();
  await page.goto(`${baseUrl}/?aptProfile=CONTINUITY_TERMINAL&humanSessionFixture=1`);

  await expect(page.getByText("Unsupported terminal profile")).toBeVisible();
  await expect(page.getByText("CONTINUITY_TERMINAL is not implemented in this slice.")).toBeVisible();
  await page.close();
}

async function runServiceUnavailableFailure() {
  const page = await newPage();
  await page.goto(fixtureUrl);

  await page.getByLabel("Ticket number").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS temporarily unavailable")).toBeVisible();
  await expect(page.getByText("Retry is available after Central PMS is reachable.")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toHaveCount(0);
  await page.close();
}

async function newPage(options = {}) {
  const page = await browser.newPage({ viewport: { width: 1366, height: 900 } });
  page.setDefaultTimeout(10000);
  await installLocalJournalBridgeFixture(page, {
    includeShift: options.activeShift,
    includeCustody: options.activeCustody,
  });
  return page;
}

async function startStaticServer() {
  const serverInstance = http.createServer(async (request, response) => {
    const requestUrl = new URL(request.url ?? "/", `http://${request.headers.host}`);
    const relativePath = requestUrl.pathname === "/" ? "index.html" : decodeURIComponent(requestUrl.pathname.slice(1));
    const filePath = path.resolve(root, relativePath);

    if (!filePath.startsWith(root)) {
      response.writeHead(403);
      response.end("Forbidden");
      return;
    }

    if (relativePath === "apt-config.json") {
      response.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
      response.end(JSON.stringify(e2eConfig));
      return;
    }

    try {
      const body = await fs.readFile(filePath);
      response.writeHead(200, { "Content-Type": contentType(filePath) });
      response.end(body);
    } catch {
      const body = await fs.readFile(path.join(root, "index.html"));
      response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
      response.end(body);
    }
  });

  await new Promise((resolve) => serverInstance.listen(port, "127.0.0.1", resolve));
  return serverInstance;
}

function contentType(filePath) {
  switch (path.extname(filePath)) {
    case ".html":
      return "text/html; charset=utf-8";
    case ".js":
      return "text/javascript; charset=utf-8";
    case ".css":
      return "text/css; charset=utf-8";
    case ".json":
      return "application/json; charset=utf-8";
    default:
      return "application/octet-stream";
  }
}
