import { chromium } from "@playwright/test";
import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";

const distRoot = path.resolve(process.cwd(), "src/AssistedPaymentTerminal.App/dist");
const evidenceRoot = path.resolve(process.cwd(), "docs/evidence/mode1-terminal-shell");
const port = 4174;
const baseUrl = `http://127.0.0.1:${port}`;

await fs.mkdir(evidenceRoot, { recursive: true });
const server = await startStaticServer();
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1366, height: 900 } });

try {
  await page.goto(baseUrl);
  await page.screenshot({ path: path.join(evidenceRoot, "01-terminal-shell.png"), fullPage: true });

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();
  await page.getByText("Payable basis is current").waitFor();
  await page.screenshot({ path: path.join(evidenceRoot, "02-valid-ticket.png"), fullPage: true });

  await page.getByLabel("Ticket reference").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();
  await page.getByText("Tariff expired").waitFor();
  await page.screenshot({ path: path.join(evidenceRoot, "03-expired-tariff.png"), fullPage: true });

  await page.getByRole("button", { name: "Recalculate Fee" }).click();
  await page.getByText("Recalculated").waitFor();
  await page.screenshot({ path: path.join(evidenceRoot, "04-recalculated-tariff.png"), fullPage: true });

  await page.goto(`${baseUrl}/?aptProfile=CONTINUITY_TERMINAL`);
  await page.getByText("Unsupported terminal profile").waitFor();
  await page.screenshot({ path: path.join(evidenceRoot, "05-unsupported-profile.png"), fullPage: true });

  await page.goto(baseUrl);
  await page.getByLabel("Ticket reference").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();
  await page.getByText("Central PMS unavailable").waitFor();
  await page.screenshot({ path: path.join(evidenceRoot, "06-service-unavailable.png"), fullPage: true });

  console.log(`Evidence screenshots saved under ${evidenceRoot}`);
} finally {
  await page.close();
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}

async function startStaticServer() {
  const serverInstance = http.createServer(async (request, response) => {
    const requestUrl = new URL(request.url ?? "/", `http://${request.headers.host}`);
    const relativePath = requestUrl.pathname === "/" ? "index.html" : decodeURIComponent(requestUrl.pathname.slice(1));
    const filePath = path.resolve(distRoot, relativePath);

    if (!filePath.startsWith(distRoot)) {
      response.writeHead(403);
      response.end("Forbidden");
      return;
    }

    try {
      const body = await fs.readFile(filePath);
      response.writeHead(200, { "Content-Type": contentType(filePath) });
      response.end(body);
    } catch {
      const body = await fs.readFile(path.join(distRoot, "index.html"));
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
