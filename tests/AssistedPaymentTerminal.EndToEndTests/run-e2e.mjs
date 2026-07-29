import { chromium, expect } from "@playwright/test";
import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";
import { activeCustodyId, activeShiftId, installLocalJournalBridgeFixture } from "./local-journal-fixture.mjs";

const root = path.resolve(process.cwd(), "src/AssistedPaymentTerminal.App/dist");
const port = 4173;
const baseUrl = `http://127.0.0.1:${port}`;

const server = await startStaticServer();
const browser = await chromium.launch();

try {
  await runActiveAndExpiredWorkflow();
  await runNoActiveShiftWorkflow();
  await runUnsupportedProfileRefusal();
  await runServiceUnavailableFailure();
  console.log("Playwright E2E passed: 4 scenarios");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}

async function runActiveAndExpiredWorkflow() {
  const page = await newPage({ activeShift: true, activeCustody: true });
  await page.goto(baseUrl);

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByText("Development Cashier", { exact: true })).toBeVisible();
  await expect(page.getByTestId("operational-shift-summary")).toHaveText("OPEN");
  await expect(page.getByText("Development Cashier Terminal 1")).toBeVisible();
  await expect(page.getByText("Configured: POS-DEV-001")).toBeVisible();
  await page.getByText("Terminal details").click();
  await expect(page.getByTestId("recovered-shift-id")).toHaveText(activeShiftId);
  await expect(page.getByTestId("active-custody-id")).toHaveText(activeCustodyId);
  await expect(page.getByTestId("configured-shift-posture")).toHaveText("OPEN");

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByTestId("payable-basis-summary")).toContainText("Authoritative payable basis");
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toContainText("Local cash prerequisites unavailable");
  await expect(page.getByTestId("local-cash-prerequisites-value")).toHaveText("Blocked");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByLabel("Ticket reference").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByRole("heading", { name: "Cash acceptance blocked" })).toBeVisible();
  await expect(page.getByTestId("tariff-readiness-value")).toHaveText("Expired");
  await expect(page.getByTestId("central-cash-ready-value")).toHaveText("false");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
  await expect(page.getByText("Parking fee has expired and must be resolved again.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();
  await page.close();
}

async function runNoActiveShiftWorkflow() {
  const page = await newPage({ activeShift: false, activeCustody: false });
  await page.goto(baseUrl);

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByTestId("operational-shift-summary")).toHaveText("No active shift");
  await page.getByText("Terminal details").click();
  await expect(page.getByTestId("configured-shift-posture")).toHaveText("OPEN");
  await expect(page.getByTestId("recovered-shift-id")).toHaveText("None");
  await expect(page.getByTestId("active-custody-id")).toHaveText("None");

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-value")).toHaveText("Blocked");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
  await page.close();
}

async function expectActivePayableBasisReady(page) {
  await expect(page.getByTestId("payable-basis-summary")).toContainText("Authoritative payable basis");
  await expect(page.getByTestId("payable-basis-amount")).toHaveText("₱125.00");
  await expect(page.getByRole("heading", { name: "Ready for cash acceptance" })).toBeVisible();
  await expect(page.getByTestId("session-readiness-value")).toHaveText("Resolved Payable");
  await expect(page.getByTestId("tariff-readiness-value")).toHaveText("Current");
  await expect(page.getByTestId("payment-eligibility-value")).toHaveText("Eligible");
  await expect(page.getByTestId("terminal-cash-readiness-value")).toHaveText("Available");
  await expect(page.getByTestId("sales-invoice-readiness-value")).toHaveText("Ready");
  await expect(page.getByTestId("fiscal-readiness-value")).toHaveText("Ready");
  await expect(page.getByTestId("central-cash-ready-value")).toHaveText("true");
}

async function runUnsupportedProfileRefusal() {
  const page = await newPage();
  await page.goto(`${baseUrl}/?aptProfile=CONTINUITY_TERMINAL`);

  await expect(page.getByText("Unsupported terminal profile")).toBeVisible();
  await expect(page.getByText("CONTINUITY_TERMINAL is not implemented in this slice.")).toBeVisible();
  await page.close();
}

async function runServiceUnavailableFailure() {
  const page = await newPage();
  await page.goto(baseUrl);

  await page.getByLabel("Ticket reference").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS temporarily unavailable")).toBeVisible();
  await expect(page.getByText("Retry is available after Central PMS is reachable.")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toBeVisible();
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
