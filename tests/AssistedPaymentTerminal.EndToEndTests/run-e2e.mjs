import { chromium, expect } from "@playwright/test";
import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";

const root = path.resolve(process.cwd(), "src/AssistedPaymentTerminal.App/dist");
const port = 4173;
const baseUrl = `http://127.0.0.1:${port}`;

const server = await startStaticServer();
const browser = await chromium.launch();

try {
  await runActiveAndExpiredWorkflow();
  await runUnsupportedProfileRefusal();
  await runServiceUnavailableFailure();
  console.log("Playwright E2E passed: 3 scenarios");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}

async function runActiveAndExpiredWorkflow() {
  const page = await newPage();
  await page.goto(baseUrl);

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal" })).toBeVisible();
  await expect(page.getByText("Mode 1", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Development Cashier Terminal 1" })).toBeVisible();
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByText("POS-DEV-001")).toBeVisible();
  await expect(page.getByText("Development Cashier (CASHIER-DEV-001)")).toBeVisible();
  await expect(page.getByText("SHIFT-DEV-20260714-A")).toBeVisible();

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Authoritative payable basis")).toBeVisible();
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expect(page.getByText("Payable basis is current")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByLabel("Ticket reference").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Tariff expired")).toBeVisible();
  await expect(page.getByText("Blocked by expired tariff")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByRole("button", { name: "Recalculate Fee" }).click();
  await expect(page.getByText("Recalculated")).toBeVisible();
  await expect(page.getByText("Payable basis is current")).toBeVisible();
  await page.close();
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

  await expect(page.getByText("Central PMS unavailable")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toBeVisible();
  await page.close();
}

async function newPage() {
  const page = await browser.newPage({ viewport: { width: 1366, height: 900 } });
  page.setDefaultTimeout(10000);
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
