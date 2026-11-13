export type CashierAccountabilityPrintRow = {
  label: string;
  value: string;
};

export type CashierAccountabilityPrintSection = {
  title: string;
  rows: CashierAccountabilityPrintRow[];
};

export type CashierAccountabilityPrintDocument = {
  status: "FINAL" | "INTERIM - CUSTODY OPEN";
  custodyReference: string;
  sections: CashierAccountabilityPrintSection[];
  footerNotes: string[];
};

export type CashierAccountabilityPrintSuccess = {
  submittedToWindowsSpooler: true;
  physicalPrintingConfirmed: false;
  duplicateSubmissionIgnored: boolean;
  printerName: string;
  paperWidthMm: 57;
  safeMessage: string;
};

export type CashierAccountabilityPrintResult =
  | {
      ok: true;
      command: "cashierAccountabilityPrint.submit";
      correlationId: string;
      payload: CashierAccountabilityPrintSuccess;
    }
  | {
      ok: false;
      command: "cashierAccountabilityPrint.submit";
      correlationId: string;
      error: { code: string; message: string; retryable: boolean };
    };

export interface CashierAccountabilityPrintBridge {
  submit(
    correlationId: string,
    submissionKey: string,
    report: CashierAccountabilityPrintDocument,
  ): Promise<CashierAccountabilityPrintResult>;
}

const source = "apt-cashier-accountability-print";
const command = "cashierAccountabilityPrint.submit" as const;

export function createWebViewCashierAccountabilityPrintBridge(): CashierAccountabilityPrintBridge {
  return {
    submit: (correlationId, submissionKey, report) => send(correlationId, submissionKey, report),
  };
}

function send(
  correlationId: string,
  submissionKey: string,
  report: CashierAccountabilityPrintDocument,
): Promise<CashierAccountabilityPrintResult> {
  const webview = window.chrome?.webview;
  if (!webview) {
    return Promise.resolve({
      ok: false,
      command,
      correlationId,
      error: {
        code: "DESKTOP_HOST_REQUIRED",
        message: "Cashier Accountability Report printing requires the Windows desktop host.",
        retryable: false,
      },
    });
  }

  return new Promise((resolve) => {
    const listener = (event: { data: unknown }) => {
      const response = parseResponse(event.data);
      if (!response || response.command !== command || response.correlationId !== correlationId) {
        return;
      }

      webview.removeEventListener("message", listener);
      resolve(response);
    };

    webview.addEventListener("message", listener);
    webview.postMessage(JSON.stringify({
      source,
      command,
      correlationId,
      payload: { submissionKey, report },
    }));
  });
}

function parseResponse(data: unknown): CashierAccountabilityPrintResult | null {
  try {
    const parsed = typeof data === "string" ? JSON.parse(data) : data;
    if (!parsed || typeof parsed !== "object" || (parsed as { source?: string }).source !== source) {
      return null;
    }

    return parsed as CashierAccountabilityPrintResult;
  } catch {
    return null;
  }
}
