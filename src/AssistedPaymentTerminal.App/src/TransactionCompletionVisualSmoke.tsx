import { useMemo, useState } from "react";
import type { ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import { CashCapturePanel } from "./CashCapturePanel";
import type { AptConfig } from "./config";
import type {
  BridgeResult,
  CashTenderSnapshot,
  CentralPmsCashFiscalCommand,
  CentralPmsCashFiscalStatus,
  CentralPmsCashReceiptCommand,
  CentralPmsCashReceiptStatus,
  CentralPmsCashSubmissionCommand,
  CentralPmsCashSubmissionStatus,
  LocalJournalBridge,
  RecordCashReceivedPayload,
} from "./localJournalBridge";
import { buildTerminalContext } from "./terminalContext";

export type TransactionCompletionScenarioId =
  | "cash-received-awaiting-submission"
  | "submission-accepted"
  | "submission-retryable"
  | "payment-finality-pending"
  | "payment-final-fiscal-pending"
  | "fiscal-retryable"
  | "fiscal-recorded-receipt-unavailable"
  | "receipt-available"
  | "terminal-payment-failure"
  | "terminal-fiscal-failure"
  | "receipt-malformed"
  | "restart-after-cash-received"
  | "restart-payment-pending"
  | "restart-fiscal-pending"
  | "restart-receipt-available";

export type TransactionCompletionScenario = {
  id: TransactionCompletionScenarioId;
  label: string;
  expectedPosture: string;
  autoAdvance: boolean;
};

export const transactionCompletionVisualSmokeScenarios: TransactionCompletionScenario[] = [
  { id: "cash-received-awaiting-submission", label: "CASH_RECEIVED awaiting submission", expectedPosture: "Cash is in local custody; terminal-cash command has not been submitted.", autoAdvance: false },
  { id: "submission-accepted", label: "Terminal-cash submission accepted", expectedPosture: "Payment command is accepted and payment finality is confirmed.", autoAdvance: false },
  { id: "submission-retryable", label: "Terminal-cash submission retryable", expectedPosture: "Persisted tender remains retryable without creating another cash record.", autoAdvance: false },
  { id: "payment-finality-pending", label: "Payment finality pending", expectedPosture: "Accepted command does not imply finality; readback remains pending.", autoAdvance: false },
  { id: "payment-final-fiscal-pending", label: "Payment final, fiscal pending", expectedPosture: "Payment is final while fiscal issuance remains a separate pending stage.", autoAdvance: false },
  { id: "fiscal-retryable", label: "Fiscal retryable", expectedPosture: "Fiscal status is retryable; no duplicate fiscal document is requested.", autoAdvance: false },
  { id: "fiscal-recorded-receipt-unavailable", label: "Fiscal document recorded, receipt unavailable", expectedPosture: "Fiscal document identity is recorded while receipt retrieval remains retryable.", autoAdvance: false },
  { id: "receipt-available", label: "Receipt available", expectedPosture: "Authoritative receipt presentation is available; ExitAuthorization readback remains contract-blocked.", autoAdvance: false },
  { id: "terminal-payment-failure", label: "Terminal payment failure", expectedPosture: "Terminal payment failure requires support and does not advance fiscal or receipt state.", autoAdvance: false },
  { id: "terminal-fiscal-failure", label: "Terminal fiscal failure", expectedPosture: "Payment finality remains distinct while fiscal terminal failure blocks completion.", autoAdvance: false },
  { id: "receipt-malformed", label: "Receipt malformed or unsupported", expectedPosture: "Malformed receipt response is terminal/support-required; no fallback receipt is rendered.", autoAdvance: false },
  { id: "restart-after-cash-received", label: "Restart after CASH_RECEIVED", expectedPosture: "Restart restores the same CASH_RECEIVED tender without resubmission.", autoAdvance: false },
  { id: "restart-payment-pending", label: "Restart during payment pending", expectedPosture: "Restart preserves pending payment readback and the same tender identity.", autoAdvance: false },
  { id: "restart-fiscal-pending", label: "Restart during fiscal pending", expectedPosture: "Restart preserves payment finality and pending fiscal state.", autoAdvance: false },
  { id: "restart-receipt-available", label: "Restart with receipt available", expectedPosture: "Restart preserves authoritative receipt evidence and still does not infer ExitAuthorization.", autoAdvance: false },
];

export function shouldUseTransactionCompletionVisualSmoke(
  search: string,
  isDevelopment = (import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV === true,
): boolean {
  return isDevelopment && new URLSearchParams(search).get("transactionCompletionVisualSmoke") === "1";
}

export function TransactionCompletionVisualSmokeShell({ config }: { config: AptConfig }) {
  const [selectedId, setSelectedId] = useState<TransactionCompletionScenarioId>("cash-received-awaiting-submission");
  const scenario = transactionCompletionVisualSmokeScenarios.find((value) => value.id === selectedId) ?? transactionCompletionVisualSmokeScenarios[0];
  const smokeConfig = useMemo<AptConfig>(
    () => ({
      ...config,
      nonLiveCashCaptureEnabled: true,
      centralPmsCashSubmissionEnabled: true,
      centralPmsFiscalIssuanceEnabled: true,
      centralPmsReceiptRetrievalEnabled: true,
      receiptPreviewEnabled: true,
      receiptPrintingEnabled: false,
      centralPmsConnectionMode: "mock",
      centralPmsBaseUrl: "http://127.0.0.1:5180",
    }),
    [config],
  );
  const context = useMemo(() => buildTerminalContext(smokeConfig), [smokeConfig]);
  const session = useMemo(() => buildCompletionSession(smokeConfig, scenario), [smokeConfig, scenario]);
  const bridge = useMemo(() => createTransactionCompletionVisualSmokeBridge(scenario), [scenario]);

  return (
    <main
      className="terminal-shell receipt-visual-smoke-shell"
      data-testid="apt-terminal-shell"
      data-surface="transaction-completion-visual-smoke"
      data-app-ready="true"
    >
      <header className="brand-header">
        <div>
          <p className="eyebrow">Development fixture</p>
          <h1>Transaction Completion Visual Smoke</h1>
        </div>
        <span className="status-badge warning">Development-only</span>
      </header>

      <section className="visual-smoke-selector" aria-label="Transaction completion visual smoke scenarios">
        {transactionCompletionVisualSmokeScenarios.map((value) => (
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

      <section className="status-notice info" role="status" aria-label="Selected transaction completion visual smoke scenario">
        <h2>{scenario.label}</h2>
        <p>{scenario.expectedPosture}</p>
        <p>ExitAuthorization readback scenarios are excluded because no APT-usable Central PMS ExitAuthorization readback contract is present.</p>
      </section>

      <div className="resolved-workflow">
        <section className="session-summary" aria-label="Controlled completed-session basis">
          <div className="amount-band">
            <div>
              <p className="eyebrow">Controlled payable basis</p>
              <strong>PHP 125.00</strong>
            </div>
            <span className="status-badge">Post-cash fixture</span>
          </div>
          <dl className="summary-primary">
            <div className="summary-row"><dt>Ticket reference</dt><dd>VISUAL-COMPLETE-1001</dd></div>
            <div className="summary-row"><dt>Parking session</dt><dd>Authoritatively resolved</dd></div>
            <div className="summary-row"><dt>Cash custody</dt><dd>Durable record restored</dd></div>
          </dl>
        </section>
        <CashCapturePanel
          config={smokeConfig}
          context={context}
          session={session}
          tariffExpired={false}
          cashAcceptanceReady
          bridge={bridge}
          developmentFixtureLocalCashTenderId={terminalCashTenderId}
          autoAdvanceAfterCashReceived={scenario.autoAdvance}
        />
      </div>
    </main>
  );
}

const now = "2026-07-27T08:00:00.000Z";
const terminalCashTenderId = "eeeeeeee-eeee-4eee-8eee-eeeeeeee9001";
const parkingSessionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa9001";

function buildCompletionSession(config: AptConfig, scenario: TransactionCompletionScenario): ResolveVendorParkingResponse {
  const expires = "2026-07-27T09:00:00.000Z";
  return {
    parkingSessionId,
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd9001",
    siteGroupId: config.siteGroupId,
    siteId: config.siteId,
    siteGroupName: "Visual Smoke Site Group",
    siteName: config.siteName,
    lookupOutcome: "RESOLVED",
    plateNumber: "APT-2026",
    ticketReference: "VISUAL-COMPLETE-1001",
    entryTimestamp: "2026-07-27T06:00:00.000Z",
    entryTime: "2026-07-27T06:00:00.000Z",
    tariffCalculatedAt: now,
    currentFeeCalculationTime: now,
    authoritativeAmountMinorUnits: 12500,
    netPayableMinorUnits: 12500,
    currency: "PHP",
    tariffValidUntil: expires,
    tariffExpiresAt: expires,
    feeValidUntil: expires,
    parkingStatus: "ACTIVE",
    paymentStatus: "UNPAID",
    statutoryDiscountApplied: false,
    vendorSystemId: config.vendorSystemId,
    sessionReadiness: "RESOLVED_PAYABLE",
    tariffReadiness: "CURRENT",
    paymentEligibility: "ELIGIBLE",
    terminalCashAvailability: "AVAILABLE",
    fiscalReadiness: "READY",
    salesInvoiceConfigurationReadiness: "READY",
    cashAcceptanceReadiness: "READY",
    readyForCashAcceptance: true,
    blockingReasonCodes: [],
    retryable: false,
    safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
    correlationId: `transaction-completion-visual-smoke:${scenario.id}`,
  };
}

export function createTransactionCompletionVisualSmokeBridge(scenario: TransactionCompletionScenario): LocalJournalBridge {
  const tender: CashTenderSnapshot = {
    id: terminalCashTenderId,
    cashCustodySessionId: "session-visual-completion",
    parkingSessionId,
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd9001",
    currency: "PHP",
    amountDue: 125,
    amountTendered: 150,
    changeDue: 25,
    correlationId: "corr-local-cash-received",
    localIdempotencyIdentity: "visual-completion",
    currentLocalState: "CashReceived",
    createdAt: now,
    updatedAt: now,
  };
  const payment = paymentCommandFor(scenario.id, tender);
  const fiscal = fiscalCommandFor(scenario.id, tender);
  const receipt = receiptCommandFor(scenario.id, tender);

  return {
    health: async (correlationId) => success("localJournal.health", correlationId, {
      healthy: true,
      enabled: true,
      databasePath: "D:\\Temp\\transaction-completion-visual-smoke.db",
      cashDrawerEnabled: false,
      authorityWarning: "Development fixture; no live Central PMS, HikCentral, gate, or printer call is executed.",
    }),
    createOrGetDevelopmentSession: async (correlationId) => success("localJournal.createOrGetDevelopmentSession", correlationId, {
      id: tender.cashCustodySessionId,
      cashierId: "cashier",
      authenticatedCashierSessionReference: "auth",
      cashierShiftId: "shift",
      terminalId: "terminal",
      siteId: "site",
      siteGroupId: "site-group",
      posServerId: "pos",
      openingCashAmount: 0,
      openedAt: now,
      status: "Open",
    }),
    startTender: async (correlationId) => success("localJournal.startTender", correlationId, tender),
    recordCashReceived: async (correlationId, _payload: RecordCashReceivedPayload) => success("localJournal.recordCashReceived", correlationId, tender),
    readTenderByParkingSession: async (correlationId) => success("localJournal.readTenderByParkingSession", correlationId, { tender, events: [] }),
    getCentralPmsCashSubmissionStatus: async (correlationId) => success("centralPmsCashSubmission.getStatus", correlationId, statusEnvelope(payment)),
    submitOrReadbackCentralPmsCashSubmission: async (correlationId) => success("centralPmsCashSubmission.submitOrReadback", correlationId, statusEnvelope(payment ?? acceptedPayment(tender))),
    getCentralPmsCashFiscalStatus: async (correlationId) => success("centralPmsCashFiscal.getStatus", correlationId, fiscalEnvelope(fiscal)),
    submitOrReadbackCentralPmsCashFiscal: async (correlationId) => success("centralPmsCashFiscal.submitOrReadback", correlationId, fiscalEnvelope(fiscal ?? recordedFiscal(tender))),
    getCentralPmsCashReceiptStatus: async (correlationId) => success("centralPmsCashReceipt.getStatus", correlationId, receiptEnvelope(receipt)),
    retrieveOrCheckCentralPmsCashReceipt: async (correlationId) => success("centralPmsCashReceipt.retrieveOrCheck", correlationId, receiptEnvelope(receipt ?? availableReceipt(tender))),
    getCentralPmsCashReceiptPreview: async (correlationId) => failure("centralPmsCashReceipt.getPreview", correlationId, "preview_not_needed", "Preview is not needed for transaction-completion visual smoke."),
    getCentralPmsCashReceiptPrintStatus: async (correlationId) => success("centralPmsCashReceiptPrint.getStatus", correlationId, { enabled: false, configurationValid: true, configurationMessage: "Disabled", command: receipt, jobs: [] }),
    submitCentralPmsCashReceiptPrint: async (correlationId) => failure("centralPmsCashReceiptPrint.submit", correlationId, "printing_disabled", "Printing is not part of transaction completion visual smoke."),
    getSalesInvoicePrintHistoryForTender: async (correlationId) => success("salesInvoicePrintHistory.getForTender", correlationId, emptyHistory()),
    getSalesInvoicePrintHistoryForFiscalDocument: async (correlationId) => success("salesInvoicePrintHistory.getForFiscalDocument", correlationId, emptyHistory()),
    getRecentSalesInvoicePrintHistory: async (correlationId) => success("salesInvoicePrintHistory.getRecent", correlationId, emptyHistory()),
    getSalesInvoicePrintHistoryDetail: async (correlationId) => failure("salesInvoicePrintHistory.getDetail", correlationId, "print_history_detail_unavailable", "No print history detail is available."),
  };
}

function success<T>(command: string, correlationId: string, payload: T): BridgeResult<T> {
  return { ok: true, command, correlationId, payload };
}

function failure<T>(command: string, correlationId: string, code: string, message: string): BridgeResult<T> {
  return { ok: false, command, correlationId, error: { code, message } };
}

function statusEnvelope(command: CentralPmsCashSubmissionCommand | null): CentralPmsCashSubmissionStatus {
  return { enabled: true, configurationValid: true, configurationMessage: "Configured", command };
}

function fiscalEnvelope(command: CentralPmsCashFiscalCommand | null): CentralPmsCashFiscalStatus {
  return { enabled: true, configurationValid: true, configurationMessage: "Configured", command };
}

function receiptEnvelope(command: CentralPmsCashReceiptCommand | null): CentralPmsCashReceiptStatus {
  return { enabled: true, configurationValid: true, configurationMessage: "Configured", command };
}

function paymentCommandFor(id: TransactionCompletionScenarioId, tender: CashTenderSnapshot): CentralPmsCashSubmissionCommand | null {
  if (id === "cash-received-awaiting-submission") return null;
  if (id === "submission-retryable") return paymentWithStatus(tender, "RetryPending", "Retryable Central PMS submission");
  if (id === "payment-finality-pending" || id === "restart-payment-pending") return paymentWithStatus(tender, "ReadbackRequired", "Payment finality pending");
  if (id === "terminal-payment-failure") return paymentWithStatus(tender, "Rejected", "Rejected - reconciliation required", "PAYMENT_REJECTED");
  return acceptedPayment(tender);
}

function acceptedPayment(tender: CashTenderSnapshot): CentralPmsCashSubmissionCommand {
  return paymentWithStatus(tender, "Confirmed", "Canonical payment confirmed");
}

function paymentWithStatus(
  tender: CashTenderSnapshot,
  status: CentralPmsCashSubmissionCommand["status"],
  statusLabel: string,
  safeCode: string | null = null,
): CentralPmsCashSubmissionCommand {
  return {
    localCommandId: "payment-command",
    terminalCashTenderId: tender.id,
    cashCustodySessionId: tender.cashCustodySessionId,
    status,
    statusLabel,
    attemptCount: 1,
    originalCorrelationId: "corr-payment",
    resultClassification: status === "Confirmed" ? "CREATED" : null,
    canonicalPaymentAttemptId: status === "Confirmed" ? "payment-attempt" : null,
    canonicalPaymentConfirmationId: status === "Confirmed" ? "payment-confirmation" : null,
    confirmedAt: status === "Confirmed" ? now : null,
    nextRetryAt: status === "RetryPending" ? now : null,
    lastSafeHttpStatus: safeCode ? 409 : null,
    lastSafeErrorCode: safeCode,
    createdAt: now,
    updatedAt: now,
  };
}

function fiscalCommandFor(id: TransactionCompletionScenarioId, tender: CashTenderSnapshot): CentralPmsCashFiscalCommand | null {
  if (["cash-received-awaiting-submission", "submission-retryable", "payment-finality-pending", "restart-payment-pending", "terminal-payment-failure"].includes(id)) return null;
  if (id === "payment-final-fiscal-pending" || id === "restart-fiscal-pending") return fiscalWithStatus(tender, "Pending", "Fiscal issuance pending");
  if (id === "fiscal-retryable") return fiscalWithStatus(tender, "RetryPending", "Fiscal issuance retryable");
  if (id === "terminal-fiscal-failure") return fiscalWithStatus(tender, "Rejected", "Fiscal rejected - reconciliation required", "FISCAL_REJECTED");
  return recordedFiscal(tender);
}

function recordedFiscal(tender: CashTenderSnapshot): CentralPmsCashFiscalCommand {
  return fiscalWithStatus(tender, "Recorded", "Fiscal document recorded");
}

function fiscalWithStatus(
  tender: CashTenderSnapshot,
  status: CentralPmsCashFiscalCommand["status"],
  statusLabel: string,
  safeCode: string | null = null,
): CentralPmsCashFiscalCommand {
  return {
    localFiscalCommandId: "fiscal-command",
    terminalCashTenderId: tender.id,
    relatedCashPaymentOutboxCommandId: "payment-command",
    canonicalPaymentAttemptId: "payment-attempt",
    canonicalPaymentConfirmationId: "payment-confirmation",
    status,
    statusLabel,
    attemptCount: 1,
    fiscalCorrelationId: "corr-fiscal",
    resultClassification: status === "Recorded" ? "NEWLY_CREATED" : null,
    fiscalIssuanceReferenceId: status === "Recorded" ? "fiscal-reference" : null,
    fiscalIssuanceState: status === "Recorded" ? "FISCAL_ISSUANCE_RECORDED" : "FISCAL_ISSUANCE_PENDING",
    posFiscalDocumentId: status === "Recorded" ? "pos-fiscal-document" : null,
    fiscalDocumentNumber: status === "Recorded" ? "SI-000001" : null,
    fiscalNumberAssignedAt: status === "Recorded" ? now : null,
    semanticHashSourceVersion: "v1",
    recordedAt: status === "Recorded" ? now : null,
    nextRetryAt: status === "RetryPending" ? now : null,
    lastSafeHttpStatus: safeCode ? 409 : null,
    lastSafeErrorCode: safeCode,
    createdAt: now,
    updatedAt: now,
  };
}

function receiptCommandFor(id: TransactionCompletionScenarioId, tender: CashTenderSnapshot): CentralPmsCashReceiptCommand | null {
  if (!["fiscal-recorded-receipt-unavailable", "receipt-available", "receipt-malformed", "restart-receipt-available"].includes(id)) return null;
  if (id === "fiscal-recorded-receipt-unavailable") return receiptWithStatus(tender, "Unavailable", "Sales Invoice is temporarily unavailable", "POS_SERVER_RECEIPT_PRESENTATION_UNAVAILABLE");
  if (id === "receipt-malformed") return receiptWithStatus(tender, "Malformed", "Sales Invoice response could not be read", "POS_SERVER_RECEIPT_PRESENTATION_MALFORMED");
  return availableReceipt(tender);
}

function availableReceipt(tender: CashTenderSnapshot): CentralPmsCashReceiptCommand {
  return receiptWithStatus(tender, "Available", "Receipt presentation available");
}

function receiptWithStatus(
  tender: CashTenderSnapshot,
  status: CentralPmsCashReceiptCommand["status"],
  statusLabel: string,
  safeCode: string | null = null,
): CentralPmsCashReceiptCommand {
  return {
    localReceiptRetrievalId: "receipt-command",
    terminalCashTenderId: tender.id,
    relatedCashPaymentOutboxCommandId: "payment-command",
    relatedFiscalCommandId: "fiscal-command",
    canonicalPaymentAttemptId: "payment-attempt",
    canonicalPaymentConfirmationId: "payment-confirmation",
    canonicalPaymentStatus: "CONFIRMED",
    fiscalIssuanceReferenceId: "fiscal-reference",
    posFiscalDocumentId: "pos-fiscal-document",
    status,
    statusLabel,
    attemptCount: 1,
    retrievalCorrelationId: "corr-receipt",
    resultClassification: status === "Available" ? "AVAILABLE" : null,
    receiptAvailabilityState: status === "Available" ? "AVAILABLE" : null,
    fiscalDocumentNumber: "SI-000001",
    fiscalDocumentStatus: "recorded",
    presentationVersion: status === "Available" ? "digital-sales-invoice-presentation-json-v1" : null,
    templateVersion: status === "Available" ? "digital-sales-invoice-json-v1" : null,
    semanticRequestHash: status === "Available" ? "sha256:fiscal-semantic" : null,
    semanticRequestHashVersion: status === "Available" ? "v1" : null,
    semanticRequestHashStatus: status === "Available" ? "MATCHED" : null,
    contentType: status === "Available" ? "application/json" : null,
    authoritativePayloadHash: status === "Available" ? "sha256:receipt-payload" : null,
    voidStatus: null,
    voidReasonCode: null,
    voidedAt: null,
    retrievedAt: status === "Available" ? now : null,
    nextRetryAt: status === "Unavailable" ? now : null,
    lastSafeHttpStatus: safeCode ? 503 : null,
    lastSafeErrorCode: safeCode,
    lastRetryable: status === "Unavailable",
    lastCentralPmsCorrelationId: "corr-central-pms",
    lastUpdatedFromCentralPms: now,
    createdAt: now,
    updatedAt: now,
  };
}

function emptyHistory() {
  return {
    scope: "terminalCashTenderId",
    summary: {
      hasHistory: false,
      originalStatus: "No print attempts recorded",
      reprintCount: 0,
      latestCopySequence: null,
      latestStatus: "No print attempts recorded",
      latestPrinterName: null,
      latestPaperWidthMm: null,
      latestAttemptAt: null,
      requiresConfirmation: false,
      attentionRequired: false,
    },
    jobs: [],
    indicators: [],
  };
}
