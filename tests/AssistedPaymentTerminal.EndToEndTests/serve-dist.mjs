import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";

const root = path.resolve(process.cwd(), "src/AssistedPaymentTerminal.App/dist");
const port = 4173;
const contentTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".svg", "image/svg+xml"],
]);

const server = http.createServer(async (request, response) => {
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
    response.writeHead(200, { "Content-Type": contentTypes.get(path.extname(filePath)) ?? "application/octet-stream" });
    response.end(body);
  } catch {
    const body = await fs.readFile(path.join(root, "index.html"));
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end(body);
  }
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Serving Assisted Payment Terminal dist on http://127.0.0.1:${port}`);
});

process.on("SIGTERM", () => server.close(() => process.exit(0)));
process.on("SIGINT", () => server.close(() => process.exit(0)));
