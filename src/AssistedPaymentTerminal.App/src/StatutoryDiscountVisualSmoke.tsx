import { useMemo, useState, type ReactNode } from "react";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import type { CentralPmsClient, CentralPmsResult, PayableBasisResponse, StatutoryDiscountWorkflowState } from "./api/centralPmsTypes";
import type { AptConfig } from "./config";
import type { CashTenderSnapshot, CentralPmsCashSubmissionStatus } from "./localJournalBridge";
import { createPayableBasisVisualSmokeBridge, type PayableBasisVisualSmokeBridge } from "./PayableBasisVisualSmoke";

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
  | "restart-applied-amount-change"
  | "applied-local-prerequisites-blocked"
  | "applied-continue-enabled"
  | "continue-revalidation-passed"
  | "continue-revalidation-amount-changed"
  | "continue-revalidation-statutory-blocked"
  | "record-cash-revalidation-passed"
  | "record-cash-revalidation-amount-changed"
  | "record-cash-revalidation-retryable"
  | "record-cash-revalidation-terminal"
  | "statutory-cash-received-once"
  | "restart-before-statutory-cash-received"
  | "restart-after-statutory-cash-received-evidence"
  | "restart-after-statutory-cash-received-submission"
  | "non-statutory-cash-flow-unchanged";


export type StatutoryDiscountVisualSmokeRenderArgs = {
  config: AptConfig;
  client: CentralPmsClient;
  initialResolvedBasis: PayableBasisResponse;
  initialStatutoryState: StatutoryDiscountWorkflowState;
  bridge: PayableBasisVisualSmokeBridge;
  initialCashEntryRequested?: boolean;
  renderKey: string;
};
export type StatutoryDiscountVisualSmokeScenario = {
  id: StatutoryDiscountVisualSmokeScenarioId;
  label: string;
  expectedPosture: string;
  state: StatutoryDiscountWorkflowState;
  localCashCaptureEnabled?: boolean;
  referenceValue?: string;
  postCashRecovery?: "evidence" | "submission";
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
  { id: "applied-local-prerequisites-blocked", label: "APPLIED complete but local prerequisites blocked", expectedPosture: "Central PMS is ready, but local terminal prerequisites still restrict cash.", state: appliedState(true), localCashCaptureEnabled: false },
  { id: "applied-continue-enabled", label: "APPLIED complete and Continue to Cash enabled", expectedPosture: "Applied statutory basis, acknowledgement, Central PMS readiness, and local prerequisites allow Continue to Cash.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "continue-revalidation-passed", label: "Continue to Cash revalidation PASSED_UNCHANGED", expectedPosture: "Continue to Cash runs statutory-aware revalidation before opening cash entry.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "continue-revalidation-amount-changed", label: "Continue to Cash revalidation AMOUNT_CHANGED", expectedPosture: "Continue to Cash returns to amount acknowledgement when Central PMS changes the applied amount.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "continue-revalidation-statutory-blocked", label: "Continue to Cash statutory blocked", expectedPosture: "Statutory-aware revalidation blocks cash when Central PMS reports statutory readiness blocked.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "record-cash-revalidation-passed", label: "Immediate Record Cash Received revalidation PASSED_UNCHANGED", expectedPosture: "Record Cash Received performs a second statutory-aware revalidation before local custody is recorded.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "record-cash-revalidation-amount-changed", label: "Immediate Record Cash Received revalidation AMOUNT_CHANGED", expectedPosture: "The second revalidation blocks CASH_RECEIVED when the amount changes.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "record-cash-revalidation-retryable", label: "Immediate Record Cash Received retryable failure", expectedPosture: "The second revalidation blocks CASH_RECEIVED on retryable Central PMS failure.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "record-cash-revalidation-terminal", label: "Immediate Record Cash Received terminal failure", expectedPosture: "The second revalidation blocks CASH_RECEIVED on terminal readback failure.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "statutory-cash-received-once", label: "Statutory CASH_RECEIVED recorded once", expectedPosture: "Successful statutory cash recording creates one local custody event and one tender identity.", state: appliedState(true), localCashCaptureEnabled: true },
  { id: "restart-before-statutory-cash-received", label: "Restart before statutory CASH_RECEIVED requires revalidation", expectedPosture: "Restart restores applied statutory basis but still requires fresh immediate revalidation before cash.", state: { ...appliedState(true), restoredAfterRestart: true }, localCashCaptureEnabled: true },
  { id: "restart-after-statutory-cash-received-evidence", label: "Restart after statutory CASH_RECEIVED preserves custody evidence", expectedPosture: "Restart after CASH_RECEIVED preserves applied snapshot, final amount, and statutory references.", state: { ...appliedState(true), restoredAfterRestart: true }, localCashCaptureEnabled: true, postCashRecovery: "evidence" },
  { id: "restart-after-statutory-cash-received-submission", label: "Restart after statutory CASH_RECEIVED resumes terminal-cash submission", expectedPosture: "Existing post-cash submission recovery resumes from the same tender identity.", state: { ...appliedState(true), restoredAfterRestart: true }, localCashCaptureEnabled: true, postCashRecovery: "submission" },
  { id: "non-statutory-cash-flow-unchanged", label: "Non-statutory cash flow unchanged", expectedPosture: "Non-statutory payable basis remains governed by the existing cash workflow.", state: { status: "none" }, localCashCaptureEnabled: true },
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
    centralPmsBaseUrl: scenario.postCashRecovery === "submission" ? "http://127.0.0.1:5179" : config.centralPmsBaseUrl,
    nonLiveCashCaptureEnabled: Boolean(scenario.localCashCaptureEnabled),
    centralPmsCashSubmissionEnabled: scenario.postCashRecovery === "submission",
    centralPmsFiscalIssuanceEnabled: false,
    centralPmsReceiptRetrievalEnabled: false,
    receiptPreviewEnabled: false,
    receiptPrintingEnabled: false,
  }), [config, scenario.localCashCaptureEnabled, scenario.postCashRecovery]);
  const basis = useMemo(() => basisForScenario(smokeConfig, scenario), [smokeConfig, scenario]);
  const client = useMemo(() => clientForScenario(new MockCentralPmsClient(smokeConfig), scenario), [smokeConfig, scenario]);
  const bridge = useMemo(() => {
    const seededBridge = createPayableBasisVisualSmokeBridge(`exitpass-apt-statutory-cash-visual-smoke:${scenario.id}`);
    seededBridge.reset();
    seedPostCashRecovery(seededBridge, scenario);
    return seededBridge;
  }, [scenario]);

  function selectScenario(value: StatutoryDiscountVisualSmokeScenario) {
    setSelectedId(value.id);
  }

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
        <p>Statutory CASH_RECEIVED is enabled only for APPLIED, acknowledged, revalidated, locally ready fixture states.</p>
        <p>No live Central PMS, HikCentral, fiscal, receipt, ExitAuthorization, gate, or cash-drawer command is executed.</p>
      </section>
      <section className="visual-smoke-selector" aria-label="Statutory discount visual smoke scenarios">
        {statutoryDiscountVisualSmokeScenarios.map((value) => (
          <button key={value.id} type="button" className={value.id === selectedId ? "secondary-action selected" : "secondary-action"} aria-pressed={value.id === selectedId} onClick={() => selectScenario(value)}>
            {value.label}
          </button>
        ))}
      </section>
      {renderTerminalShell({
        config: smokeConfig,
        client,
        initialResolvedBasis: basis,
        initialStatutoryState: scenario.state,
        bridge,
        initialCashEntryRequested: Boolean(scenario.postCashRecovery),
        renderKey: scenario.id,
      })}
    </main>
  );
}

function clientForScenario(base: MockCentralPmsClient, scenario: StatutoryDiscountVisualSmokeScenario): CentralPmsClient {
  if (!isActionRevalidationScenario(scenario.id)) {
    return base;
  }

  let revalidationCount = 0;
  return {
    resolvePayableBasis: (...args) => base.resolvePayableBasis(...args),
    revalidatePayableBasis: async (displayedBasis, correlationId) => {
      revalidationCount += 1;
      const queuedOutcome = actionRevalidationOutcome(scenario.id, revalidationCount, displayedBasis, correlationId);
      if (!queuedOutcome) {
        return base.revalidatePayableBasis(displayedBasis, correlationId);
      }

      return queuedOutcome;
    },
    submitStatutoryDiscountDecision: (...args) => base.submitStatutoryDiscountDecision(...args),
    getStatutoryDiscountDecision: (...args) => base.getStatutoryDiscountDecision(...args),
    resolveTicket: (...args) => base.resolveTicket(...args),
    recalculateFee: (...args) => base.recalculateFee(...args),
  };
}

function isActionRevalidationScenario(id: StatutoryDiscountVisualSmokeScenarioId): boolean {
  return id === "continue-revalidation-amount-changed"
    || id === "continue-revalidation-statutory-blocked"
    || id === "record-cash-revalidation-amount-changed"
    || id === "record-cash-revalidation-retryable"
    || id === "record-cash-revalidation-terminal";
}

function actionRevalidationOutcome(
  id: StatutoryDiscountVisualSmokeScenarioId,
  revalidationCount: number,
  displayedBasis: PayableBasisResponse,
  correlationId: string,
): CentralPmsResult | null {
  if (id === "continue-revalidation-amount-changed" && revalidationCount === 1) {
    return statutoryAmountChangedResponse(displayedBasis, correlationId);
  }

  if (id === "continue-revalidation-statutory-blocked" && revalidationCount === 1) {
    return statutoryBlockedResponse(displayedBasis, correlationId);
  }

  if (id === "record-cash-revalidation-amount-changed" && revalidationCount === 2) {
    return statutoryAmountChangedResponse(displayedBasis, correlationId);
  }

  if (id === "record-cash-revalidation-retryable" && revalidationCount === 2) {
    return {
      ok: false,
      kind: "service_unavailable",
      error: {
        errorCode: "VENDOR_PMS_UNAVAILABLE",
        message: "Central PMS could not revalidate the statutory payable basis. Try again before accepting cash.",
        correlationId,
        retryable: true,
      },
    };
  }

  if (id !== "record-cash-revalidation-terminal" || revalidationCount !== 2) {
    return null;
  }

  return {
    ok: true,
    response: {
      ...displayedBasis,
      operation: "revalidate",
      revalidationOutcome: "SESSION_ALREADY_PAID",
      paymentStatus: "Paid",
      readyForCashAcceptance: false,
      cashAcceptanceReadiness: "BLOCKED",
      blockingReasonCodes: ["PAYMENT_ALREADY_FINAL"],
      retryable: false,
      safeUserFacingClassification: "SESSION_ALREADY_PAID",
      safeMessage: "Parking session is already paid.",
      correlationId,
    },
  };
}

function statutoryAmountChangedResponse(displayedBasis: PayableBasisResponse, correlationId: string): CentralPmsResult {
  const changedAmount = displayedBasis.authoritativeAmountMinorUnits + 2500;
  const changedSnapshot = "99999999-9999-4999-8999-999999990002";
  return {
    ok: true,
    response: {
      ...displayedBasis,
      operation: "revalidate",
      revalidationOutcome: "AMOUNT_CHANGED",
      tariffSnapshotId: changedSnapshot,
      effectiveTariffSnapshotId: changedSnapshot,
      appliedTariffSnapshotId: changedSnapshot,
      authoritativeAmountMinorUnits: changedAmount,
      netPayableMinorUnits: changedAmount,
      readyForCashAcceptance: false,
      cashAcceptanceReadiness: "BLOCKED",
      blockingReasonCodes: ["AMOUNT_CHANGED"],
      retryable: false,
      safeUserFacingClassification: "AMOUNT_CHANGED",
      safeMessage: "Parking fee changed before cash acceptance.",
      correlationId,
      statutoryDiscountReadiness: displayedBasis.statutoryDiscountReadiness
        ? {
            ...displayedBasis.statutoryDiscountReadiness,
            ready: true,
            payableBasisReady: true,
            appliedTariffSnapshotId: changedSnapshot,
            finalPayableAmountMinorUnits: changedAmount,
          }
        : displayedBasis.statutoryDiscountReadiness,
    },
  };
}

function statutoryBlockedResponse(displayedBasis: PayableBasisResponse, correlationId: string): CentralPmsResult {
  return {
    ok: true,
    response: {
      ...displayedBasis,
      operation: "revalidate",
      revalidationOutcome: "STATUTORY_DISCOUNT_BLOCKED",
      readyForCashAcceptance: false,
      cashAcceptanceReadiness: "BLOCKED",
      blockingReasonCodes: ["STATUTORY_DISCOUNT_APPLICATION_PROCESSING"],
      retryable: true,
      safeUserFacingClassification: "STATUTORY_DISCOUNT_BLOCKED",
      safeMessage: "Statutory payable basis is being applied.",
      correlationId,
      statutoryDiscountReadiness: displayedBasis.statutoryDiscountReadiness
        ? {
            ...displayedBasis.statutoryDiscountReadiness,
            ready: false,
            payableBasisReady: false,
            applicationCommandStatus: "PROCESSING",
            applicationResultClassification: "PROCESSING",
            payableBasisReadinessStatus: "APPLICATION_PROCESSING",
            payableBasisReadinessAction: "POLL_READBACK",
            recoveryAction: "POLL_READBACK",
            blockingReasonCode: "STATUTORY_DISCOUNT_APPLICATION_PROCESSING",
            message: "Statutory payable basis is being applied.",
          }
        : displayedBasis.statutoryDiscountReadiness,
    },
  };
}

function seedPostCashRecovery(bridge: PayableBasisVisualSmokeBridge, scenario: StatutoryDiscountVisualSmokeScenario) {
  if (!scenario.postCashRecovery) {
    return;
  }

  const tender = statutoryPostCashTender();
  bridge.seedTender(tender);
  bridge.seedCentralPmsCashSubmissionStatus(scenario.postCashRecovery === "submission" ? pendingSubmissionStatus(tender) : null);
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
  const referenceValue = scenario.referenceValue ?? "APT-ACTIVE-1001";
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
    ticketReference: referenceValue,
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

function statutoryPostCashTender(): CashTenderSnapshot {
  const timestamp = new Date().toISOString();
  return {
    id: "statutory-cash-visual-smoke-tender",
    cashCustodySessionId: "statutory-cash-visual-smoke-custody-session",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: appliedSnapshot,
    currency: "PHP",
    amountDue: 100,
    amountTendered: 100,
    changeDue: 0,
    correlationId: "statutory-smoke-correlation",
    localIdempotencyIdentity: "apt-cash-received:statutory-cash-visual-smoke-tender",
    currentLocalState: "CashReceived",
    createdAt: timestamp,
    updatedAt: timestamp,
    statutoryDiscountDecisionCommandId: decisionId,
    statutoryDiscountPayableBasisApplicationCommandId: applicationId,
    statutoryDiscountValidationId: "66666666-6666-4666-8666-666666660001",
    statutoryOriginalTariffSnapshotId: originalSnapshot,
    statutoryAppliedTariffSnapshotId: appliedSnapshot,
    statutoryOriginalAmountMinorUnits: 12500,
    statutoryFinalAmountMinorUnits: 10000,
    statutoryCurrency: "PHP",
    statutoryAmountAcknowledged: true,
    statutoryAmountAcknowledgedAt: timestamp,
    statutoryImmediateRevalidationOutcome: "PASSED_UNCHANGED",
    statutoryImmediateRevalidatedAt: timestamp,
    statutoryCorrelationId: "statutory-smoke-correlation",
    statutoryReadinessStatus: "APPLIED",
    statutoryReadinessAction: null,
  };
}

function pendingSubmissionStatus(tender: CashTenderSnapshot): CentralPmsCashSubmissionStatus {
  const timestamp = new Date().toISOString();
  return {
    enabled: true,
    configurationValid: true,
    configurationMessage: "Controlled statutory visual-smoke Central PMS submission fixture.",
    command: {
      localCommandId: "statutory-cash-submission-command",
      terminalCashTenderId: tender.id,
      cashCustodySessionId: tender.cashCustodySessionId,
      status: "Pending",
      statusLabel: "Submitting cash payment",
      attemptCount: 1,
      originalCorrelationId: "statutory-smoke-correlation",
      resultClassification: null,
      canonicalPaymentAttemptId: null,
      canonicalPaymentConfirmationId: null,
      confirmedAt: null,
      nextRetryAt: null,
      lastSafeHttpStatus: null,
      lastSafeErrorCode: null,
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  };
}


