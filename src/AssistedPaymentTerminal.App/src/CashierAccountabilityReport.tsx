import { useRef, useState } from "react";
import {
  createWebViewCashierAccountabilityPrintBridge,
  type CashierAccountabilityPrintBridge,
  type CashierAccountabilityPrintSection,
} from "./cashierAccountabilityPrintBridge";
import { createCorrelationId } from "./correlation";
import type { CashCustodySummary } from "./humanSessionBridge";

const defaultPrintBridge = createWebViewCashierAccountabilityPrintBridge();

type Props = {
  summary: CashCustodySummary;
  currentCashierId: string | null;
  currentCashierDisplayName: string | null;
  siteDisplayName: string | null;
  terminalDisplayName: string | null;
  terminalSerialNumber: string | null;
  generatedAt: string;
  onClose: () => void;
  printBridge?: CashierAccountabilityPrintBridge;
};

type PrintState =
  | { kind: "idle" }
  | { kind: "submitting" }
  | { kind: "submitted"; message: string }
  | { kind: "failed"; message: string; retryable: boolean };

export function CashierAccountabilityReport({
  summary,
  currentCashierId,
  currentCashierDisplayName,
  siteDisplayName,
  terminalDisplayName,
  terminalSerialNumber,
  generatedAt,
  onClose,
  printBridge = defaultPrintBridge,
}: Props) {
  const [printState, setPrintState] = useState<PrintState>({ kind: "idle" });
  const printInProgress = useRef(false);
  const { session, submissions, accountability } = summary;
  const closed = session.status === "Closed";
  const currentNameApplies = Boolean(
    currentCashierId
    && currentCashierDisplayName
    && currentCashierId.localeCompare(session.cashierId, undefined, { sensitivity: "accent" }) === 0,
  );
  const unresolvedCount = closed
    ? session.unresolvedSubmissionCountAtClose ?? submissions.unresolvedSubmissionCount
    : submissions.unresolvedSubmissionCount;
  const variance = closed ? session.varianceAmount : null;
  const status = closed ? "FINAL" as const : "INTERIM - CUSTODY OPEN" as const;
  const sections: CashierAccountabilityPrintSection[] = [
    {
      title: "Accountability scope",
      rows: [
        { label: "Site", value: siteDisplayName ?? "Unavailable" },
        { label: "Site ID", value: session.siteId },
        { label: "Terminal", value: terminalDisplayName ?? session.terminalId },
        { label: "Terminal ID", value: session.terminalId },
        { label: "Terminal S/N", value: terminalSerialNumber ?? "Unavailable" },
        { label: "Cashier ID", value: session.cashierId },
        {
          label: "Cashier name (current)",
          value: currentNameApplies ? currentCashierDisplayName! : "Unavailable - no historical name snapshot",
        },
        { label: "Shift reference", value: session.cashierShiftId },
        { label: "Custody reference", value: session.id },
      ],
    },
    {
      title: "Custody period",
      rows: [
        { label: "Opened", value: formatDateTime(session.openedAt) },
        { label: "Closed", value: closed ? formatDateTime(session.closedAt) : "Custody remains open" },
        { label: "Report generated", value: formatDateTime(generatedAt) },
      ],
    },
    {
      title: "Physical cash activity",
      rows: [
        { label: "Opening cash fund", value: formatMajorUnits(session.openingCashAmount) },
        {
          label: `Physical cash received (${submissions.physicalCashReceiptCount})`,
          value: formatMinorUnits(accountability?.physicalCashReceivedMinorUnits),
        },
        {
          label: `Central-Confirmed Payments (Local Record) (${submissions.confirmedSubmissionCount})`,
          value: formatMinorUnits(accountability?.canonicallyConfirmedCashMinorUnits),
        },
        { label: "Completed cash returns", value: formatMinorUnits(accountability?.completedCashReturnsMinorUnits) },
        { label: "Completed safe drops", value: formatMinorUnits(accountability?.completedSafeDropsMinorUnits) },
        { label: "Completed direct remittances", value: formatMinorUnits(accountability?.completedDirectRemittancesMinorUnits) },
        { label: "Unresolved cash submissions", value: String(unresolvedCount) },
      ],
    },
    {
      title: "Closing reconciliation",
      rows: [
        {
          label: "Expected closing cash",
          value: closed ? formatOptionalMajorUnits(session.expectedClosingCashAmount) : "Unavailable - custody open",
        },
        {
          label: "Actual physical closing cash",
          value: closed ? formatOptionalMajorUnits(session.closingCashAmount) : "Unavailable - custody open",
        },
        {
          label: "Signed cash variance",
          value: closed ? formatSignedVariance(variance) : "Unavailable - custody open",
        },
        { label: "Variance result", value: closed ? varianceClassification(variance) : "INTERIM" },
      ],
    },
  ];
  const footerNotes = [
    "Physical cash received is custody evidence, not fiscal sales.",
    "This is not a POS X/Z Reading or fiscal business-day report.",
    ...(currentNameApplies
      ? ["The display name is current identity data. Historical attribution uses Cashier ID."]
      : []),
  ];
  const printLocked = printState.kind === "submitting" || printState.kind === "submitted"
    || (printState.kind === "failed" && !printState.retryable);

  async function printReport() {
    if (printInProgress.current || printLocked) return;
    printInProgress.current = true;
    setPrintState({ kind: "submitting" });
    try {
      const result = await printBridge.submit(
        createCorrelationId(),
        `cashier-accountability:${session.id}:${generatedAt}`,
        { status, custodyReference: session.id, sections, footerNotes },
      );
      setPrintState(result.ok
        ? { kind: "submitted", message: result.payload.safeMessage }
        : { kind: "failed", message: result.error.message, retryable: result.error.retryable });
    } catch {
      setPrintState({
        kind: "failed",
        message: "The Windows desktop host did not return a report printing result.",
        retryable: false,
      });
    } finally {
      printInProgress.current = false;
    }
  }

  return (
    <section className="cashier-session-group cashier-accountability-report" aria-label="Cashier Accountability Report preview">
      <div className="cashier-accountability-report-actions">
        <div>
          <h3>Cashier Accountability Report</h3>
          <p>Read-only local cash-custody evidence.</p>
        </div>
        <div>
          <button type="button" disabled={printLocked} onClick={() => void printReport()}>
            {printState.kind === "submitting" ? "Submitting report..." : "Print report"}
          </button>
          <button type="button" className="secondary-action" onClick={onClose}>Close preview</button>
        </div>
      </div>

      {printState.kind === "submitted" && <p className="status-notice info" role="status">{printState.message}</p>}
      {printState.kind === "failed" && <p className="cash-error" role="alert">{printState.message}</p>}

      <article className="receipt-paper receipt-paper-57 cashier-accountability-print" aria-label="Printable Cashier Accountability Report">
        <header className="receipt-paper-header">
          <p className="receipt-paper-merchant">CASHIER ACCOUNTABILITY REPORT</p>
          <p className="cashier-accountability-status">{status}</p>
        </header>

        {sections.map((section) => <ReportSection key={section.title} section={section} />)}

        <footer className="receipt-paper-footer">
          {footerNotes.map((note) => <p key={note}>{note}</p>)}
        </footer>
      </article>
    </section>
  );
}

function ReportSection({ section }: { section: CashierAccountabilityPrintSection }) {
  return (
    <section className="receipt-paper-section">
      <h4>{section.title}</h4>
      <dl className="receipt-paper-fields">
        {section.rows.map((row) => <ReportRow key={row.label} label={row.label} value={row.value} />)}
      </dl>
    </section>
  );
}

function ReportRow({ label, value }: { label: string; value: string }) {
  return <div><dt>{label}</dt><dd>{value}</dd></div>;
}

function formatMinorUnits(value: number | null | undefined): string {
  return value == null ? "Unavailable" : formatMajorUnits(value / 100);
}

function formatOptionalMajorUnits(value: number | null | undefined): string {
  return value == null ? "Unavailable" : formatMajorUnits(value);
}

function formatMajorUnits(value: number): string {
  return new Intl.NumberFormat("en-PH", { style: "currency", currency: "PHP" }).format(value);
}

function formatSignedVariance(value: number | null | undefined): string {
  if (value == null) return "Unavailable";
  if (value > 0) return `+${formatMajorUnits(value)}`;
  return formatMajorUnits(value);
}

function varianceClassification(value: number | null | undefined): string {
  if (value == null) return "Unavailable";
  if (value > 0) return "OVER";
  if (value < 0) return "SHORT";
  return "BALANCED";
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return "Unavailable";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Unavailable";
  return `${new Intl.DateTimeFormat("en-PH", {
    dateStyle: "medium",
    timeStyle: "medium",
    timeZone: "Asia/Manila",
  }).format(date)} Asia/Manila`;
}
