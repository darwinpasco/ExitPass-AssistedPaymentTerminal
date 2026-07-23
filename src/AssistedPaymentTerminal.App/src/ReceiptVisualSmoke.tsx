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
  | "incomplete-configuration";

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
      centralPmsConnectionMode: "mock",
    }),
    [config],
  );
  const context = useMemo(() => buildTerminalContext(smokeConfig), [smokeConfig]);
  const session = useMemo(() => buildScenarioSession(smokeConfig, scenario), [smokeConfig, scenario]);

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
          config={smokeConfig}
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
