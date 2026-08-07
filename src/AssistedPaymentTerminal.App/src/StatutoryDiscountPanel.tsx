import { useEffect, useMemo, useState } from "react";
import type {
  CentralPmsClient,
  PayableBasisResponse,
  StatutoryDiscountDecisionResponse,
  StatutoryDiscountDecisionSubmitRequest,
  StatutoryDiscountWorkflowState,
  StatutoryEntitlementType,
  StatutoryOrdinanceAvailabilityResponse,
  StatutoryOrdinanceAvailabilityViewState,
} from "./api/centralPmsTypes";
import { createCorrelationId } from "./correlation";
import { cashierSafeSupportReference } from "./cashierSafeReferences";
import { containsManualStatutoryIdMask, maskStatutoryId } from "./statutoryIdMasking";
import type { TerminalContext } from "./terminalContext";
import { StatutoryEvidencePanel } from "./StatutoryEvidencePanel";
import { createWebViewStatutoryEvidenceBridge, type StatutoryEvidenceBridge } from "./statutoryEvidenceBridge";

const defaultEvidenceBridge = createWebViewStatutoryEvidenceBridge();

export type StatutoryDiscountPanelProps = {
  basis: PayableBasisResponse;
  client: CentralPmsClient;
  context: TerminalContext;
  state: StatutoryDiscountWorkflowState;
  ordinanceAvailability: StatutoryOrdinanceAvailabilityViewState;
  onRetryAvailability: () => void;
  onStateChange: (next: StatutoryDiscountWorkflowState) => void;
  onAppliedBasisReady: (decisionCommandId: string, response: StatutoryDiscountDecisionResponse, nextState: StatutoryDiscountWorkflowState) => Promise<void>;
  evidenceBridge?: StatutoryEvidenceBridge;
};

const defaultDraft = {
  entitlementType: "SENIOR_CITIZEN",
  maskedIdReference: "",
  idDocumentType: "OSCA_ID",
  issuingAuthority: "OSCA",
  expiryDate: "",
  requesterAttested: false,
  attestationNotes: "",
};

export function StatutoryDiscountPanel({ basis, client, context, state, ordinanceAvailability, onRetryAvailability, onStateChange, onAppliedBasisReady, evidenceBridge = defaultEvidenceBridge }: StatutoryDiscountPanelProps) {
  const [draft, setDraft] = useState(() => ({
    ...defaultDraft,
    entitlementType: state.entitlementType ?? defaultDraft.entitlementType,
    maskedIdReference: state.maskedIdReference ?? defaultDraft.maskedIdReference,
    idDocumentType: state.idDocumentType ?? defaultDraft.idDocumentType,
    issuingAuthority: state.issuingAuthority ?? defaultDraft.issuingAuthority,
    expiryDate: state.expiryDate ?? defaultDraft.expiryDate,
    requesterAttested: Boolean(state.requesterAttested),
    attestationNotes: state.attestationNotes ?? defaultDraft.attestationNotes,
  }));
  const [message, setMessage] = useState<string | null>(null);
  const [rawIdInput, setRawIdInput] = useState("");
  const [statutoryIdEditing, setStatutoryIdEditing] = useState(false);
  const active = state.status !== "none";
  const status = state.status;
  const decisionId = state.statutoryDiscountDecisionCommandId;
  const applicationId = state.statutoryDiscountPayableBasisApplicationCommandId;
  const action = state.payableBasisReadinessAction;
  const availableEntitlements = ordinanceAvailability.status === "ready"
    ? (["SENIOR_CITIZEN", "PWD"] as const).filter((entitlementType) => availabilityFor(ordinanceAvailability, entitlementType).statutoryRequestAllowed)
    : [];
  const selectedEntitlement = asEntitlementType(draft.entitlementType);
  const selectedAvailability = selectedEntitlement && ordinanceAvailability.status === "ready"
    ? availabilityFor(ordinanceAvailability, selectedEntitlement)
    : null;
  const selectedEntitlementAllowed = selectedAvailability?.classification === "AVAILABLE" && selectedAvailability.statutoryRequestAllowed;
  const entitlementOptions = active && selectedEntitlement ? [selectedEntitlement] : availableEntitlements;
  const retryAvailable = ordinanceAvailability.status === "ready" && (ordinanceAvailability.seniorCitizen.retryable || ordinanceAvailability.pwd.retryable);
  const maskedIdFromRawInput = maskStatutoryId(rawIdInput);
  const rawIdHasManualMask = containsManualStatutoryIdMask(rawIdInput);
  const maskedIdForSubmission = rawIdInput.trim() ? maskedIdFromRawInput : draft.maskedIdReference.trim();
  const canSubmitDecision = Boolean(status === "draft" && selectedEntitlementAllowed && draft.requesterAttested && maskedIdForSubmission && !rawIdHasManualMask && draft.entitlementType.trim());
  const canCheckReview = Boolean(decisionId) && ["awaiting_review", "retryable_failure"].includes(status);
  const canSubmitApplication = status === "approved_application_not_requested" && Boolean(decisionId);
  const appliedComplete = status === "applied" && Boolean(state.appliedTariffSnapshotId) && state.finalPayableAmountMinorUnits != null && Boolean(state.currency);
  const statutoryCashReady = appliedComplete && state.amountAcknowledged === true;

  useEffect(() => {
    if ((status === "none" || status === "draft") && availableEntitlements.length > 0 && !availableEntitlements.includes(draft.entitlementType as StatutoryEntitlementType)) {
      setDraft((current) => ({ ...current, entitlementType: availableEntitlements[0] }));
    }
  }, [availableEntitlements.join("|"), draft.entitlementType, status]);

  const facts = useMemo(() => ([
    ["Decision", decisionId ? "Recorded" : "Not recorded"],
    ["Application", applicationId ? "Recorded" : "Not requested"],
    ["Decision status", friendly(state.decisionResultStatus ?? state.decisionStatus ?? status)],
    ["Application status", friendly(state.applicationCommandStatus ?? "NOT_REQUESTED")],
    ["Readiness", friendly(state.payableBasisReadinessStatus ?? "NOT_SUBMITTED")],
    ["Readiness action", friendly(action ?? "No action")],
  ]), [action, applicationId, decisionId, state.applicationCommandStatus, state.decisionResultStatus, state.decisionStatus, state.payableBasisReadinessStatus, status]);

  function updateDraft<K extends keyof typeof draft>(key: K, value: (typeof draft)[K]) {
    setDraft((current) => ({ ...current, [key]: value }));
    if (status === "none") {
      onStateChange({ status: "draft", ...currentStateEvidence(state), [key]: value });
    }
  }

  function startDraft() {
    if (!selectedEntitlementAllowed) {
      setMessage("Central PMS has not allowed a statutory request for the selected entitlement at this Site.");
      return;
    }
    const next = { status: "draft" as const, ...currentStateEvidence(state), ...draft, updatedAt: new Date().toISOString() };
    onStateChange(next);
  }

  function commitStatutoryIdMask() {
    setStatutoryIdEditing(false);
    if (!rawIdInput.trim()) return;
    if (rawIdHasManualMask) {
      setDraft((current) => ({ ...current, maskedIdReference: "" }));
      setMessage("Enter the statutory ID normally without asterisks. Masking is automatic.");
      return;
    }
    setDraft((current) => ({ ...current, maskedIdReference: maskedIdFromRawInput }));
    setMessage(null);
  }

  async function submitDecision(applyPayableBasis: boolean) {
    if (!selectedEntitlementAllowed) {
      setMessage("Authoritative ordinance coverage is required before a statutory request or application can be submitted.");
      return;
    }
    if (!client.submitStatutoryDiscountDecision) {
      setMessage("Central PMS statutory-discount client is unavailable.");
      return;
    }
    if (!maskedIdForSubmission || rawIdHasManualMask) {
      setMessage(rawIdHasManualMask
        ? "Enter the statutory ID normally without asterisks. Masking is automatic."
        : "Enter the statutory ID before submitting the statutory request.");
      return;
    }

    const submissionDraft = { ...draft, maskedIdReference: maskedIdForSubmission };
    setDraft(submissionDraft);
    setRawIdInput("");
    setStatutoryIdEditing(false);

    const correlationId = createCorrelationId();
    const requestReference = state.requestReference ?? createCorrelationId();
    const decisionIdempotencyKey = state.decisionIdempotencyKey ?? `apt-statutory-decision:${basis.parkingSessionId}:${draft.entitlementType}`;
    const applicationIdempotencyKey = state.applicationIdempotencyKey ?? `apt-statutory-application:${state.statutoryDiscountDecisionCommandId ?? requestReference}`;
    const idempotencyKey = applyPayableBasis ? applicationIdempotencyKey : decisionIdempotencyKey;
    const submittingStatus = applyPayableBasis ? "application_submitting" : "submitting";
    const submittingState = {
      ...state,
      status: submittingStatus as StatutoryDiscountWorkflowState["status"],
      entitlementType: submissionDraft.entitlementType,
      maskedIdReference: submissionDraft.maskedIdReference,
      idDocumentType: submissionDraft.idDocumentType,
      issuingAuthority: submissionDraft.issuingAuthority,
      expiryDate: submissionDraft.expiryDate || null,
      requesterAttested: submissionDraft.requesterAttested,
      attestationNotes: submissionDraft.attestationNotes || null,
      requestReference,
      decisionIdempotencyKey,
      applicationIdempotencyKey,
      updatedAt: new Date().toISOString(),
    };
    onStateChange(submittingState);
    setMessage(null);

    const request = buildDecisionRequest({ basis, context, draft: submissionDraft, requestReference, applyPayableBasis });
    const result = await client.submitStatutoryDiscountDecision(request, correlationId, idempotencyKey);
    if (!result.ok) {
      onStateChange({
        ...submittingState,
        status: result.error.retryable ? "retryable_failure" : "terminal_failure",
        retryable: result.error.retryable,
        safeErrorCode: result.error.errorCode,
        correlationId: result.error.correlationId,
        updatedAt: new Date().toISOString(),
      });
      setMessage(result.error.message);
      return;
    }

    const next = mapDecisionResponse(result.response, submittingState);
    onStateChange(next);
    if (isAppliedComplete(next)) {
      await onAppliedBasisReady(result.response.statutoryDiscountDecisionCommandId, result.response, next);
    }
  }

  async function checkStatus() {
    if (!decisionId || !client.getStatutoryDiscountDecision) return;
    const correlationId = createCorrelationId();
    const result = await client.getStatutoryDiscountDecision(decisionId, correlationId);
    if (!result.ok) {
      onStateChange({
        ...state,
        status: result.error.retryable ? "retryable_failure" : "terminal_failure",
        retryable: result.error.retryable,
        safeErrorCode: result.error.errorCode,
        correlationId: result.error.correlationId,
        lastReadbackAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      });
      setMessage(result.error.message);
      return;
    }

    const next = mapDecisionResponse(result.response, state);
    onStateChange(next);
    if (isAppliedComplete(next)) {
      await onAppliedBasisReady(result.response.statutoryDiscountDecisionCommandId, result.response, next);
    }
  }

  return (
    <section className="status-notice info statutory-discount-panel" aria-label="Statutory discount workflow" data-testid="statutory-discount-panel">
      <h3>Statutory discount</h3>
      <p data-testid="statutory-cash-blocker">
        {statutoryCashReady
          ? "Statutory payable basis is ready for Continue to Cash after immediate Central PMS revalidation."
          : "Statutory cash remains blocked until approval, APPLIED payable basis, amount acknowledgement, Central PMS readiness, local prerequisites, and immediate revalidation all pass."}
      </p>
      <section className="ordinance-availability" aria-label="Statutory ordinance availability" data-testid="statutory-ordinance-availability">
        <h4>Site ordinance availability</h4>
        {ordinanceAvailability.status === "idle" && <p>Resolve a parking session before checking statutory parking coverage.</p>}
        {ordinanceAvailability.status === "loading" && <p role="status">Checking authoritative Senior Citizen and PWD coverage for this Site...</p>}
        {ordinanceAvailability.status === "ready" && (
          <>
            {ordinanceAvailability.restoredRefresh && <p>Recovered local state was advisory only. Central PMS was checked again after restart.</p>}
            <dl className="central-pms-details ordinance-coverage-grid">
              <AvailabilityRow label="Senior Citizen" response={ordinanceAvailability.seniorCitizen} testId="senior-citizen-ordinance-availability" />
              <AvailabilityRow label="PWD" response={ordinanceAvailability.pwd} testId="pwd-ordinance-availability" />
            </dl>
            <p data-testid="ordinary-payment-preserved">
              {ordinanceAvailability.seniorCitizen.ordinaryPaymentPreserved && ordinanceAvailability.pwd.ordinaryPaymentPreserved
                ? "Ordinary payment remains available subject to its independent readiness checks."
                : "Ordinary payment readiness must be resolved again with Central PMS."}
            </p>
            {retryAvailable && <button type="button" className="secondary-action" onClick={onRetryAvailability}>Retry ordinance availability</button>}
          </>
        )}
      </section>
      {!active && (
        <div className="statutory-draft-actions">
          {availableEntitlements.length > 0 ? (
            <>
              <label>
                Covered entitlement
                <select data-testid="covered-entitlement-selector" value={draft.entitlementType} onChange={(event) => setDraft((current) => ({ ...current, entitlementType: event.target.value }))}>
                  {availableEntitlements.map((entitlementType) => <option key={entitlementType} value={entitlementType}>{entitlementLabel(entitlementType)}</option>)}
                </select>
              </label>
              <p>No statutory request is active for this payable basis.</p>
              <button type="button" className="secondary-action" onClick={startDraft} disabled={!selectedEntitlementAllowed}>Start statutory request</button>
            </>
          ) : ordinanceAvailability.status === "ready" ? (
            <p data-testid="statutory-request-unavailable">No statutory request option is available for this Site. Ordinary payment remains separate.</p>
          ) : null}
        </div>
      )}

      {(active || status === "draft") && (
        <div className="statutory-grid">
          <label>
            Entitlement type
            <select value={draft.entitlementType} onChange={(event) => updateDraft("entitlementType", event.target.value)} disabled={status !== "draft" && status !== "none"}>
              {entitlementOptions.map((entitlementType) => <option key={entitlementType} value={entitlementType}>{entitlementLabel(entitlementType)}</option>)}
            </select>
          </label>
          <label>
            Statutory ID
            <input
              aria-label="Statutory ID"
              aria-describedby="statutory-id-mask-guidance"
              autoComplete="off"
              spellCheck={false}
              value={statutoryIdEditing ? rawIdInput : (rawIdInput.trim() ? maskedIdFromRawInput : draft.maskedIdReference)}
              onChange={(event) => {
                setRawIdInput(event.target.value);
                setDraft((current) => ({ ...current, maskedIdReference: "" }));
              }}
              onFocus={() => setStatutoryIdEditing(true)}
              onBlur={commitStatutoryIdMask}
              disabled={status !== "draft" && status !== "none"}
            />
            <span id="statutory-id-mask-guidance">Enter the ID normally. The application masks it automatically after entry.</span>
          </label>
          <label>
            ID document type
            <input value={draft.idDocumentType} onChange={(event) => updateDraft("idDocumentType", event.target.value)} disabled={status !== "draft" && status !== "none"} />
          </label>
          <label>
            Issuing authority
            <input value={draft.issuingAuthority} onChange={(event) => updateDraft("issuingAuthority", event.target.value)} disabled={status !== "draft" && status !== "none"} />
          </label>
          <label>
            Expiry date
            <input type="date" value={draft.expiryDate ?? ""} onChange={(event) => updateDraft("expiryDate", event.target.value)} disabled={status !== "draft" && status !== "none"} />
          </label>
          <label className="checkbox-line">
            <input type="checkbox" checked={draft.requesterAttested} onChange={(event) => updateDraft("requesterAttested", event.target.checked)} disabled={status !== "draft" && status !== "none"} />
            Cashier attests safe entitlement facts were presented for Operator Console review.
          </label>
        </div>
      )}

      <div className="statutory-status-card" data-testid="statutory-status-card">
        <strong>{titleForStatus(status)}</strong>
        <p>{descriptionForStatus(status, state)}</p>
        {message && <p className="cash-error">{message}</p>}
        <dl className="central-pms-details">
          {facts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
        </dl>
      </div>

      {appliedComplete && (
        <section className="statutory-applied-facts" aria-label="Applied statutory payable basis" data-testid="statutory-applied-facts">
          <h4>Applied statutory payable basis</h4>
          <dl className="central-pms-details">
            <div><dt>Original amount</dt><dd>{formatCurrency(state.originalAmountMinorUnits, state.currency)}</dd></div>
            <div><dt>VAT-exclusive amount</dt><dd>{formatCurrency(state.vatExclusiveBasisAmountMinorUnits, state.currency)}</dd></div>
            <div><dt>VAT amount</dt><dd>{formatCurrency(state.vatAmountMinorUnits, state.currency)}</dd></div>
            <div><dt>VAT treatment</dt><dd>{friendly(state.vatTreatment)}</dd></div>
            <div><dt>Statutory discount</dt><dd>{formatCurrency(state.statutoryDiscountAmountMinorUnits, state.currency)}</dd></div>
            <div><dt>Final payable amount</dt><dd>{formatCurrency(state.finalPayableAmountMinorUnits, state.currency)}</dd></div>
            <div><dt>Tariff application</dt><dd>Authoritative version applied</dd></div>
          </dl>
        </section>
      )}

      {decisionId && (
        <StatutoryEvidencePanel
          decisionCommandId={decisionId}
          restored={Boolean(state.restoredAfterRestart)}
          recovery={state.evidenceRecovery}
          bridge={evidenceBridge}
          onRecoveryChange={(evidenceRecovery) => onStateChange({
            ...state,
            evidenceRecovery,
            updatedAt: new Date().toISOString(),
          })}
        />
      )}

      <div className="statutory-actions">
        {status === "draft" && <button type="button" className="primary-action" disabled={!canSubmitDecision} onClick={() => void submitDecision(false)}>Submit for Operator Review</button>}
        {canCheckReview && <button type="button" className="secondary-action" onClick={() => void checkStatus()}>Check Review Status</button>}
        {canSubmitApplication && <button type="button" className="primary-action" onClick={() => void submitDecision(true)}>Submit Statutory Application</button>}
        {status === "application_processing" && <button type="button" className="secondary-action" onClick={() => void checkStatus()}>Check Application Status</button>}
      </div>
    </section>
  );
}

function AvailabilityRow({ label, response, testId }: { label: string; response: StatutoryOrdinanceAvailabilityResponse; testId: string }) {
  const supportReference = cashierSafeSupportReference(response.supportReference);
  return (
    <div className="ordinance-coverage-row" data-testid={testId}>
      <dt>{label}</dt>
      <dd>
        <strong>{friendly(response.classification)}</strong>
        <span>{response.safeMessage}</span>
        <span>Evaluated: {formatDate(response.evaluatedAt)}</span>
        {supportReference && <span>Support reference: {supportReference}</span>}
        <span>Retryable: {response.retryable ? "Yes" : "No"}</span>
      </dd>
    </div>
  );
}

function availabilityFor(
  availability: Extract<StatutoryOrdinanceAvailabilityViewState, { status: "ready" }>,
  entitlementType: StatutoryEntitlementType,
): StatutoryOrdinanceAvailabilityResponse {
  return entitlementType === "SENIOR_CITIZEN" ? availability.seniorCitizen : availability.pwd;
}

function asEntitlementType(value: string): StatutoryEntitlementType | null {
  return value === "SENIOR_CITIZEN" || value === "PWD" ? value : null;
}

function entitlementLabel(value: StatutoryEntitlementType): string {
  return value === "SENIOR_CITIZEN" ? "Senior citizen" : "Person with disability";
}

function formatDate(value?: string | null): string {
  if (!value) return "Unavailable";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unavailable" : date.toLocaleString();
}

function buildDecisionRequest({
  basis,
  context,
  draft,
  requestReference,
  applyPayableBasis,
}: {
  basis: PayableBasisResponse;
  context: TerminalContext;
  draft: typeof defaultDraft;
  requestReference: string;
  applyPayableBasis: boolean;
}): StatutoryDiscountDecisionSubmitRequest {
  return {
    requestReference,
    sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
    parkingSessionId: basis.parkingSessionId,
    siteId: context.siteId,
    siteGroupId: context.siteGroupId,
    ticketReference: basis.ticketReference ?? null,
    plateNumber: basis.plateNumber ?? null,
    entitlementType: draft.entitlementType,
    idDocumentType: draft.idDocumentType,
    issuingAuthority: draft.issuingAuthority,
    expiryDate: draft.expiryDate || null,
    maskedIdReference: draft.maskedIdReference,
    evidenceCaptureRequested: true,
    evidenceReferences: null,
    requesterAttestation: draft.requesterAttested,
    attestationNotes: draft.attestationNotes || null,
    reasonCode: null,
    applyPayableBasis,
    originalTariffSnapshotId: basis.statutoryDiscountReadiness?.originalTariffSnapshotId ?? basis.originalTariffSnapshotId ?? basis.tariffSnapshotId,
  };
}

export function mapDecisionResponse(response: StatutoryDiscountDecisionResponse, previous: StatutoryDiscountWorkflowState): StatutoryDiscountWorkflowState {
  const status = statusFromResponse(response);
  return {
    ...previous,
    status,
    statutoryDiscountDecisionCommandId: response.statutoryDiscountDecisionCommandId,
    statutoryDiscountPayableBasisApplicationCommandId: response.statutoryDiscountPayableBasisApplicationCommandId ?? previous.statutoryDiscountPayableBasisApplicationCommandId ?? null,
    entitlementType: response.entitlementType,
    decisionStatus: response.decisionStatus,
    decisionResultStatus: response.decisionResultStatus,
    applicationCommandStatus: response.applicationCommandStatus,
    applicationResultClassification: response.applicationResultClassification,
    retryable: response.retryable,
    recoveryClassification: response.recoveryClassification,
    recoveryAction: response.recoveryAction,
    safeErrorCode: response.safeErrorCode,
    originalTariffSnapshotId: response.originalTariffSnapshotId,
    appliedTariffSnapshotId: response.appliedTariffSnapshotId,
    originalAmountMinorUnits: response.grossAmountMinorUnits,
    vatExclusiveBasisAmountMinorUnits: response.vatExclusiveBasisAmountMinorUnits,
    vatAmountMinorUnits: response.vatAmountMinorUnits,
    vatTreatment: response.vatTreatment,
    statutoryDiscountAmountMinorUnits: response.statutoryDiscountAmountMinorUnits,
    finalPayableAmountMinorUnits: response.netPayableAmountMinorUnits,
    currency: response.currency,
    payableBasisReady: response.payableBasisReady,
    payableBasisReadinessStatus: response.payableBasisReadinessStatus,
    payableBasisReadinessAction: response.payableBasisReadinessAction,
    evidenceRequired: response.evidenceRequired,
    evidenceRecorded: response.evidenceRecorded,
    correlationId: response.correlationId,
    createdAt: response.createdAt,
    lastReadbackAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function statusFromResponse(response: StatutoryDiscountDecisionResponse): StatutoryDiscountWorkflowState["status"] {
  if (response.payableBasisReady && response.payableBasisReadinessStatus === "APPLIED") return "applied";
  switch (response.payableBasisReadinessStatus) {
    case "AWAITING_REVIEW": return "awaiting_review";
    case "DECISION_APPROVED_APPLICATION_NOT_REQUESTED": return "approved_application_not_requested";
    case "APPLICATION_PROCESSING": return "application_processing";
    case "DECISION_REJECTED": return "rejected";
    case "RETRYABLE_FAILURE": return "retryable_failure";
    case "TERMINAL_FAILURE": return "terminal_failure";
    case "REQUIRED_FACTS_UNAVAILABLE": return "required_facts_unavailable";
    default:
      if (response.decisionResultStatus === "REJECTED") return "rejected";
      if (response.applicationCommandStatus === "PROCESSING") return "application_processing";
      return response.retryable ? "retryable_failure" : "terminal_failure";
  }
}

function isAppliedComplete(state: StatutoryDiscountWorkflowState): boolean {
  return state.status === "applied" && Boolean(state.appliedTariffSnapshotId) && state.finalPayableAmountMinorUnits != null && Boolean(state.currency);
}

function currentStateEvidence(state: StatutoryDiscountWorkflowState): Partial<StatutoryDiscountWorkflowState> {
  return {
    requestReference: state.requestReference,
    decisionIdempotencyKey: state.decisionIdempotencyKey,
    applicationIdempotencyKey: state.applicationIdempotencyKey,
    statutoryDiscountDecisionCommandId: state.statutoryDiscountDecisionCommandId,
    statutoryDiscountPayableBasisApplicationCommandId: state.statutoryDiscountPayableBasisApplicationCommandId,
  };
}

function titleForStatus(status: StatutoryDiscountWorkflowState["status"]): string {
  const titles: Record<StatutoryDiscountWorkflowState["status"], string> = {
    none: "No statutory request",
    draft: "Draft statutory request",
    submitting: "Submitting statutory request",
    awaiting_review: "Awaiting Operator Review",
    approved_application_not_requested: "Approved, Application Not Requested",
    application_submitting: "Submitting statutory application",
    application_processing: "Statutory Payable Basis Processing",
    applied: "Statutory Payable Basis Applied",
    rejected: "Rejected",
    retryable_failure: "Retry Required",
    terminal_failure: "Support Required",
    required_facts_unavailable: "Required Facts Unavailable",
  };
  return titles[status];
}

function descriptionForStatus(status: StatutoryDiscountWorkflowState["status"], state: StatutoryDiscountWorkflowState): string {
  if (state.restoredAfterRestart) {
    if (status === "application_processing") {
      return "Statutory payable-basis application remains in progress after restart. Use canonical readback before taking another action.";
    }
    if (status === "applied" && !state.amountAcknowledged) {
      return "Restored after restart with applied statutory amount change still pending acknowledgement. Use canonical readback only after cashier acknowledgement.";
    }
    return "Restored after restart. Use canonical readback before taking the next statutory action.";
  }
  const descriptions: Record<StatutoryDiscountWorkflowState["status"], string> = {
    none: "Start a review-mediated statutory request only after the original authoritative payable basis is visible.",
    draft: "Enter safe entitlement facts for Operator Console review. Full IDs, raw images, reviewer data, and calculated values are not accepted here.",
    submitting: "Submitting safe pending-review facts with applyPayableBasis=false.",
    awaiting_review: "Waiting for Operator Console review. Check status performs read-only Central PMS GET readback.",
    approved_application_not_requested: "Statutory request was approved. Statutory payable-basis application has not been requested. Action: Submit Application Intent.",
    application_submitting: "Submitting application intent with applyPayableBasis=true. The desktop does not send calculated statutory values.",
    application_processing: "Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.",
    applied: "Central PMS returned the applied tariff snapshot and final statutory amount. Continue to Cash is available only after acknowledgement, statutory-aware readiness, local prerequisites, and immediate revalidation pass.",
    rejected: "Operator Console rejected the statutory request. Application intent is unavailable.",
    retryable_failure: "A retryable statutory failure was reported. Reuse the original idempotency key according to Central PMS recovery guidance.",
    terminal_failure: "A terminal statutory failure requires support. Blind retry is disabled.",
    required_facts_unavailable: "Central PMS did not return all required statutory payable-basis facts. Missing values are not substituted locally.",
  };
  return descriptions[status];
}

function friendly(value?: string | null): string {
  if (!value) return "Unavailable";
  return value.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}

function formatCurrency(amountMinorUnits?: number | null, currency?: string | null): string {
  if (amountMinorUnits == null) return "Unavailable";
  return new Intl.NumberFormat("en-PH", { style: "currency", currency: currency ?? "PHP" }).format(amountMinorUnits / 100);
}
