import { useMemo, useState, type ReactNode } from "react";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import type { PayableBasisResponse, StatutoryDiscountWorkflowState } from "./api/centralPmsTypes";
import type { AptConfig } from "./config";

export type StatutoryDiscountVisualSmokeScenarioId =
  | "none"
  | "draft"
  | "awaiting-review"
  | "approved-application-not-requested"
  | "application-processing"
  | "applied-complete"
  | "applied-amount-changed"
  | "rejected"
  | "retryable-decision-failure"
  | "retryable-application-failure"
  | "terminal-failure"
  | "required-facts-unavailable"
  | "restart-awaiting-review"
  | "restart-after-approval"
  | "restart-application-processing"
  | "restart-applied-amount-change";


export type StatutoryDiscountVisualSmokeRenderArgs = {
  config: AptConfig;
  client: MockCentralPmsClient;
  initialResolvedBasis: PayableBasisResponse;
  initialStatutoryState: StatutoryDiscountWorkflowState;
  renderKey: string;
};
export type StatutoryDiscountVisualSmokeScenario = {
  id: StatutoryDiscountVisualSmokeScenarioId;
  label: string;
  expectedPosture: string;
  state: StatutoryDiscountWorkflowState;
};

const decisionId = "77777777-7777-4777-8777-777777770777";
const applicationId = "88888888-8888-4888-8888-888888880001";
const originalSnapshot = "dddddddd-dddd-4ddd-8ddd-dddddddd1001";
const appliedSnapshot = "99999999-9999-4999-8999-999999990001";

export const statutoryDiscountVisualSmokeScenarios: StatutoryDiscountVisualSmokeScenario[] = [
  { id: "none", label: "No statutory request", expectedPosture: "Original payable basis is visible; no statutory request is active.", state: { status: "none" } },
  { id: "draft", label: "Draft statutory request", expectedPosture: "Safe entitlement facts can be submitted for Operator Console review.", state: baseState("draft") },
  { id: "awaiting-review", label: "Awaiting review", expectedPosture: "Pending review is durable; status checks use GET only.", state: baseState("awaiting_review", { payableBasisReadinessStatus: "AWAITING_REVIEW", payableBasisReadinessAction: "POLL_READBACK" }) },
  { id: "approved-application-not-requested", label: "Approved, application not requested", expectedPosture: "Operator Console approved the request; application intent is available.", state: baseState("approved_application_not_requested", { decisionResultStatus: "APPROVED", payableBasisReadinessStatus: "DECISION_APPROVED_APPLICATION_NOT_REQUESTED", payableBasisReadinessAction: "SUBMIT_APPLICATION_INTENT" }) },
  { id: "application-processing", label: "Application processing", expectedPosture: "Application command exists and remains pending without another POST.", state: baseState("application_processing", { statutoryDiscountPayableBasisApplicationCommandId: applicationId, applicationCommandStatus: "PROCESSING", payableBasisReadinessStatus: "APPLICATION_PROCESSING", payableBasisReadinessAction: "POLL_READBACK" }) },
  { id: "applied-complete", label: "Applied complete", expectedPosture: "Applied snapshot, final amount, VAT, and discount facts are visible after amount acknowledgement.", state: appliedState(true) },
  { id: "applied-amount-changed", label: "Applied amount changed", expectedPosture: "Applied basis differs from the original basis and requires amount acknowledgement.", state: appliedState(false) },
  { id: "rejected", label: "Rejected", expectedPosture: "Rejected decision is terminal for this statutory request.", state: baseState("rejected", { decisionResultStatus: "REJECTED", payableBasisReadinessStatus: "DECISION_REJECTED", payableBasisReadinessAction: "DO_NOT_RETRY", safeErrorCode: "STATUTORY_DISCOUNT_DECISION_REJECTED" }) },
  { id: "retryable-decision-failure", label: "Retryable decision failure", expectedPosture: "Retry guidance preserves the original idempotency key.", state: baseState("retryable_failure", { retryable: true, recoveryClassification: "RETRY_ORIGINAL_IDEMPOTENCY_KEY", recoveryAction: "WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY", safeErrorCode: "STATUTORY_DISCOUNT_RETRYABLE_FAILURE", payableBasisReadinessStatus: "RETRYABLE_FAILURE" }) },
  { id: "retryable-application-failure", label: "Retryable application failure", expectedPosture: "Application recovery uses original application idempotency.", state: baseState("retryable_failure", { statutoryDiscountPayableBasisApplicationCommandId: applicationId, retryable: true, recoveryClassification: "RETRY_ORIGINAL_IDEMPOTENCY_KEY", recoveryAction: "WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY", safeErrorCode: "STATUTORY_DISCOUNT_RETRYABLE_FAILURE", payableBasisReadinessStatus: "RETRYABLE_FAILURE" }) },
  { id: "terminal-failure", label: "Terminal failure", expectedPosture: "Support-required posture with no blind retry.", state: baseState("terminal_failure", { safeErrorCode: "STATUTORY_DISCOUNT_TERMINAL_FAILURE", payableBasisReadinessStatus: "TERMINAL_FAILURE", payableBasisReadinessAction: "DO_NOT_RETRY" }) },
  { id: "required-facts-unavailable", label: "Required facts unavailable", expectedPosture: "Missing canonical facts are shown as unavailable, never zero.", state: baseState("required_facts_unavailable", { statutoryDiscountPayableBasisApplicationCommandId: applicationId, applicationCommandStatus: "PROCESSING", safeErrorCode: "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE", payableBasisReadinessStatus: "REQUIRED_FACTS_UNAVAILABLE", payableBasisReadinessAction: "DO_NOT_RETRY" }) },
  { id: "restart-awaiting-review", label: "Restart awaiting review", expectedPosture: "Restart restores the decision ID and remains pre-cash.", state: { ...baseState("awaiting_review", { payableBasisReadinessStatus: "AWAITING_REVIEW", payableBasisReadinessAction: "POLL_READBACK" }), restoredAfterRestart: true } },
  { id: "restart-after-approval", label: "Restart after approval", expectedPosture: "Restart restores approval before application intent.", state: { ...baseState("approved_application_not_requested", { decisionResultStatus: "APPROVED", payableBasisReadinessStatus: "DECISION_APPROVED_APPLICATION_NOT_REQUESTED", payableBasisReadinessAction: "SUBMIT_APPLICATION_INTENT" }), restoredAfterRestart: true } },
  { id: "restart-application-processing", label: "Restart during application processing", expectedPosture: "Restart preserves application command and uses GET readback.", state: { ...baseState("application_processing", { statutoryDiscountPayableBasisApplicationCommandId: applicationId, applicationCommandStatus: "PROCESSING", payableBasisReadinessStatus: "APPLICATION_PROCESSING", payableBasisReadinessAction: "POLL_READBACK" }), restoredAfterRestart: true } },
  { id: "restart-applied-amount-change", label: "Restart after applied amount change", expectedPosture: "Restart preserves applied amount evidence before statutory cash is authorized.", state: { ...appliedState(false), restoredAfterRestart: true } },
];

export function shouldUseStatutoryDiscountVisualSmoke(
  search: string,
  isDevelopment = (import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV === true,
): boolean {
  return isDevelopment && new URLSearchParams(search).get("statutoryDiscountVisualSmoke") === "1";
}

export function StatutoryDiscountVisualSmokeShell({ config, renderTerminalShell }: { config: AptConfig; renderTerminalShell: (args: StatutoryDiscountVisualSmokeRenderArgs) => ReactNode }) {
  const [selectedId, setSelectedId] = useState<StatutoryDiscountVisualSmokeScenarioId>("none");
  const scenario = statutoryDiscountVisualSmokeScenarios.find((value) => value.id === selectedId) ?? statutoryDiscountVisualSmokeScenarios[0];
  const smokeConfig = useMemo<AptConfig>(() => ({
    ...config,
    centralPmsConnectionMode: "mock",
    nonLiveCashCaptureEnabled: false,
    centralPmsCashSubmissionEnabled: false,
    centralPmsFiscalIssuanceEnabled: false,
    centralPmsReceiptRetrievalEnabled: false,
    receiptPreviewEnabled: false,
    receiptPrintingEnabled: false,
  }), [config]);
  const basis = useMemo(() => basisForScenario(smokeConfig, scenario), [smokeConfig, scenario]);
  const client = useMemo(() => new MockCentralPmsClient(smokeConfig), [smokeConfig]);

  return (
    <main className="terminal-shell receipt-visual-smoke-shell" data-testid="apt-terminal-shell" data-surface="statutory-discount-visual-smoke" data-app-ready="true">
      <header className="brand-header">
        <div>
          <p className="eyebrow">Development fixture</p>
          <h1>Statutory Discount Visual Smoke</h1>
        </div>
        <span className="status-badge warning">Development-only</span>
      </header>
      <section className="status-notice info" role="status" aria-label="Statutory discount visual smoke notice">
        <h2>{scenario.label}</h2>
        <p>{scenario.expectedPosture}</p>
        <p>Statutory CASH_RECEIVED is not enabled in this slice.</p>
        <p>No live Central PMS, HikCentral, fiscal, receipt, ExitAuthorization, gate, or cash-drawer command is executed.</p>
      </section>
      <section className="visual-smoke-selector" aria-label="Statutory discount visual smoke scenarios">
        {statutoryDiscountVisualSmokeScenarios.map((value) => (
          <button key={value.id} type="button" className={value.id === selectedId ? "secondary-action selected" : "secondary-action"} aria-pressed={value.id === selectedId} onClick={() => setSelectedId(value.id)}>
            {value.label}
          </button>
        ))}
      </section>
      {renderTerminalShell({
        config: smokeConfig,
        client,
        initialResolvedBasis: basis,
        initialStatutoryState: scenario.state,
        renderKey: scenario.id,
      })}
    </main>
  );
}

function baseState(status: StatutoryDiscountWorkflowState["status"], overrides: Partial<StatutoryDiscountWorkflowState> = {}): StatutoryDiscountWorkflowState {
  return {
    status,
    entitlementType: "SENIOR_CITIZEN",
    maskedIdReference: "SC-****-0001",
    idDocumentType: "OSCA_ID",
    issuingAuthority: "OSCA",
    requesterAttested: true,
    requestReference: "99999999-9999-4999-8999-999999990001",
    decisionIdempotencyKey: "apt-statutory-decision:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001:SENIOR_CITIZEN",
    applicationIdempotencyKey: "apt-statutory-application:77777777-7777-4777-8777-777777770777",
    statutoryDiscountDecisionCommandId: decisionId,
    decisionStatus: "AWAITING_REVIEW",
    decisionResultStatus: "NOT_DECIDED",
    applicationCommandStatus: "NOT_REQUESTED",
    applicationResultClassification: "NOT_REQUESTED",
    retryable: false,
    recoveryClassification: "NONE",
    recoveryAction: overrides.payableBasisReadinessAction ?? null,
    originalTariffSnapshotId: originalSnapshot,
    originalAmountMinorUnits: 12500,
    payableBasisReady: false,
    payableBasisReadinessStatus: "AWAITING_REVIEW",
    payableBasisReadinessAction: "POLL_READBACK",
    currency: "PHP",
    correlationId: "statutory-smoke-correlation",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastReadbackAt: new Date().toISOString(),
    ...overrides,
  };
}

function appliedState(amountAcknowledged: boolean): StatutoryDiscountWorkflowState {
  return baseState("applied", {
    decisionStatus: "COMPLETED",
    decisionResultStatus: "APPROVED",
    statutoryDiscountPayableBasisApplicationCommandId: applicationId,
    applicationCommandStatus: "APPLIED",
    applicationResultClassification: "APPLIED",
    appliedTariffSnapshotId: appliedSnapshot,
    vatExclusiveBasisAmountMinorUnits: 8929,
    vatAmountMinorUnits: 1071,
    vatTreatment: "VAT_EXEMPT_WITH_DISCOUNT",
    statutoryDiscountAmountMinorUnits: 2500,
    finalPayableAmountMinorUnits: 10000,
    payableBasisReady: true,
    payableBasisReadinessStatus: "APPLIED",
    payableBasisReadinessAction: null,
    amountAcknowledged,
  });
}

function basisForScenario(config: AptConfig, scenario: StatutoryDiscountVisualSmokeScenario): PayableBasisResponse {
  const applied = scenario.state.status === "applied";
  const blocker = statutoryBlockerForScenario(scenario.state);
  const amount = applied && scenario.state.finalPayableAmountMinorUnits != null ? scenario.state.finalPayableAmountMinorUnits : 12500;
  const snapshot = applied && scenario.state.appliedTariffSnapshotId ? scenario.state.appliedTariffSnapshotId : originalSnapshot;
  const now = new Date();
  const validUntil = new Date(now.getTime() + 20 * 60 * 1000).toISOString();
  return {
    operation: "resolve",
    revalidationOutcome: null,
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: snapshot,
    siteGroupId: config.siteGroupId,
    siteId: config.siteId,
    sitePosServerId: config.posServerId,
    terminalId: config.terminalId,
    siteGroupName: "ExitPass Development Group",
    siteName: config.siteName,
    ticketReference: "APT-ACTIVE-1001",
    plateNumber: "NCR-4421",
    entryTimestamp: new Date(now.getTime() - 2 * 60 * 60 * 1000).toISOString(),
    parkingStatus: "Active",
    paymentStatus: "Unpaid",
    authoritativeAmountMinorUnits: amount,
    netPayableMinorUnits: amount,
    currency: scenario.state.currency ?? "PHP",
    tariffCalculatedAt: now.toISOString(),
    tariffValidUntil: validUntil,
    feeValidUntil: validUntil,
    vendorSystemId: config.vendorSystemId,
    statutoryDiscountApplied: applied,
    statutoryDiscountValidationId: "66666666-6666-4666-8666-666666660001",
    statutoryDiscountApplicationId: scenario.state.statutoryDiscountPayableBasisApplicationCommandId ?? null,
    statutoryDiscountReadiness: scenario.state.status === "none" ? null : {
      applicable: true,
      ready: applied,
      statutoryDiscountDecisionCommandId: scenario.state.statutoryDiscountDecisionCommandId ?? null,
      statutoryDiscountPayableBasisApplicationCommandId: scenario.state.statutoryDiscountPayableBasisApplicationCommandId ?? null,
      entitlementType: scenario.state.entitlementType ?? null,
      decisionStatus: scenario.state.decisionStatus ?? null,
      decisionResultStatus: scenario.state.decisionResultStatus ?? null,
      decisionCommandStatus: scenario.state.decisionStatus ?? null,
      applicationCommandStatus: scenario.state.applicationCommandStatus ?? null,
      applicationResultClassification: scenario.state.applicationResultClassification ?? null,
      payableBasisReady: Boolean(scenario.state.payableBasisReady),
      payableBasisReadinessStatus: scenario.state.payableBasisReadinessStatus ?? "NOT_READY",
      payableBasisReadinessAction: scenario.state.payableBasisReadinessAction ?? null,
      originalTariffSnapshotId: scenario.state.originalTariffSnapshotId ?? null,
      appliedTariffSnapshotId: scenario.state.appliedTariffSnapshotId ?? null,
      originalAmountMinorUnits: scenario.state.originalAmountMinorUnits ?? null,
      vatExclusiveBasisAmountMinorUnits: scenario.state.vatExclusiveBasisAmountMinorUnits ?? null,
      vatAmountMinorUnits: scenario.state.vatAmountMinorUnits ?? null,
      vatTreatment: scenario.state.vatTreatment ?? null,
      statutoryDiscountAmountMinorUnits: scenario.state.statutoryDiscountAmountMinorUnits ?? null,
      finalPayableAmountMinorUnits: scenario.state.finalPayableAmountMinorUnits ?? null,
      currency: scenario.state.currency ?? null,
      retryable: Boolean(scenario.state.retryable),
      recoveryClassification: scenario.state.recoveryClassification ?? null,
      recoveryAction: scenario.state.recoveryAction ?? null,
      safeErrorCode: scenario.state.safeErrorCode ?? null,
      blockingReasonCode: blocker,
      message: scenario.state.payableBasisReadinessStatus ?? scenario.state.status,
    },
    originalTariffSnapshotId: applied ? originalSnapshot : null,
    effectiveTariffSnapshotId: snapshot,
    appliedTariffSnapshotId: applied ? appliedSnapshot : null,
    policyResolutionBasis: applied ? "CENTRAL_PMS_STATUTORY_POLICY" : null,
    benefitType: scenario.state.entitlementType ?? null,
    readinessDimensions: null,
    sessionReadiness: "RESOLVED_PAYABLE",
    tariffReadiness: "CURRENT",
    paymentEligibility: "ELIGIBLE",
    terminalCashAvailability: "AVAILABLE",
    fiscalReadiness: "READY",
    salesInvoiceConfigurationReadiness: "READY",
    cashAcceptanceReadiness: applied || scenario.state.status === "none" ? "READY" : "BLOCKED",
    readyForCashAcceptance: applied || scenario.state.status === "none",
    blockingReasonCodes: applied || scenario.state.status === "none" ? [] : [blocker],
    retryable: Boolean(scenario.state.retryable),
    safeUserFacingClassification: applied || scenario.state.status === "none" ? "READY_FOR_CASH_ACCEPTANCE" : "STATUTORY_DISCOUNT_BLOCKED",
    safeMessage: "Controlled statutory discount visual smoke basis.",
    correlationId: scenario.state.correlationId ?? "statutory-smoke-correlation",
  };
}

function statutoryBlockerForScenario(state: StatutoryDiscountWorkflowState): string {
  if (state.safeErrorCode) return state.safeErrorCode;
  switch (state.payableBasisReadinessStatus) {
    case "DECISION_APPROVED_APPLICATION_NOT_REQUESTED":
      return "STATUTORY_DISCOUNT_APPLICATION_NOT_REQUESTED";
    case "APPLICATION_PROCESSING":
      return "STATUTORY_DISCOUNT_APPLICATION_PROCESSING";
    case "DECISION_REJECTED":
      return "STATUTORY_DISCOUNT_DECISION_REJECTED";
    case "RETRYABLE_FAILURE":
      return "STATUTORY_DISCOUNT_RETRYABLE_FAILURE";
    case "TERMINAL_FAILURE":
      return "STATUTORY_DISCOUNT_TERMINAL_FAILURE";
    case "REQUIRED_FACTS_UNAVAILABLE":
      return "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE";
    case "AWAITING_REVIEW":
    default:
      return "STATUTORY_DISCOUNT_AWAITING_REVIEW";
  }
}


