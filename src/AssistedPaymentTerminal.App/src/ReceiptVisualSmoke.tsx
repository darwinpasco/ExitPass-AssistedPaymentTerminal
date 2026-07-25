import { useMemo, useState } from "react";
import type { ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import { CashCapturePanel } from "./CashCapturePanel";
import type { AptConfig } from "./config";
import type { LocalJournalBridge } from "./localJournalBridge";
import { buildTerminalContext } from "./terminalContext";

export type ReceiptVisualSmokeScenarioId =
  | "temporarily-unavailable"
  | "available"
  | "terminal-failure"
  | "restart-recovery"
  | "incomplete-configuration"
  | "original-print"
  | "reprint"
  | "printer-unavailable"
  | "retryable-printer-failure"
  | "unknown-spooler-outcome"
  | "unsupported-width"
  | "history-empty"
  | "history-original-submitted"
  | "history-original-plus-reprints"
  | "history-latest-failed"
  | "history-unknown-outcome"
  | "history-printer-changed"
  | "history-width-changed"
  | "history-inconsistent-copy-sequence"
  | "history-restart-recovery";

export type ReceiptVisualSmokeScenario = {
  id: ReceiptVisualSmokeScenarioId;
  label: string;
  terminalCashTenderId: string;
  ticketReference: string;
  parkingSessionId: string;
  expectedReceiptPosture: string;
};

export const receiptVisualSmokeScenarios: ReceiptVisualSmokeScenario[] = [
  {
    id: "temporarily-unavailable",
    label: "Temporarily unavailable",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2001",
    ticketReference: "VISUAL-RECEIPT-UNAVAILABLE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2001",
    expectedReceiptPosture: "Retryable unavailable status; no Sales Invoice preview.",
  },
  {
    id: "available",
    label: "Available",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2002",
    ticketReference: "VISUAL-RECEIPT-AVAILABLE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2002",
    expectedReceiptPosture: "Authoritative Sales Invoice preview with governed values.",
  },
  {
    id: "terminal-failure",
    label: "Terminal failure",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2003",
    ticketReference: "VISUAL-RECEIPT-TERMINAL",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2003",
    expectedReceiptPosture: "Unsupported terminal state; no retry button.",
  },
  {
    id: "restart-recovery",
    label: "Restart-recovery setup",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2004",
    ticketReference: "VISUAL-RECEIPT-RESTART",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2004",
    expectedReceiptPosture: "Stable parking session for durable restart recovery.",
  },
  {
    id: "incomplete-configuration",
    label: "Incomplete configuration",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2005",
    ticketReference: "VISUAL-RECEIPT-INCOMPLETE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2005",
    expectedReceiptPosture: "Preview blocked safely; no placeholder invoice.",
  },
  {
    id: "original-print",
    label: "Original print available",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3001",
    ticketReference: "VISUAL-PRINT-ORIGINAL",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3001",
    expectedReceiptPosture: "Authoritative receipt available; first accepted print is ORIGINAL.",
  },
  {
    id: "reprint",
    label: "Reprint",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3002",
    ticketReference: "VISUAL-PRINT-REPRINT",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3002",
    expectedReceiptPosture: "After an original accepted print, later cashier prints show REPRINTED with the accepted reprint timestamp above SALES INVOICE.",
  },
  {
    id: "printer-unavailable",
    label: "Printer unavailable",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3003",
    ticketReference: "VISUAL-PRINT-UNAVAILABLE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3003",
    expectedReceiptPosture: "Safe printer-unavailable failure; receipt and fiscal state are unchanged.",
  },
  {
    id: "retryable-printer-failure",
    label: "Retryable printer failure",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3004",
    ticketReference: "VISUAL-PRINT-RETRYABLE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3004",
    expectedReceiptPosture: "Retryable spooler failure creates a linked attempt without duplicate simultaneous jobs.",
  },
  {
    id: "unknown-spooler-outcome",
    label: "Unknown spooler outcome",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3005",
    ticketReference: "VISUAL-PRINT-UNKNOWN",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3005",
    expectedReceiptPosture: "Unknown printer result survives restart and is not silently resubmitted.",
  },
  {
    id: "unsupported-width",
    label: "Unsupported width",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee3006",
    ticketReference: "VISUAL-PRINT-WIDTH",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa3006",
    expectedReceiptPosture: "Unsupported width falls back safely to 57 mm with a configuration warning.",
  },
  {
    id: "history-empty",
    label: "No print history",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4001",
    ticketReference: "VISUAL-HISTORY-EMPTY",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4001",
    expectedReceiptPosture: "Sales Invoice is available; local print history shows an empty state.",
  },
  {
    id: "history-original-submitted",
    label: "Original submitted",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4002",
    ticketReference: "VISUAL-HISTORY-ORIGINAL",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4002",
    expectedReceiptPosture: "Print history shows Original, copy sequence 1, and submitted-to-printer wording without physical-output overclaim.",
  },
  {
    id: "history-original-plus-reprints",
    label: "Original plus reprints",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4003",
    ticketReference: "VISUAL-HISTORY-REPRINTS",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4003",
    expectedReceiptPosture: "Print history shows Original and Reprint rows with unchanged fiscal document identity.",
  },
  {
    id: "history-latest-failed",
    label: "Latest attempt failed",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4004",
    ticketReference: "VISUAL-HISTORY-FAILED",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4004",
    expectedReceiptPosture: "Print history keeps previous submitted evidence visible while the latest failure is safely labeled.",
  },
  {
    id: "history-unknown-outcome",
    label: "Unknown outcome",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4005",
    ticketReference: "VISUAL-HISTORY-UNKNOWN",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4005",
    expectedReceiptPosture: "Print result requires confirmation; no resolve, retry, or reprint action is introduced by history.",
  },
  {
    id: "history-printer-changed",
    label: "Printer changed",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4006",
    ticketReference: "VISUAL-HISTORY-PRINTER-CHANGED",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4006",
    expectedReceiptPosture: "Local reconciliation attention flags printer changes across copies.",
  },
  {
    id: "history-width-changed",
    label: "Paper width changed",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4007",
    ticketReference: "VISUAL-HISTORY-WIDTH-CHANGED",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4007",
    expectedReceiptPosture: "Local reconciliation attention flags paper-width changes across copies.",
  },
  {
    id: "history-inconsistent-copy-sequence",
    label: "Inconsistent copy sequence",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4008",
    ticketReference: "VISUAL-HISTORY-COPY-SEQUENCE",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4008",
    expectedReceiptPosture: "Local reconciliation attention flags duplicate copy sequence without repairing records.",
  },
  {
    id: "history-restart-recovery",
    label: "Print history restart recovery",
    terminalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee4009",
    ticketReference: "VISUAL-HISTORY-RESTART",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa4009",
    expectedReceiptPosture: "Restart with the same SQLite journal preserves print history and creates no new print attempt.",
  },
];

export function shouldUseReceiptVisualSmoke(
  search: string,
  isDevelopment = (import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV === true,
): boolean {
  return isDevelopment && new URLSearchParams(search).get("receiptVisualSmoke") === "1";
}

export function ReceiptVisualSmokeShell({ config, bridge }: { config: AptConfig; bridge?: LocalJournalBridge }) {
  const [selectedId, setSelectedId] = useState<ReceiptVisualSmokeScenarioId>("temporarily-unavailable");
  const scenario = receiptVisualSmokeScenarios.find((value) => value.id === selectedId) ?? receiptVisualSmokeScenarios[0];
  const smokeConfig = useMemo<AptConfig>(
    () => ({
      ...config,
      nonLiveCashCaptureEnabled: true,
      centralPmsCashSubmissionEnabled: true,
      centralPmsFiscalIssuanceEnabled: true,
      centralPmsReceiptRetrievalEnabled: true,
      receiptPreviewEnabled: true,
      receiptPrintingEnabled: true,
      receiptPrinterName: "APT Controlled Printer",
      centralPmsConnectionMode: "mock",
    }),
    [config],
  );
  const scenarioConfig = useMemo<AptConfig>(
    () => scenario.id === "unsupported-width"
      ? {
          ...smokeConfig,
          receiptPaperWidthMm: 57,
          receiptPaperWidthWarning: "Unsupported APT_RECEIPT_PAPER_WIDTH_MM value '99'. Falling back to 57 mm.",
        }
      : smokeConfig,
    [scenario.id, smokeConfig],
  );
  const context = useMemo(() => buildTerminalContext(scenarioConfig), [scenarioConfig]);
  const session = useMemo(() => buildScenarioSession(scenarioConfig, scenario), [scenarioConfig, scenario]);

  return (
    <main
      className="terminal-shell receipt-visual-smoke-shell"
      data-testid="apt-terminal-shell"
      data-surface="receipt-visual-smoke"
      data-app-ready="true"
    >
      <header className="brand-header">
        <div>
          <p className="eyebrow">Development fixture</p>
          <h1>Receipt Visual Smoke</h1>
        </div>
        <span className="status-badge warning">Development-only</span>
      </header>

      <section className="visual-smoke-selector" aria-label="Receipt visual smoke scenarios">
        {receiptVisualSmokeScenarios.map((value) => (
          <button
            key={value.id}
            type="button"
            className={value.id === selectedId ? "secondary-action selected" : "secondary-action"}
            aria-pressed={value.id === selectedId}
            onClick={() => setSelectedId(value.id)}
          >
            {value.label}
          </button>
        ))}
      </section>

      <section className="status-notice info" role="status" aria-label="Selected receipt visual smoke scenario">
        <h3>{scenario.label}</h3>
        <p>{scenario.expectedReceiptPosture}</p>
        <p className="support-line">Ticket reference: {scenario.ticketReference}</p>
      </section>

      <div className="resolved-workflow">
        <section className="session-summary" aria-label="Controlled parking session">
          <div className="amount-band">
            <div>
              <p className="eyebrow">Controlled payable basis</p>
              <strong>PHP 125.00</strong>
            </div>
            <span className="status-badge">Fixture</span>
          </div>
          <dl className="summary-primary">
            <div className="summary-row">
              <dt>Parking session ID</dt>
              <dd>{scenario.parkingSessionId}</dd>
            </div>
            <div className="summary-row">
              <dt>Ticket reference</dt>
              <dd>{scenario.ticketReference}</dd>
            </div>
            <div className="summary-row">
              <dt>Fiscal document</dt>
              <dd>SI-000001</dd>
            </div>
          </dl>
        </section>
        <CashCapturePanel
          config={scenarioConfig}
          context={context}
          session={session}
          tariffExpired={false}
          bridge={bridge}
          developmentFixtureLocalCashTenderId={scenario.terminalCashTenderId}
        />
      </div>
    </main>
  );
}

function buildScenarioSession(config: AptConfig, scenario: ReceiptVisualSmokeScenario): ResolveVendorParkingResponse {
  const now = Date.now();
  const issued = new Date(now - 60 * 60 * 1000).toISOString();
  const expires = new Date(now + 60 * 60 * 1000).toISOString();

  return {
    parkingSessionId: scenario.parkingSessionId,
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd2001",
    siteGroupId: config.siteGroupId,
    siteId: config.siteId,
    siteGroupName: "Visual Smoke Site Group",
    siteName: config.siteName,
    lookupOutcome: "RESOLVED",
    plateNumber: "APT-2026",
    ticketReference: scenario.ticketReference,
    entryTime: issued,
    currentFeeCalculationTime: issued,
    netPayableMinorUnits: 12500,
    currency: "PHP",
    tariffExpiresAt: expires,
    feeValidUntil: expires,
    parkingStatus: "ACTIVE",
    paymentStatus: "UNPAID",
    statutoryDiscountApplied: false,
    vendorSystemId: config.vendorSystemId,
    correlationId: `receipt-visual-smoke:${scenario.id}`,
  };
}
