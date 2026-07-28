import { useEffect, useMemo, useRef, useState } from "react";
import type { AptConfig, ConfigLoadResult } from "./config";
import { loadAptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import { createCentralPmsClient } from "./api/clientFactory";
import type { CentralPmsClient, CentralPmsResult, PayableBasisReferenceType, PayableBasisResponse, StatutoryDiscountDecisionResponse, StatutoryDiscountWorkflowState } from "./api/centralPmsTypes";
import { CashCapturePanel } from "./CashCapturePanel";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import { ReceiptVisualSmokeShell, shouldUseReceiptVisualSmoke } from "./ReceiptVisualSmoke";
import {
  PayableBasisVisualSmokeShell,
  shouldUsePayableBasisVisualSmoke,
} from "./PayableBasisVisualSmoke";
import {
  TransactionCompletionVisualSmokeShell,
  shouldUseTransactionCompletionVisualSmoke,
} from "./TransactionCompletionVisualSmoke";
import { StatutoryDiscountVisualSmokeShell, shouldUseStatutoryDiscountVisualSmoke } from "./StatutoryDiscountVisualSmoke";
import { buildTerminalContext, type TerminalContext } from "./terminalContext";
import { createWebViewLocalJournalBridge, type LocalJournalBridge, type PayableBasisStateSnapshot } from "./localJournalBridge";

type LookupState =
  | { status: "idle" }
  | { status: "loading"; correlationId: string; requestId: number; referenceType: PayableBasisReferenceType; referenceValue: string }
  | { status: "resolved"; basis: PayableBasisResponse; source: "fresh" | "restored"; acknowledgementRequired?: false }
  | { status: "amount_changed"; previous: PayableBasisResponse; current: PayableBasisResponse; correlationId: string; acknowledged: boolean }
  | { status: "failed"; result: Exclude<CentralPmsResult, { ok: true }> };

type PreCashResult =
  | { ok: true; basis: PayableBasisResponse }
  | { ok: false; message: string };

const defaultBridge = createWebViewLocalJournalBridge();
const noStatutoryWorkflow: StatutoryDiscountWorkflowState = { status: "none" };

export function App() {
  const [configResult, setConfigResult] = useState<ConfigLoadResult | null>(null);

  useEffect(() => {
    void loadAptConfig().then(setConfigResult).catch(() => {
      setConfigResult({
        ok: false,
        errors: ["Unable to load terminal configuration from /apt-config.json."],
      });
    });
  }, []);

  if (!configResult) {
    return <StartupFrame title="Starting terminal" message="Loading terminal configuration..." />;
  }

  if (!configResult.ok) {
    return <StartupRefusal result={configResult} />;
  }

  if (shouldUseStatutoryDiscountVisualSmoke(window.location.search)) {
    return (
      <StatutoryDiscountVisualSmokeShell
        config={configResult.config}
        renderTerminalShell={({ config, client, initialResolvedBasis, initialStatutoryState, bridge, initialCashEntryRequested, renderKey }) => (
          <TerminalShell
            key={renderKey}
            config={config}
            client={client}
            localJournalBridge={bridge}
            initialReferenceType="ticket"
            initialReferenceValue="APT-ACTIVE-1001"
            initialResolvedBasis={initialResolvedBasis}
            initialStatutoryState={initialStatutoryState}
            restorePayableBasisOnMount={false}
            initialCashEntryRequested={initialCashEntryRequested}
          />
        )}
      />
    );
  }

  if (shouldUsePayableBasisVisualSmoke(window.location.search)) {
    return (
      <PayableBasisVisualSmokeShell
        config={configResult.config}
        renderTerminalShell={({ scenario, bridge, restorePayableBasisOnMount, renderKey }) => (
          <TerminalShell
            key={renderKey}
            config={scenario.config}
            client={scenario.client}
            localJournalBridge={bridge}
            initialReferenceType={scenario.referenceType}
            initialReferenceValue={scenario.referenceValue}
            restorePayableBasisOnMount={restorePayableBasisOnMount}
          />
        )}
      />
    );
  }

  if (shouldUseReceiptVisualSmoke(window.location.search)) {
    return <ReceiptVisualSmokeShell config={configResult.config} />;
  }

  if (shouldUseTransactionCompletionVisualSmoke(window.location.search)) {
    return <TransactionCompletionVisualSmokeShell config={configResult.config} />;
  }

  return <TerminalShell config={configResult.config} client={createCentralPmsClient(configResult.config)} />;
}

export function TerminalShell({
  config,
  client,
  localJournalBridge = defaultBridge,
  initialReferenceType = "ticket",
  initialReferenceValue = "",
  restorePayableBasisOnMount = true,
  initialResolvedBasis,
  initialStatutoryState = noStatutoryWorkflow,
  initialCashEntryRequested = false,
}: {
  config: AptConfig;
  client: CentralPmsClient;
  localJournalBridge?: LocalJournalBridge;
  initialReferenceType?: PayableBasisReferenceType;
  initialReferenceValue?: string;
  restorePayableBasisOnMount?: boolean;
  initialResolvedBasis?: PayableBasisResponse;
  initialStatutoryState?: StatutoryDiscountWorkflowState;
  initialCashEntryRequested?: boolean;
}) {
  const context = useMemo(() => buildTerminalContext(config), [config]);
  const [referenceType, setReferenceType] = useState<PayableBasisReferenceType>(initialReferenceType);
  const [referenceValue, setReferenceValue] = useState(initialReferenceValue);
  const [lookupState, setLookupState] = useState<LookupState>(() => initialLookupState(initialResolvedBasis, initialStatutoryState, "fresh"));
  const [statutoryWorkflowState, setStatutoryWorkflowState] = useState<StatutoryDiscountWorkflowState>(initialStatutoryState);
  const [localPrerequisiteMessage, setLocalPrerequisiteMessage] = useState<string | null>(null);
  const [cashEntryRequested, setCashEntryRequested] = useState(initialCashEntryRequested);
  const [preCashStatus, setPreCashStatus] = useState<"idle" | "revalidating" | "passed" | "blocked">("idle");
  const latestRequestId = useRef(0);

  useEffect(() => {
    latestRequestId.current += 1;
    setReferenceType(initialReferenceType);
    setReferenceValue(initialReferenceValue);
    setLookupState(initialLookupState(initialResolvedBasis, initialStatutoryState, "fresh"));
    setStatutoryWorkflowState(initialStatutoryState);
    setLocalPrerequisiteMessage(null);
    setCashEntryRequested(initialCashEntryRequested);
    setPreCashStatus("idle");
  }, [initialReferenceType, initialReferenceValue, initialResolvedBasis, initialStatutoryState, initialCashEntryRequested]);

  useEffect(() => {
    if (!restorePayableBasisOnMount) {
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();

    async function restore() {
      const result = await localJournalBridge.getLatestPayableBasisState?.(correlationId, context.terminalId, context.siteId);
      if (cancelled || !result || !result.ok || !result.payload) {
        return;
      }

      setReferenceType(result.payload.lookupReferenceType);
      setReferenceValue(result.payload.lookupReferenceValue);
      setCashEntryRequested(false);
      setPreCashStatus("idle");
      const restoredStatutoryState = parseStatutoryState(result.payload.statutoryDiscountStateJson, true);
      const restoredBasis = basisFromState(result.payload);
      setStatutoryWorkflowState(restoredStatutoryState);
      setLookupState(initialLookupState(restoredBasis, restoredStatutoryState, "restored"));
    }

    void restore();
    return () => {
      cancelled = true;
    };
  }, [context.siteId, context.terminalId, localJournalBridge, restorePayableBasisOnMount]);
  const displayedBasis = lookupState.status === "resolved" ? lookupState.basis : lookupState.status === "amount_changed" && lookupState.acknowledged ? lookupState.current : undefined;
  const tariffExpired = displayedBasis ? new Date(displayedBasis.tariffValidUntil).getTime() <= Date.now() : false;
  const statutoryWorkflowActive = statutoryWorkflowState.status !== "none";
  const centralReady = Boolean(displayedBasis?.readyForCashAcceptance) && !tariffExpired && lookupState.status !== "amount_changed";
  const statutoryCashGate = displayedBasis
    ? statutoryCashGateStatus(displayedBasis, statutoryWorkflowState, lookupState)
    : { ready: false, message: "No payable basis is resolved." };
  const cashBoundaryReady = centralReady && (!statutoryWorkflowActive || statutoryCashGate.ready);

  async function resolveReference() {
    const trimmed = referenceValue.trim();
    if (!trimmed) {
      setLookupState({
        status: "failed",
        result: {
          ok: false,
          kind: "invalid_request",
          error: {
            errorCode: "INVALID_REFERENCE",
            message: "Enter one ticket or plate reference before resolving.",
            correlationId: "local-validation",
            retryable: false,
          },
        },
      });
      return;
    }

    const correlationId = createCorrelationId();
    const requestId = latestRequestId.current + 1;
    latestRequestId.current = requestId;
    setCashEntryRequested(false);
    setPreCashStatus("idle");
    setStatutoryWorkflowState(noStatutoryWorkflow);
    setLookupState({ status: "loading", correlationId, requestId, referenceType, referenceValue: trimmed });

    const result = await client.resolvePayableBasis(referenceType, trimmed, correlationId);
    if (latestRequestId.current !== requestId) {
      return;
    }

    if (result.ok) {
      await persistPayableBasis(result.response, referenceType, trimmed, false, false, null, noStatutoryWorkflow);
      setCashEntryRequested(false);
      setPreCashStatus("idle");
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return;
    }

    setLookupState({ status: "failed", result });
  }

  function resetLookup() {
    latestRequestId.current += 1;
    setLookupState({ status: "idle" });
    setReferenceValue("");
    setLocalPrerequisiteMessage(null);
  }

  async function preCashRevalidate(currentBasis: PayableBasisResponse, preserveCashEntry = false): Promise<PreCashResult> {
    if (!currentBasis.readyForCashAcceptance) {
      return { ok: false, message: "Central PMS has not marked this payable basis ready for cash acceptance." };
    }

    const correlationId = createCorrelationId();
    const requestId = latestRequestId.current + 1;
    latestRequestId.current = requestId;
    const result = await client.revalidatePayableBasis(currentBasis, correlationId);

    if (latestRequestId.current !== requestId) {
      return { ok: false, message: "The payable basis changed while revalidation was pending. Resolve the current reference again." };
    }

    if (!result.ok) {
      setLookupState({ status: "failed", result });
      return { ok: false, message: result.error.message };
    }

    const outcome = result.response.revalidationOutcome ?? "UNKNOWN";
    const nextStatutoryState = statutoryStateFromPayableBasis(result.response, statutoryWorkflowState, outcome);
    if (nextStatutoryState !== statutoryWorkflowState) {
      setStatutoryWorkflowState(nextStatutoryState);
    }
    await persistPayableBasis(
      result.response,
      result.response.ticketReference ? "ticket" : "plate",
      result.response.ticketReference ?? result.response.plateNumber ?? referenceValue,
      outcome === "AMOUNT_CHANGED",
      false,
      currentBasis.authoritativeAmountMinorUnits,
      nextStatutoryState,
    );

    if (outcome === "PASSED_UNCHANGED" && result.response.readyForCashAcceptance && revalidatedBasisMatchesCurrentStatutoryAuthority(result.response, nextStatutoryState)) {
      setCashEntryRequested(preserveCashEntry);
      setPreCashStatus("idle");
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return { ok: true, basis: result.response };
    }

    if (outcome === "PASSED_UNCHANGED" && result.response.readyForCashAcceptance) {
      setCashEntryRequested(false);
      setPreCashStatus("blocked");
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return { ok: false, message: "Central PMS revalidation did not return the same applied statutory payable basis. Resolve or check statutory status again before accepting cash." };
    }

    if (outcome === "AMOUNT_CHANGED") {
      setCashEntryRequested(false);
      setPreCashStatus("blocked");
      setLookupState({ status: "amount_changed", previous: currentBasis, current: result.response, correlationId, acknowledged: false });
      return { ok: false, message: "The parking fee changed before cash acceptance. Review and acknowledge the new amount before continuing." };
    }

    setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
    return { ok: false, message: blockerMessage(result.response) };
  }

  async function acknowledgeAmountChange() {
    if (lookupState.status !== "amount_changed") return;
    const acknowledgedStatutoryState = statutoryWorkflowState.status === "none"
      ? statutoryWorkflowState
      : { ...statutoryWorkflowState, amountAcknowledged: true, updatedAt: new Date().toISOString() };
    await persistPayableBasis(
      lookupState.current,
      lookupState.current.ticketReference ? "ticket" : "plate",
      lookupState.current.ticketReference ?? lookupState.current.plateNumber ?? referenceValue,
      true,
      false,
      lookupState.previous.authoritativeAmountMinorUnits,
      acknowledgedStatutoryState,
    );
    setStatutoryWorkflowState(acknowledgedStatutoryState);
    setReferenceValue(lookupState.current.ticketReference ?? lookupState.current.plateNumber ?? referenceValue);

    if (lookupState.current.statutoryDiscountReadiness?.applicable) {
      const correlationId = createCorrelationId();
      const result = await client.revalidatePayableBasis(lookupState.current, correlationId);
      if (!result.ok) {
        setLookupState({ status: "failed", result });
        return;
      }

      await persistPayableBasis(
        result.response,
        result.response.ticketReference ? "ticket" : "plate",
        result.response.ticketReference ?? result.response.plateNumber ?? referenceValue,
        true,
        false,
        lookupState.previous.authoritativeAmountMinorUnits,
        acknowledgedStatutoryState,
      );
      setCashEntryRequested(false);
      setPreCashStatus("blocked");
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return;
    }

    setLookupState({ status: "resolved", basis: lookupState.current, source: "fresh" });
  }

  async function persistPayableBasis(
    basis: PayableBasisResponse,
    persistedReferenceType: PayableBasisReferenceType,
    persistedReferenceValue: string,
    amountChanged: boolean,
    cashierAcknowledgementRequired: boolean,
    priorAmountMinorUnits: number | null,
    statutoryState: StatutoryDiscountWorkflowState = statutoryWorkflowState,
  ) {
    await localJournalBridge.savePayableBasisState?.(createCorrelationId(), {
      localWorkflowId: `${basis.siteId}:${basis.terminalId ?? context.terminalId}:${basis.parkingSessionId}`,
      lookupReferenceType: persistedReferenceType,
      lookupReferenceValue: persistedReferenceValue,
      parkingSessionId: basis.parkingSessionId,
      tariffSnapshotId: basis.tariffSnapshotId,
      siteId: basis.siteId,
      siteGroupId: basis.siteGroupId,
      sitePosServerId: basis.sitePosServerId ?? context.posServerId,
      terminalId: basis.terminalId ?? context.terminalId,
      authoritativeAmountMinorUnits: basis.authoritativeAmountMinorUnits,
      currency: basis.currency,
      tariffCalculatedAt: basis.tariffCalculatedAt ?? null,
      tariffValidUntil: basis.tariffValidUntil,
      feeValidUntil: basis.feeValidUntil ?? null,
      parkingStatus: basis.parkingStatus,
      paymentStatus: basis.paymentStatus,
      sessionReadiness: basis.sessionReadiness ?? null,
      tariffReadiness: basis.tariffReadiness ?? null,
      paymentEligibility: basis.paymentEligibility ?? null,
      terminalCashAvailability: basis.terminalCashAvailability ?? null,
      fiscalReadiness: basis.fiscalReadiness ?? null,
      salesInvoiceConfigurationReadiness: basis.salesInvoiceConfigurationReadiness ?? null,
      cashAcceptanceReadiness: basis.cashAcceptanceReadiness ?? null,
      readyForCashAcceptance: basis.readyForCashAcceptance,
      blockingReasonCodes: basis.blockingReasonCodes,
      retryable: basis.retryable,
      safeUserFacingClassification: basis.safeUserFacingClassification,
      centralPmsCorrelationId: basis.correlationId,
      revalidationOutcome: basis.revalidationOutcome ?? null,
      cashierAcknowledgementRequired,
      amountChanged,
      priorDisplayedAmountMinorUnits: priorAmountMinorUnits,
      statutoryDiscountStateJson: serializeStatutoryState(statutoryState),
    });
  }

  async function handleContinueToCash(currentBasis: PayableBasisResponse) {
    if (!cashBoundaryReady || !localPrerequisitesReady) {
      setPreCashStatus("blocked");
      if (statutoryWorkflowActive && !statutoryCashGate.ready) {
        setLocalPrerequisiteMessage(statutoryCashGate.message);
      }
      return;
    }

    setPreCashStatus("revalidating");
    const result = await preCashRevalidate(currentBasis);
    if (result.ok) {
      setCashEntryRequested(true);
      setPreCashStatus("passed");
    } else {
      setCashEntryRequested(false);
      setPreCashStatus("blocked");
      setLocalPrerequisiteMessage(result.message);
    }
  }
  function handleReferenceTypeChange(nextType: PayableBasisReferenceType) {
    latestRequestId.current += 1;
    setReferenceType(nextType);
    setReferenceValue("");
    setStatutoryWorkflowState(noStatutoryWorkflow);
    setLookupState({ status: "idle" });
  }


  async function handleStatutoryStateChange(next: StatutoryDiscountWorkflowState) {
    setStatutoryWorkflowState(next);
    if (displayedBasis) {
      await persistPayableBasis(
        displayedBasis,
        displayedBasis.ticketReference ? "ticket" : "plate",
        displayedBasis.ticketReference ?? displayedBasis.plateNumber ?? referenceValue,
        lookupState.status === "amount_changed",
        lookupState.status === "amount_changed" && !lookupState.acknowledged,
        lookupState.status === "amount_changed" ? lookupState.previous.authoritativeAmountMinorUnits : null,
        next,
      );
    }
  }

  async function handleAppliedStatutoryBasis(decisionCommandId: string, _response: StatutoryDiscountDecisionResponse, nextState: StatutoryDiscountWorkflowState) {
    if (!displayedBasis) return;
    const correlationId = createCorrelationId();
    const referenceTypeForBasis: PayableBasisReferenceType = displayedBasis.ticketReference ? "ticket" : "plate";
    const referenceValueForBasis = displayedBasis.ticketReference ?? displayedBasis.plateNumber ?? referenceValue;
    const result = await client.resolvePayableBasis(referenceTypeForBasis, referenceValueForBasis, correlationId, decisionCommandId);
    if (!result.ok) {
      setLookupState({ status: "failed", result });
      return;
    }

    const changed = result.response.authoritativeAmountMinorUnits !== displayedBasis.authoritativeAmountMinorUnits || result.response.tariffSnapshotId !== displayedBasis.tariffSnapshotId;
    await persistPayableBasis(result.response, referenceTypeForBasis, referenceValueForBasis, changed, changed, displayedBasis.authoritativeAmountMinorUnits, nextState);
    setStatutoryWorkflowState(nextState);
    setCashEntryRequested(false);
    setPreCashStatus("blocked");
    setLookupState(changed
      ? { status: "amount_changed", previous: displayedBasis, current: result.response, correlationId, acknowledged: false }
      : { status: "resolved", basis: result.response, source: "fresh" });
  }
  const localPrerequisitesReady = config.nonLiveCashCaptureEnabled;

  return (
    <main className="terminal-shell" data-testid="apt-terminal-shell" data-app-ready="true">
      <header className="brand-header">
        <div>
          <p className="eyebrow">ExitPass Assisted Payment Terminal</p>
          <h1>Cashier-Assisted Terminal</h1>
        </div>
      </header>

      <section className="workflow-stack">
        <OperationalContextPanel context={context} />
        <section className="lookup-panel" aria-labelledby="lookup-heading">
          <div className="section-heading">
            <p className="eyebrow">Session resolution</p>
            <h2 id="lookup-heading">Ticket or plate lookup</h2>
          </div>
          <form
            className="lookup-form"
            onSubmit={(event) => {
              event.preventDefault();
              void resolveReference();
            }}
          >
            <fieldset className="reference-type-toggle" aria-label="Reference type">
              <label>
                <input type="radio" checked={referenceType === "ticket"} onChange={() => handleReferenceTypeChange("ticket")} />
                Ticket
              </label>
              <label>
                <input type="radio" checked={referenceType === "plate"} onChange={() => handleReferenceTypeChange("plate")} />
                Plate
              </label>
            </fieldset>
            <label htmlFor="referenceValue">{referenceType === "ticket" ? "Ticket reference" : "Plate number"}</label>
            <div className="lookup-row">
              <input
                id="referenceValue"
                value={referenceValue}
                onChange={(event) => {
                  latestRequestId.current += 1;
                  setReferenceValue(event.target.value);
                  setLookupState({ status: "idle" });
                }}
                placeholder={referenceType === "ticket" ? "Scan or type ticket reference" : "Type plate number"}
                autoFocus
                autoComplete="off"
              />
              <button type="submit" disabled={!referenceValue.trim() || lookupState.status === "loading"}>
                Resolve
              </button>
            </div>
          </form>

          {lookupState.status === "loading" && (
            <StatusNotice tone="info" title="Resolving parking session">
              Request sent to {context.centralPmsConnectionMode}. Support reference: <strong>{lookupState.correlationId}</strong>
            </StatusNotice>
          )}

          {lookupState.status === "failed" && <FailureNotice result={lookupState.result} onReset={resetLookup} />}
          {lookupState.status === "amount_changed" && (
            <AmountChangedNotice previous={lookupState.previous} current={lookupState.current} onAcknowledge={() => void acknowledgeAmountChange()} />
          )}

          {displayedBasis && (
            <>
              <div className="resolved-workflow">
                <div className="session-column">
                  <SessionSummary basis={displayedBasis} restored={lookupState.status === "resolved" && lookupState.source === "restored"} statutoryWorkflowActive={statutoryWorkflowActive} statutoryCashReady={statutoryCashGate.ready} />
                  <ReadinessPanel basis={displayedBasis} tariffExpired={tariffExpired} statutoryWorkflowActive={statutoryWorkflowActive} statutoryCashReady={statutoryCashGate.ready} />
                  <StatutoryDiscountPanel
                    basis={displayedBasis}
                    client={client}
                    context={context}
                    state={statutoryWorkflowState}
                    onStateChange={(next) => void handleStatutoryStateChange(next)}
                    onAppliedBasisReady={handleAppliedStatutoryBasis}
                  />
                  {!localPrerequisitesReady && (
                    <StatusNotice tone="danger" title="Local cash prerequisites unavailable" dataTestId="local-cash-prerequisites-notice">
                      Local cash capture is disabled in this terminal profile. Local prerequisites can only restrict Central PMS readiness.
                    </StatusNotice>
                  )}
                  {localPrerequisiteMessage && <p className="cash-error">{localPrerequisiteMessage}</p>}
                </div>
                <div className="cash-column">
                  <PreCashBoundaryPanel
                    basis={displayedBasis}
                    centralReady={cashBoundaryReady}
                    localPrerequisitesReady={localPrerequisitesReady}
                    status={preCashStatus}
                    onContinue={() => void handleContinueToCash(displayedBasis)}
                  />
                  {cashEntryRequested && (
                    <CashCapturePanel
                      config={config}
                      context={context}
                      session={displayedBasis}
                      tariffExpired={!centralReady}
                      cashAcceptanceReady={cashBoundaryReady && localPrerequisitesReady}
                      cashAcceptanceBlockedMessage={statutoryWorkflowActive && !statutoryCashGate.ready ? statutoryCashGate.message : blockerMessage(displayedBasis)}
                      onBeforeCashReceived={(basis) => preCashRevalidate(basis, true)}
                      onLocalPrerequisiteFailure={setLocalPrerequisiteMessage}
                      bridge={localJournalBridge}
                    />
                  )}
                </div>
              </div>
              {!config.nonLiveCashCaptureEnabled && <PaymentStage />}
            </>
          )}
        </section>
      </section>
    </main>
  );
}


function PreCashBoundaryPanel({
  basis,
  centralReady,
  localPrerequisitesReady,
  status,
  onContinue,
}: {
  basis: PayableBasisResponse;
  centralReady: boolean;
  localPrerequisitesReady: boolean;
  status: "idle" | "revalidating" | "passed" | "blocked";
  onContinue: () => void;
}) {
  const disabled = !centralReady || !localPrerequisitesReady || status === "revalidating";
  const statusText = status === "revalidating"
    ? "Revalidation in progress"
    : status === "passed"
      ? "Revalidation passed unchanged"
      : centralReady && localPrerequisitesReady
        ? "Ready for immediate pre-cash revalidation"
        : "Cash acceptance remains blocked";

  return (
    <section className="status-notice info" aria-label="Pre-cash acceptance boundary" data-testid="pre-cash-boundary">
      <h3>Pre-cash acceptance</h3>
      <p>{statusText}</p>
      <p>CASH_RECEIVED has not occurred. Continue to Cash runs Central PMS revalidation before local cash custody can be recorded.</p>
      <dl className="central-pms-details">
        <div><dt>readyForCashAcceptance</dt><dd data-testid="central-cash-ready-value">{basis.readyForCashAcceptance ? "true" : "false"}</dd></div>
        <div><dt>Local prerequisites</dt><dd data-testid="local-cash-prerequisites-value">{localPrerequisitesReady ? "Satisfied" : "Blocked"}</dd></div>
        <div><dt>Tariff snapshot</dt><dd>{basis.tariffSnapshotId}</dd></div>
      </dl>
      <button type="button" className="primary-action" disabled={disabled} onClick={onContinue} data-testid="continue-to-cash">
        Continue to Cash
      </button>
    </section>
  );
}

function StartupFrame({ title, message }: { title: string; message: string }) {
  return (
    <main className="startup-frame">
      <div className="startup-panel">
        <p className="eyebrow">ExitPass Assisted Payment Terminal</p>
        <h1>{title}</h1>
        <p>{message}</p>
      </div>
    </main>
  );
}

function StartupRefusal({ result }: { result: ConfigLoadResult & { ok: false } }) {
  return (
    <main className="startup-frame refusal" role="alert">
      <div className="startup-panel">
        <p className="eyebrow">Startup refused</p>
        <h1>Unsupported terminal profile</h1>
        <p>The terminal can start only with APT_PROFILE set to CASHIER_ASSISTED_TERMINAL.</p>
        <ul>{result.errors.map((error) => <li key={error}>{error}</li>)}</ul>
      </div>
    </main>
  );
}

function OperationalContextPanel({ context }: { context: TerminalContext }) {
  const summaryRows = [
    ["Site", context.siteName],
    ["Cashier", context.cashierDisplayName],
    ["Shift", context.shiftStatus],
    ["Terminal", context.terminalDisplayName],
    ["POS readiness", `Configured: ${context.posServerId}`],
  ];

  const detailRows = [
    ["Terminal ID", context.terminalId],
    ["Site ID", context.siteId],
    ["Site-group ID", context.siteGroupId],
    ["POS Server ID", context.posServerId],
    ["Cashier ID", context.cashierId],
    ["Shift ID", context.shiftId],
    ["Central PMS", context.centralPmsConnectionMode],
  ];

  return (
    <aside className="context-panel compact" aria-label="Operational context">
      <div className="context-summary-grid">
        {summaryRows.map(([label, value]) => (
          <div key={label} className="context-chip"><span>{label}</span><strong>{value}</strong></div>
        ))}
      </div>
      <details className="terminal-details">
        <summary>Terminal details</summary>
        <dl>{detailRows.map(([label, value]) => <div key={label} className="context-row"><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      </details>
    </aside>
  );
}

function SessionSummary({ basis, restored, statutoryWorkflowActive, statutoryCashReady }: { basis: PayableBasisResponse; restored: boolean; statutoryWorkflowActive: boolean; statutoryCashReady: boolean }) {
  const amount = formatCurrency(basis.authoritativeAmountMinorUnits, basis.currency);
  const primaryRows = [
    [basis.ticketReference ? "Ticket reference" : "Plate number", basis.ticketReference ?? basis.plateNumber ?? "Unavailable"],
    ["Parking session ID", basis.parkingSessionId],
    ["Tariff snapshot ID", basis.tariffSnapshotId],
    ["Tariff valid until", formatDate(basis.tariffValidUntil)],
    ["Payment status", basis.paymentStatus],
  ];

  const secondaryRows = [
    ["Masked plate", maskPlate(basis.plateNumber)],
    ["Site", basis.siteName ?? basis.siteId],
    ["Entry timestamp", formatDate(basis.entryTimestamp)],
    ["Currency", basis.currency],
    ["Tariff calculated", formatDate(basis.tariffCalculatedAt)],
    ["Fee valid until", formatDate(basis.feeValidUntil)],
    ["Correlation ID", basis.correlationId],
  ];

  return (
    <section className="session-summary" aria-label="Resolved parking session" data-testid="payable-basis-summary">
      <div className="amount-band">
        <div>
          <p className="eyebrow">Authoritative payable basis</p>
          <strong data-testid="payable-basis-amount">{amount}</strong>
        </div>
        <span className={basis.readyForCashAcceptance && (!statutoryWorkflowActive || statutoryCashReady) ? "status-badge success" : "status-badge"}>
          {restored ? "Previously resolved" : statutoryWorkflowActive && statutoryCashReady ? "Statutory ready" : statutoryWorkflowActive && basis.readyForCashAcceptance ? "Statutory blocked" : basis.readyForCashAcceptance ? "Ready" : "Blocked"}
        </span>
      </div>
      <dl className="summary-primary">{primaryRows.map(([label, value]) => <div key={label} className="summary-row"><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      <details className="session-details">
        <summary>Session details</summary>
        <dl>{secondaryRows.map(([label, value]) => <div key={label} className="summary-row"><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      </details>
    </section>
  );
}

function ReadinessPanel({ basis, tariffExpired, statutoryWorkflowActive, statutoryCashReady }: { basis: PayableBasisResponse; tariffExpired: boolean; statutoryWorkflowActive: boolean; statutoryCashReady: boolean }) {
  const ready = basis.readyForCashAcceptance && !tariffExpired;
  const cashierReady = ready && (!statutoryWorkflowActive || statutoryCashReady);
  const title = cashierReady
    ? statutoryWorkflowActive ? "Statutory payable basis ready for cash acceptance" : "Ready for cash acceptance"
    : statutoryWorkflowActive && ready ? "Central PMS payable basis ready" : "Cash acceptance blocked";
  const dimensions = [
    { label: "Session", value: basis.sessionReadiness ?? basis.parkingStatus, testId: "session-readiness-value" },
    { label: "Tariff", value: tariffExpired ? "EXPIRED" : basis.tariffReadiness ?? "UNKNOWN", testId: "tariff-readiness-value" },
    { label: "Payment eligibility", value: basis.paymentEligibility ?? basis.paymentStatus, testId: "payment-eligibility-value" },
    { label: "Terminal cash", value: basis.terminalCashAvailability ?? "UNKNOWN", testId: "terminal-cash-readiness-value" },
    { label: "Sales Invoice configuration", value: basis.salesInvoiceConfigurationReadiness ?? "UNKNOWN", testId: "sales-invoice-readiness-value" },
    { label: "Fiscal readiness", value: basis.fiscalReadiness ?? "UNKNOWN", testId: "fiscal-readiness-value" },
  ];

  return (
    <StatusNotice tone={cashierReady ? "success" : statutoryWorkflowActive && ready ? "info" : basis.retryable ? "info" : "danger"} title={title} dataTestId="cash-readiness-status">
      <p>{cashierReady
        ? statutoryWorkflowActive
          ? "Central PMS confirmed the applied statutory payable basis. Continue to Cash runs statutory-aware revalidation before cash entry, and CASH_RECEIVED revalidates again before local custody is recorded."
          : "Central PMS confirmed all pre-cash readiness checks. Revalidation will still run immediately before CASH_RECEIVED."
        : statutoryWorkflowActive && ready
          ? "Central PMS confirms the statutory payable basis is ready, but local statutory cash requirements are still blocked."
          : blockerMessage(basis)}</p>
      <dl className="central-pms-details">
        {dimensions.map((dimension) => <div key={dimension.label}><dt>{dimension.label}</dt><dd data-testid={dimension.testId}>{friendlyCode(dimension.value)}</dd></div>)}
      </dl>
      {basis.statutoryDiscountReadiness?.applicable && (
        <details open>
          <summary>Statutory readiness</summary>
          <dl className="central-pms-details">
            <div><dt>Status</dt><dd data-testid="statutory-readiness-value">{friendlyCode(basis.statutoryDiscountReadiness.payableBasisReadinessStatus)}</dd></div>
            <div><dt>Action</dt><dd>{friendlyCode(basis.statutoryDiscountReadiness.payableBasisReadinessAction)}</dd></div>
            <div><dt>Decision</dt><dd>{basis.statutoryDiscountReadiness.statutoryDiscountDecisionCommandId ?? "Unavailable"}</dd></div>
          </dl>
        </details>
      )}
      {basis.blockingReasonCodes.length > 0 && (
        <details>
          <summary>Support details</summary>
          <p>Safe codes: {basis.blockingReasonCodes.join(", ")}</p>
          <p>Classification: {basis.safeUserFacingClassification}</p>
          <p>Support reference: {basis.correlationId}</p>
        </details>
      )}
    </StatusNotice>
  );
}

function AmountChangedNotice({ previous, current, onAcknowledge }: { previous: PayableBasisResponse; current: PayableBasisResponse; onAcknowledge: () => void }) {
  const statutoryApplied = current.statutoryDiscountReadiness?.applicable === true;
  const originalSnapshot = current.statutoryDiscountReadiness?.originalTariffSnapshotId ?? previous.tariffSnapshotId;
  const appliedSnapshot = current.statutoryDiscountReadiness?.appliedTariffSnapshotId ?? current.appliedTariffSnapshotId ?? current.tariffSnapshotId;
  return (
    <StatusNotice tone="danger" title="Parking fee changed before cash acceptance">
      <p>Review the new authoritative payable basis before accepting cash. CASH_RECEIVED remains blocked until acknowledgement and a later unchanged revalidation.</p>
      <dl className="central-pms-details">
        <div><dt>Previous amount</dt><dd>{formatCurrency(previous.authoritativeAmountMinorUnits, previous.currency)}</dd></div>
        <div><dt>{statutoryApplied ? "Authoritative applied amount" : "New amount"}</dt><dd>{formatCurrency(current.authoritativeAmountMinorUnits, current.currency)}</dd></div>
        <div><dt>Previous tariff snapshot</dt><dd>{previous.tariffSnapshotId}</dd></div>
        <div><dt>Authoritative tariff snapshot</dt><dd>{current.tariffSnapshotId}</dd></div>
        <div><dt>Original tariff snapshot</dt><dd>{originalSnapshot}</dd></div>
        <div><dt>Applied tariff snapshot</dt><dd>{appliedSnapshot}</dd></div>
        {statutoryApplied && <div><dt>Statutory decision</dt><dd>{current.statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId ?? "Unavailable"}</dd></div>}
        {statutoryApplied && <div><dt>Statutory application</dt><dd>{current.statutoryDiscountReadiness?.statutoryDiscountPayableBasisApplicationCommandId ?? "Unavailable"}</dd></div>}
        <div><dt>Recalculated at</dt><dd>{formatDate(current.tariffCalculatedAt)}</dd></div>
        <div><dt>Support reference</dt><dd>{current.correlationId}</dd></div>
      </dl>
      <button className="secondary-action" type="button" onClick={onAcknowledge}>Acknowledge new amount</button>
    </StatusNotice>
  );
}

function PaymentStage() {
  return (
    <section className="payment-stage" aria-label="Payment stage disabled">
      <div>
        <p className="eyebrow">Payment stage</p>
        <h2>Unavailable in this slice</h2>
        <p>Central PMS payable-basis readiness is displayed here. Local cash custody remains the next desktop boundary.</p>
      </div>
      <button type="button" disabled>Collect payment</button>
    </section>
  );
}

function FailureNotice({ result, onReset }: { result: Exclude<CentralPmsResult, { ok: true }>; onReset: () => void }) {
  const titleByKind: Record<string, string> = {
    not_found: "Parking session not found",
    inactive: "Parking session is not payable",
    closed: "Parking session is closed",
    already_paid: "Parking session is already paid",
    ambiguous: "Multiple matching sessions require review",
    service_unavailable: "Central PMS temporarily unavailable",
    timeout: "Central PMS timeout",
    malformed_response: "Central PMS response could not be read",
    invalid_request: "Invalid lookup request",
    tariff_expired: "Parking fee has expired",
    cash_unavailable: "Cash payment is unavailable",
    fiscal_unavailable: "Fiscal service is unavailable",
    amount_changed: "Parking fee changed",
    unauthorized: "This Site or terminal is not authorized",
    unknown: "Lookup failed",
  };

  return (
    <StatusNotice tone={result.error.retryable ? "info" : "danger"} title={titleByKind[result.kind] ?? "Lookup failed"}>
      <p>{result.error.message}</p>
      {result.error.retryable && <p>Retry is available after Central PMS is reachable.</p>}
      <p className="support-line">Support reference: {result.error.correlationId}</p>
      <button className="secondary-action" type="button" onClick={onReset}>Back to lookup</button>
    </StatusNotice>
  );
}

function StatusNotice({
  tone,
  title,
  children,
  dataTestId,
}: {
  tone: "info" | "success" | "danger";
  title: string;
  children: React.ReactNode;
  dataTestId?: string;
}) {
  return <section className={`status-notice ${tone}`} role={tone === "danger" ? "alert" : "status"} data-testid={dataTestId}><h3>{title}</h3><div>{children}</div></section>;
}

function parseStatutoryState(raw?: string | null, restoredAfterRestart = false): StatutoryDiscountWorkflowState {
  if (!raw) return noStatutoryWorkflow;
  try {
    const parsed = JSON.parse(raw) as StatutoryDiscountWorkflowState;
    return parsed?.status ? { ...parsed, restoredAfterRestart } : noStatutoryWorkflow;
  } catch {
    return { status: "required_facts_unavailable", safeErrorCode: "LOCAL_STATUTORY_STATE_MALFORMED", restoredAfterRestart };
  }
}

function serializeStatutoryState(state: StatutoryDiscountWorkflowState): string | null {
  return state.status === "none" ? null : JSON.stringify(state);
}

function statutoryCashGateStatus(
  basis: PayableBasisResponse,
  statutoryState: StatutoryDiscountWorkflowState,
  lookupState: LookupState,
): { ready: boolean; message: string } {
  if (statutoryState.status === "none") {
    return { ready: true, message: "No statutory workflow is active." };
  }

  if (lookupState.status === "amount_changed" || !statutoryState.amountAcknowledged) {
    return { ready: false, message: "The applied statutory amount must be acknowledged before Continue to Cash." };
  }

  const readiness = basis.statutoryDiscountReadiness;
  if (!readiness?.applicable) {
    return { ready: false, message: "Central PMS did not return statutory readiness for the active statutory workflow." };
  }

  if (statutoryState.status !== "applied") {
    return { ready: false, message: statutoryGateMessageForStatus(statutoryState) };
  }

  if (statutoryState.decisionStatus !== "COMPLETED" || statutoryState.decisionResultStatus !== "APPROVED") {
    return { ready: false, message: "The statutory decision is not approved in canonical Central PMS readback." };
  }

  if (statutoryState.applicationCommandStatus !== "APPLIED" || !isSuccessfulStatutoryApplication(statutoryState.applicationResultClassification)) {
    return { ready: false, message: "The statutory payable-basis application is not applied in canonical Central PMS readback." };
  }

  if (!statutoryState.payableBasisReady || !readiness.payableBasisReady || !readiness.ready) {
    return { ready: false, message: "Central PMS has not marked the statutory payable basis ready." };
  }

  const appliedSnapshot = statutoryState.appliedTariffSnapshotId ?? readiness.appliedTariffSnapshotId ?? basis.appliedTariffSnapshotId;
  if (!appliedSnapshot || basis.tariffSnapshotId !== appliedSnapshot) {
    return { ready: false, message: "The displayed payable basis does not use the applied statutory tariff snapshot." };
  }

  const finalAmount = statutoryState.finalPayableAmountMinorUnits ?? readiness.finalPayableAmountMinorUnits;
  if (finalAmount == null || basis.authoritativeAmountMinorUnits !== finalAmount) {
    return { ready: false, message: "The displayed payable basis does not match the final statutory amount." };
  }

  const currency = statutoryState.currency ?? readiness.currency;
  if (!currency || basis.currency !== currency) {
    return { ready: false, message: "The displayed payable basis does not match the statutory currency." };
  }

  if (!statutoryState.statutoryDiscountDecisionCommandId || !statutoryState.statutoryDiscountPayableBasisApplicationCommandId) {
    return { ready: false, message: "Canonical statutory decision and application references are required before cash acceptance." };
  }

  if (basis.blockingReasonCodes.length > 0) {
    return { ready: false, message: blockerMessage(basis) };
  }

  return { ready: true, message: "Statutory payable basis is ready for cash acceptance." };
}

function revalidatedBasisMatchesCurrentStatutoryAuthority(
  basis: PayableBasisResponse,
  statutoryState: StatutoryDiscountWorkflowState,
): boolean {
  if (statutoryState.status === "none") {
    return true;
  }

  const readiness = basis.statutoryDiscountReadiness;
  const appliedSnapshot = statutoryState.appliedTariffSnapshotId ?? readiness?.appliedTariffSnapshotId ?? basis.appliedTariffSnapshotId;
  const finalAmount = statutoryState.finalPayableAmountMinorUnits ?? readiness?.finalPayableAmountMinorUnits;
  const currency = statutoryState.currency ?? readiness?.currency;

  return Boolean(readiness?.applicable)
    && readiness?.statutoryDiscountDecisionCommandId === statutoryState.statutoryDiscountDecisionCommandId
    && basis.tariffSnapshotId === appliedSnapshot
    && finalAmount != null
    && basis.authoritativeAmountMinorUnits === finalAmount
    && Boolean(currency)
    && basis.currency === currency;
}

function statutoryStateFromPayableBasis(
  basis: PayableBasisResponse,
  previous: StatutoryDiscountWorkflowState,
  revalidationOutcome?: string | null,
): StatutoryDiscountWorkflowState {
  const readiness = basis.statutoryDiscountReadiness;
  if (!readiness?.applicable) {
    return previous.status === "none"
      ? previous
      : {
          ...previous,
          status: "required_facts_unavailable",
          payableBasisReady: false,
          payableBasisReadinessStatus: "REQUIRED_FACTS_UNAVAILABLE",
          payableBasisReadinessAction: "DO_NOT_RETRY",
          safeErrorCode: "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE",
          amountAcknowledged: false,
          correlationId: basis.correlationId,
          updatedAt: new Date().toISOString(),
        };
  }

  const status = statutoryWorkflowStatusFromReadiness(readiness.payableBasisReadinessStatus, readiness.payableBasisReady);
  const amountAcknowledged = revalidationOutcome === "AMOUNT_CHANGED"
    ? false
    : status === "applied"
      ? previous.amountAcknowledged
      : false;

  return {
    ...previous,
    status,
    statutoryDiscountDecisionCommandId: readiness.statutoryDiscountDecisionCommandId ?? previous.statutoryDiscountDecisionCommandId ?? null,
    statutoryDiscountPayableBasisApplicationCommandId: readiness.statutoryDiscountPayableBasisApplicationCommandId ?? previous.statutoryDiscountPayableBasisApplicationCommandId ?? null,
    entitlementType: readiness.entitlementType ?? previous.entitlementType ?? null,
    decisionStatus: readiness.decisionStatus ?? previous.decisionStatus ?? null,
    decisionResultStatus: readiness.decisionResultStatus ?? previous.decisionResultStatus ?? null,
    applicationCommandStatus: readiness.applicationCommandStatus ?? previous.applicationCommandStatus ?? null,
    applicationResultClassification: readiness.applicationResultClassification ?? previous.applicationResultClassification ?? null,
    retryable: readiness.retryable,
    recoveryClassification: readiness.recoveryClassification ?? previous.recoveryClassification ?? null,
    recoveryAction: readiness.recoveryAction ?? previous.recoveryAction ?? null,
    safeErrorCode: readiness.safeErrorCode ?? readiness.blockingReasonCode ?? previous.safeErrorCode ?? null,
    originalTariffSnapshotId: readiness.originalTariffSnapshotId ?? previous.originalTariffSnapshotId ?? null,
    appliedTariffSnapshotId: readiness.appliedTariffSnapshotId ?? previous.appliedTariffSnapshotId ?? basis.appliedTariffSnapshotId ?? null,
    originalAmountMinorUnits: readiness.originalAmountMinorUnits ?? previous.originalAmountMinorUnits ?? null,
    vatExclusiveBasisAmountMinorUnits: readiness.vatExclusiveBasisAmountMinorUnits ?? previous.vatExclusiveBasisAmountMinorUnits ?? null,
    vatAmountMinorUnits: readiness.vatAmountMinorUnits ?? previous.vatAmountMinorUnits ?? null,
    vatTreatment: readiness.vatTreatment ?? previous.vatTreatment ?? null,
    statutoryDiscountAmountMinorUnits: readiness.statutoryDiscountAmountMinorUnits ?? previous.statutoryDiscountAmountMinorUnits ?? null,
    finalPayableAmountMinorUnits: readiness.finalPayableAmountMinorUnits ?? previous.finalPayableAmountMinorUnits ?? null,
    currency: readiness.currency ?? previous.currency ?? basis.currency,
    payableBasisReady: readiness.payableBasisReady,
    payableBasisReadinessStatus: readiness.payableBasisReadinessStatus,
    payableBasisReadinessAction: readiness.payableBasisReadinessAction ?? null,
    correlationId: basis.correlationId,
    amountAcknowledged,
    lastReadbackAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function statutoryWorkflowStatusFromReadiness(status: string, ready: boolean): StatutoryDiscountWorkflowState["status"] {
  if (ready && status === "APPLIED") return "applied";
  switch (status) {
    case "AWAITING_REVIEW": return "awaiting_review";
    case "DECISION_APPROVED_APPLICATION_NOT_REQUESTED": return "approved_application_not_requested";
    case "APPLICATION_PROCESSING": return "application_processing";
    case "DECISION_REJECTED": return "rejected";
    case "RETRYABLE_FAILURE": return "retryable_failure";
    case "TERMINAL_FAILURE": return "terminal_failure";
    case "REQUIRED_FACTS_UNAVAILABLE": return "required_facts_unavailable";
    case "APPLIED": return "applied";
    default: return "required_facts_unavailable";
  }
}

function isSuccessfulStatutoryApplication(value?: string | null): boolean {
  return value === "APPLIED" || value === "SUCCESS" || value === "SUCCESSFUL" || value === "ACCEPTED";
}

function statutoryGateMessageForStatus(state: StatutoryDiscountWorkflowState): string {
  if (state.restoredAfterRestart && state.status === "application_processing") {
    return "Statutory payable-basis application remains in progress after restart. Use canonical readback before taking another action.";
  }

  const messages: Record<StatutoryDiscountWorkflowState["status"], string> = {
    none: "No statutory workflow is active.",
    draft: "Complete and submit the statutory request before cash acceptance.",
    submitting: "Statutory request submission is still pending.",
    awaiting_review: "Statutory request is awaiting Operator Console review.",
    approved_application_not_requested: "Statutory request was approved. Statutory payable-basis application has not been requested. Action: Submit Application Intent.",
    application_submitting: "Statutory payable-basis application submission is still pending.",
    application_processing: "Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.",
    applied: "Statutory payable basis is applied.",
    rejected: "Statutory request was rejected.",
    retryable_failure: "Statutory workflow has a retryable Central PMS failure.",
    terminal_failure: "Statutory workflow requires support.",
    required_facts_unavailable: "Required statutory payable-basis facts are unavailable.",
  };
  return messages[state.status];
}

function initialLookupState(
  basis: PayableBasisResponse | undefined,
  statutoryState: StatutoryDiscountWorkflowState,
  source: "fresh" | "restored",
): LookupState {
  if (!basis) return { status: "idle" };
  if (requiresStatutoryAmountAcknowledgement(basis, statutoryState)) {
    return {
      status: "amount_changed",
      previous: previousStatutoryBasis(basis, statutoryState),
      current: basis,
      correlationId: basis.correlationId,
      acknowledged: false,
    };
  }

  return { status: "resolved", basis, source };
}

function requiresStatutoryAmountAcknowledgement(basis: PayableBasisResponse, statutoryState: StatutoryDiscountWorkflowState): boolean {
  if (statutoryState.status !== "applied" || statutoryState.amountAcknowledged) return false;
  const originalAmount = statutoryState.originalAmountMinorUnits ?? basis.statutoryDiscountReadiness?.originalAmountMinorUnits;
  const finalAmount = statutoryState.finalPayableAmountMinorUnits ?? basis.statutoryDiscountReadiness?.finalPayableAmountMinorUnits ?? basis.authoritativeAmountMinorUnits;
  const originalSnapshot = statutoryState.originalTariffSnapshotId ?? basis.statutoryDiscountReadiness?.originalTariffSnapshotId;
  const appliedSnapshot = statutoryState.appliedTariffSnapshotId ?? basis.statutoryDiscountReadiness?.appliedTariffSnapshotId ?? basis.tariffSnapshotId;

  return (originalAmount != null && finalAmount != null && originalAmount !== finalAmount)
    || (Boolean(originalSnapshot) && Boolean(appliedSnapshot) && originalSnapshot !== appliedSnapshot);
}

function previousStatutoryBasis(current: PayableBasisResponse, statutoryState: StatutoryDiscountWorkflowState): PayableBasisResponse {
  const originalAmount = statutoryState.originalAmountMinorUnits ?? current.statutoryDiscountReadiness?.originalAmountMinorUnits ?? current.authoritativeAmountMinorUnits;
  const originalSnapshot = statutoryState.originalTariffSnapshotId ?? current.statutoryDiscountReadiness?.originalTariffSnapshotId ?? current.originalTariffSnapshotId ?? current.tariffSnapshotId;
  return {
    ...current,
    tariffSnapshotId: originalSnapshot,
    authoritativeAmountMinorUnits: originalAmount,
    netPayableMinorUnits: originalAmount,
    statutoryDiscountApplied: false,
    statutoryDiscountReadiness: null,
    appliedTariffSnapshotId: null,
    effectiveTariffSnapshotId: originalSnapshot,
    readyForCashAcceptance: false,
    cashAcceptanceReadiness: "BLOCKED",
    safeUserFacingClassification: "STATUTORY_AMOUNT_ACKNOWLEDGEMENT_REQUIRED",
    blockingReasonCodes: ["AMOUNT_CHANGED"],
  };
}

function basisFromState(state: PayableBasisStateSnapshot): PayableBasisResponse {
  const statutoryReadiness = parseStatutoryReadiness(state.statutoryDiscountStateJson);
  return {
    operation: "resolve",
    revalidationOutcome: state.revalidationOutcome,
    parkingSessionId: state.parkingSessionId,
    tariffSnapshotId: state.tariffSnapshotId,
    siteGroupId: state.siteGroupId,
    siteId: state.siteId,
    sitePosServerId: state.sitePosServerId,
    terminalId: state.terminalId,
    ticketReference: state.lookupReferenceType === "ticket" ? state.lookupReferenceValue : null,
    plateNumber: state.lookupReferenceType === "plate" ? state.lookupReferenceValue : null,
    entryTimestamp: null,
    parkingStatus: state.parkingStatus,
    paymentStatus: state.paymentStatus,
    authoritativeAmountMinorUnits: state.authoritativeAmountMinorUnits,
    currency: state.currency,
    tariffCalculatedAt: state.tariffCalculatedAt,
    tariffValidUntil: state.tariffValidUntil,
    feeValidUntil: state.feeValidUntil,
    sessionReadiness: state.sessionReadiness,
    tariffReadiness: state.tariffReadiness,
    paymentEligibility: state.paymentEligibility,
    terminalCashAvailability: state.terminalCashAvailability,
    fiscalReadiness: state.fiscalReadiness,
    salesInvoiceConfigurationReadiness: state.salesInvoiceConfigurationReadiness,
    cashAcceptanceReadiness: state.cashAcceptanceReadiness,
    readyForCashAcceptance: state.readyForCashAcceptance,
    blockingReasonCodes: state.blockingReasonCodes,
    retryable: state.retryable,
    safeUserFacingClassification: state.safeUserFacingClassification,
    correlationId: state.centralPmsCorrelationId,
    statutoryDiscountApplied: statutoryReadiness?.ready ?? false,
    statutoryDiscountReadiness: statutoryReadiness,
    originalTariffSnapshotId: statutoryReadiness?.originalTariffSnapshotId ?? null,
    effectiveTariffSnapshotId: statutoryReadiness?.appliedTariffSnapshotId ?? state.tariffSnapshotId,
    appliedTariffSnapshotId: statutoryReadiness?.appliedTariffSnapshotId ?? null,
  };
}

function blockerMessage(basis: PayableBasisResponse): string {
  if (basis.readyForCashAcceptance) return "Central PMS readiness is satisfied.";
  const first = basis.blockingReasonCodes[0] ?? basis.safeUserFacingClassification;
  const messages: Record<string, string> = {
    SESSION_NOT_FOUND: "Parking session not found.",
    VENDOR_SESSION_AMBIGUOUS: "Multiple matching sessions require review.",
    SESSION_NOT_PAYABLE: "Parking session is not active or payable.",
    PAYMENT_ALREADY_FINAL: "Parking session is already paid.",
    STALE_TARIFF: "Parking fee has expired and must be resolved again.",
    CASH_PAYMENT_RAIL_NOT_CONFIGURED: "Cash payment is unavailable for this Site or terminal.",
    SITE_POS_SERVER_NOT_CONFIGURED: "Site POS Server is not configured.",
    SALES_INVOICE_CONFIGURATION_NOT_READY: "Sales Invoice configuration is incomplete.",
    FISCAL_PATH_UNAVAILABLE: "Fiscal service is unavailable.",
    AMOUNT_CHANGED: "Parking fee changed before cash acceptance.",
    VENDOR_PMS_UNAVAILABLE: "Central PMS or Vendor PMS is temporarily unavailable.",
    STATUTORY_DISCOUNT_AWAITING_REVIEW: "Statutory request is awaiting Operator Console review.",
    STATUTORY_DISCOUNT_APPLICATION_NOT_REQUESTED: "Statutory request was approved. Statutory payable-basis application has not been requested. Action: Submit Application Intent.",
    STATUTORY_DISCOUNT_APPLICATION_PROCESSING: "Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.",
    STATUTORY_DISCOUNT_DECISION_REJECTED: "Statutory request was rejected.",
    STATUTORY_DISCOUNT_RETRYABLE_FAILURE: "Statutory workflow has a retryable Central PMS failure.",
    STATUTORY_DISCOUNT_TERMINAL_FAILURE: "Statutory workflow requires support.",
    STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE: "Required statutory payable-basis facts are unavailable.",
  };
  return messages[first] ?? basis.safeMessage ?? friendlyCode(first);
}

function friendlyCode(value?: string | null): string {
  if (!value) return "Unavailable";
  return value.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}

function formatCurrency(amountMinorUnits: number, currency: string): string {
  return new Intl.NumberFormat("en-PH", { style: "currency", currency }).format(amountMinorUnits / 100);
}

function maskPlate(plate?: string | null): string {
  if (!plate) return "Unavailable";
  const compact = plate.replace(/[^a-z0-9]/gi, "");
  if (compact.length <= 3) return "***";
  return `${compact.slice(0, 3)}-${"*".repeat(Math.max(2, compact.length - 3))}`;
}

function formatDate(value?: string | null): string {
  if (!value) return "Unavailable";
  return new Intl.DateTimeFormat("en-PH", { dateStyle: "medium", timeStyle: "medium" }).format(new Date(value));
}

function parseStatutoryReadiness(raw?: string | null) {
  const state = parseStatutoryState(raw);
  if (state.status === "none") return null;
  return {
    applicable: true,
    ready: state.status === "applied" && Boolean(state.payableBasisReady),
    statutoryDiscountDecisionCommandId: state.statutoryDiscountDecisionCommandId ?? null,
    statutoryDiscountPayableBasisApplicationCommandId: state.statutoryDiscountPayableBasisApplicationCommandId ?? null,
    entitlementType: state.entitlementType ?? null,
    decisionStatus: state.decisionStatus ?? null,
    decisionResultStatus: state.decisionResultStatus ?? null,
    decisionCommandStatus: state.decisionStatus ?? null,
    applicationCommandStatus: state.applicationCommandStatus ?? null,
    applicationResultClassification: state.applicationResultClassification ?? null,
    payableBasisReady: Boolean(state.payableBasisReady),
    payableBasisReadinessStatus: state.payableBasisReadinessStatus ?? "NOT_READY",
    payableBasisReadinessAction: state.payableBasisReadinessAction ?? null,
    originalTariffSnapshotId: state.originalTariffSnapshotId ?? null,
    appliedTariffSnapshotId: state.appliedTariffSnapshotId ?? null,
    originalAmountMinorUnits: state.originalAmountMinorUnits ?? null,
    vatExclusiveBasisAmountMinorUnits: state.vatExclusiveBasisAmountMinorUnits ?? null,
    vatAmountMinorUnits: state.vatAmountMinorUnits ?? null,
    vatTreatment: state.vatTreatment ?? null,
    statutoryDiscountAmountMinorUnits: state.statutoryDiscountAmountMinorUnits ?? null,
    finalPayableAmountMinorUnits: state.finalPayableAmountMinorUnits ?? null,
    currency: state.currency ?? null,
    retryable: Boolean(state.retryable),
    recoveryClassification: state.recoveryClassification ?? null,
    recoveryAction: state.recoveryAction ?? null,
    safeErrorCode: state.safeErrorCode ?? null,
    blockingReasonCode: state.safeErrorCode ?? "STATUTORY_DISCOUNT_AWAITING_REVIEW",
    message: state.payableBasisReadinessStatus ?? state.status,
  };
}
