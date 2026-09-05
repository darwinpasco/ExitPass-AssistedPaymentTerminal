export type PayableBasisBridgeCommand = "payableBasis.resolve" | "payableBasis.revalidate";

export type PayableBasisBridgeResult =
  | {
      ok: true;
      command: PayableBasisBridgeCommand;
      correlationId: string;
      payload: { statusCode: number; body: Record<string, unknown> | null };
    }
  | {
      ok: false;
      command: PayableBasisBridgeCommand;
      correlationId: string;
      error: { code: string; message: string };
    };

export function sendPayableBasisRequest(
  command: PayableBasisBridgeCommand,
  correlationId: string,
  siteId: string,
  body: unknown,
): Promise<PayableBasisBridgeResult | null> {
  const webview = window.chrome?.webview;
  if (!webview) {
    return Promise.resolve(null);
  }

  return new Promise((resolve) => {
    const listener = (event: { data: unknown }) => {
      const response = typeof event.data === "string" ? safeParse(event.data) : event.data;
      if (!isResponse(response, command, correlationId)) return;
      webview.removeEventListener("message", listener);
      resolve(response);
    };

    webview.addEventListener("message", listener);
    webview.postMessage(JSON.stringify({
      source: "apt-central-pms-payable-basis",
      command,
      correlationId,
      siteId,
      body,
    }));
  });
}

function safeParse(value: string): unknown {
  try { return JSON.parse(value); } catch { return null; }
}

function isResponse(
  value: unknown,
  command: PayableBasisBridgeCommand,
  correlationId: string,
): value is PayableBasisBridgeResult {
  const candidate = value as Partial<PayableBasisBridgeResult> | null;
  return Boolean(
    candidate &&
      candidate.command === command &&
      candidate.correlationId === correlationId &&
      typeof candidate.ok === "boolean",
  );
}
