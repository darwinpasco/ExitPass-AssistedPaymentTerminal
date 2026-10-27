import type { ReactNode } from "react";
import type { CashCustodySummary } from "./humanSessionBridge";

type Props = {
  summary: CashCustodySummary;
  currentCashierId: string | null;
  currentCashierDisplayName: string | null;
  siteDisplayName: string | null;
  terminalDisplayName: string | null;
  terminalSerialNumber: string | null;
  generatedAt: string;
  onClose: () => void;
  onPrint?: () => void;
};

export function CashierAccountabilityReport({
  summary,
  currentCashierId,
  currentCashierDisplayName,
  siteDisplayName,
  terminalDisplayName,
  terminalSerialNumber,
  generatedAt,
  onClose,
  onPrint = () => window.print(),
}: Props) {
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

  return (
    <section className="cashier-session-group cashier-accountability-report" aria-label="Cashier Accountability Report preview">
      <div className="cashier-accountability-report-actions">
        <div>
          <h3>Cashier Accountability Report</h3>
          <p>Read-only local cash-custody evidence.</p>
        </div>
        <div>
          <button type="button" onClick={onPrint}>Print report</button>
          <button type="button" className="secondary-action" onClick={onClose}>Close preview</button>
        </div>
      </div>

      <article className="receipt-paper receipt-paper-57 cashier-accountability-print" aria-label="Printable Cashier Accountability Report">
        <header className="receipt-paper-header">
          <p className="receipt-paper-merchant">CASHIER ACCOUNTABILITY REPORT</p>
          <p className="cashier-accountability-status">{closed ? "FINAL" : "INTERIM - CUSTODY OPEN"}</p>
        </header>

        <ReportSection title="Accountability scope">
          <ReportRow label="Site" value={siteDisplayName ?? "Unavailable"} />
          <ReportRow label="Site ID" value={session.siteId} />
          <ReportRow label="Terminal" value={terminalDisplayName ?? session.terminalId} />
          <ReportRow label="Terminal ID" value={session.terminalId} />
          <ReportRow label="Terminal S/N" value={terminalSerialNumber ?? "Unavailable"} />
          <ReportRow label="Cashier ID" value={session.cashierId} />
          <ReportRow
            label="Cashier name (current)"
            value={currentNameApplies ? currentCashierDisplayName! : "Unavailable - no historical name snapshot"}
          />
          <ReportRow label="Shift reference" value={session.cashierShiftId} />
          <ReportRow label="Custody reference" value={session.id} />
        </ReportSection>

        <ReportSection title="Custody period">
          <ReportRow label="Opened" value={formatDateTime(session.openedAt)} />
          <ReportRow label="Closed" value={closed ? formatDateTime(session.closedAt) : "Custody remains open"} />
          <ReportRow label="Report generated" value={formatDateTime(generatedAt)} />
        </ReportSection>

        <ReportSection title="Physical cash activity">
          <ReportRow label="Opening cash fund" value={formatMajorUnits(session.openingCashAmount)} />
          <ReportRow
            label={`Physical cash received (${submissions.physicalCashReceiptCount})`}
            value={formatMinorUnits(accountability?.physicalCashReceivedMinorUnits)}
          />
          <ReportRow
            label={`Central-Confirmed Payments (Local Record) (${submissions.confirmedSubmissionCount})`}
            value={formatMinorUnits(accountability?.canonicallyConfirmedCashMinorUnits)}
          />
          <ReportRow label="Completed cash returns" value={formatMinorUnits(accountability?.completedCashReturnsMinorUnits)} />
          <ReportRow label="Completed safe drops" value={formatMinorUnits(accountability?.completedSafeDropsMinorUnits)} />
          <ReportRow label="Completed direct remittances" value={formatMinorUnits(accountability?.completedDirectRemittancesMinorUnits)} />
          <ReportRow label="Unresolved cash submissions" value={String(unresolvedCount)} />
        </ReportSection>

        <ReportSection title="Closing reconciliation">
          <ReportRow
            label="Expected closing cash"
            value={closed ? formatOptionalMajorUnits(session.expectedClosingCashAmount) : "Unavailable - custody open"}
          />
          <ReportRow
            label="Actual physical closing cash"
            value={closed ? formatOptionalMajorUnits(session.closingCashAmount) : "Unavailable - custody open"}
          />
          <ReportRow
            label="Signed cash variance"
            value={closed ? formatSignedVariance(variance) : "Unavailable - custody open"}
          />
          <ReportRow label="Variance result" value={closed ? varianceClassification(variance) : "INTERIM"} />
        </ReportSection>

        <footer className="receipt-paper-footer">
          <p>Physical cash received is custody evidence, not fiscal sales.</p>
          <p>This is not a POS X/Z Reading or fiscal business-day report.</p>
          {currentNameApplies && <p>The display name is current identity data. Historical attribution uses Cashier ID.</p>}
        </footer>
      </article>
    </section>
  );
}

function ReportSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="receipt-paper-section">
      <h4>{title}</h4>
      <dl className="receipt-paper-fields">{children}</dl>
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
