import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createWebViewCashierAccountabilityPrintBridge,
  type CashierAccountabilityPrintDocument,
} from "./cashierAccountabilityPrintBridge";

describe("Cashier Accountability Report desktop print bridge", () => {
  afterEach(() => {
    delete window.chrome;
  });

  it("fails safely outside the Windows desktop host", async () => {
    const result = await createWebViewCashierAccountabilityPrintBridge().submit(
      "11111111-1111-4111-8111-111111111111",
      "cashier-accountability:custody-1:generated",
      report(),
    );

    expect(result).toMatchObject({
      ok: false,
      error: { code: "DESKTOP_HOST_REQUIRED", retryable: false },
    });
  });

  it("sends the structured report to the dedicated native command", async () => {
    let listener: ((event: { data: unknown }) => void) | undefined;
    const postMessage = vi.fn((message: string) => {
      const request = JSON.parse(message) as {
        source: string;
        command: string;
        correlationId: string;
        payload: { submissionKey: string; report: CashierAccountabilityPrintDocument };
      };
      expect(request.source).toBe("apt-cashier-accountability-print");
      expect(request.command).toBe("cashierAccountabilityPrint.submit");
      expect(request.payload.report.sections[0].rows).toContainEqual({
        label: "Custody reference",
        value: "custody-1",
      });
      listener?.({
        data: JSON.stringify({
          source: request.source,
          ok: true,
          command: request.command,
          correlationId: request.correlationId,
          payload: {
            submittedToWindowsSpooler: true,
            physicalPrintingConfirmed: false,
            duplicateSubmissionIgnored: false,
            printerName: "TSC TTP-225",
            paperWidthMm: 57,
            safeMessage: "Submitted to the Windows spooler. Physical printing is not confirmed.",
          },
        }),
      });
    });
    window.chrome = {
      webview: {
        postMessage,
        addEventListener: (_type, callback) => { listener = callback; },
        removeEventListener: vi.fn(),
      },
    };

    const result = await createWebViewCashierAccountabilityPrintBridge().submit(
      "11111111-1111-4111-8111-111111111111",
      "cashier-accountability:custody-1:generated",
      report(),
    );

    expect(result).toMatchObject({
      ok: true,
      payload: {
        submittedToWindowsSpooler: true,
        physicalPrintingConfirmed: false,
        paperWidthMm: 57,
      },
    });
    expect(postMessage).toHaveBeenCalledOnce();
  });
});

function report(): CashierAccountabilityPrintDocument {
  return {
    status: "FINAL",
    custodyReference: "custody-1",
    sections: [
      {
        title: "Accountability scope",
        rows: [{ label: "Custody reference", value: "custody-1" }],
      },
      { title: "Custody period", rows: [{ label: "Opened", value: "now" }] },
      { title: "Physical cash activity", rows: [{ label: "Opening cash fund", value: "PHP 0.00" }] },
      { title: "Closing reconciliation", rows: [{ label: "Variance result", value: "BALANCED" }] },
    ],
    footerNotes: [
      "Physical cash received is custody evidence, not fiscal sales.",
      "This is not a POS X/Z Reading or fiscal business-day report.",
    ],
  };
}
