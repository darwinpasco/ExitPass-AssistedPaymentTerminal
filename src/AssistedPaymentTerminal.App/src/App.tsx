import { useEffect, useMemo, useRef, useState } from "react";
import type { AptConfig, ConfigLoadResult } from "./config";
import { loadAptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import { cashierSafeSupportReference } from "./cashierSafeReferences";
import { createCentralPmsClient } from "./api/clientFactory";
import type {
  CentralPmsClient,
  CentralPmsResult,
  CentralPmsResolveResult,
  PayableBasisLookupResponse,
  PayableBasisReferenceType,
  PayableBasisResponse,
  ProjectedSessionResponse,
  StatutoryDiscountWorkflowState,
} from "./api/centralPmsTypes";
import { isProjectedSessionResponse } from "./api/centralPmsClient";
import { CashCapturePanel } from "./CashCapturePanel";
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
import { createWebViewLocalJournalBridge, type LocalJournalBridge, type LocalJournalHealth, type PayableBasisStateSnapshot } from "./localJournalBridge";
import { createWebViewStatutoryEvidenceBridge, type StatutoryEvidenceBridge, type StatutoryEvidenceChannelResponse } from "./statutoryEvidenceBridge";
import {
  createDevelopmentHumanSessionBridge,
  createWebViewHumanSessionBridge,
  mayUseDevelopmentHumanSessionFixture,
  type HumanSessionBridge,
  type HumanSessionBridgeResult,
  type HumanSessionState,
} from "./humanSessionBridge";

type LookupState =
  | { status: "idle" }
  | { status: "loading"; correlationId: string; requestId: number; referenceType: PayableBasisReferenceType; referenceValue: string }
  | { status: "resolved"; basis: PayableBasisLookupResponse; source: "fresh" | "restored"; acknowledgementRequired?: false }
  | { status: "amount_changed"; previous: PayableBasisResponse; current: PayableBasisResponse; correlationId: string; acknowledged: boolean }
  | { status: "failed"; result: Exclude<CentralPmsResolveResult, { ok: true }> };

type PreCashResult =
  | { ok: true; basis: PayableBasisResponse }
  | { ok: false; message: string };

const defaultBridge = createWebViewLocalJournalBridge();
const defaultEvidenceBridge = createWebViewStatutoryEvidenceBridge();
const defaultHumanSessionBridge = createWebViewHumanSessionBridge();
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
        renderTerminalShell={({ config, client, initialResolvedBasis, initialStatutoryState, bridge, evidenceBridge, initialCashEntryRequested, renderKey }) => (
          <TerminalShell
            key={renderKey}
            config={config}
            client={client}
            localJournalBridge={bridge}
            statutoryEvidenceBridge={evidenceBridge}
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

  const humanSessionBridge = mayUseDevelopmentHumanSessionFixture(configResult.config)
    ? createDevelopmentHumanSessionBridge(configResult.config)
    : defaultHumanSessionBridge;
  return (
    <AuthenticatedTerminal
      config={configResult.config}
      client={createCentralPmsClient(configResult.config)}
      humanSessionBridge={humanSessionBridge}
    />
  );
}

export function AuthenticatedTerminal({
  config,
  client,
  humanSessionBridge = defaultHumanSessionBridge,
  localJournalBridge = defaultBridge,
}: {
  config: AptConfig;
  client: CentralPmsClient;
  humanSessionBridge?: HumanSessionBridge;
  localJournalBridge?: LocalJournalBridge;
}) {
  const [humanState, setHumanState] = useState<HumanSessionState>({
    authenticationState: "LOADING",
    authenticated: false,
    deviceTrusted: false,
    shiftOperationsAuthorized: false,
    custodyOperationsAuthorized: false,
    cashOperationsAuthorized: false,
    userReference: null,
    username: null,
    displayName: null,
    audience: null,
    assurance: null,
    privilegedAccount: false,
    mfaRequired: false,
    idleExpiresAt: null,
    absoluteExpiresAt: null,
    safeSupportReference: "Unavailable",
    safeMessage: "Validating the device-bound cashier session online...",
    errorCode: null,
    retryable: false,
    activeShift: null,
    activeCashCustodySession: null,
  });

  function apply(result: HumanSessionBridgeResult) {
    if (result.ok) {
      setHumanState(result.payload);
      return;
    }
    setHumanState((current) => ({
      ...current,
      authenticationState: "UNAVAILABLE",
      authenticated: false,
      shiftOperationsAuthorized: false,
      custodyOperationsAuthorized: false,
      cashOperationsAuthorized: false,
      errorCode: result.error.code,
      safeMessage: result.error.message,
    }));
  }

  useEffect(() => {
    let cancelled = false;
    void humanSessionBridge.restore(createCorrelationId()).then((result) => {
      if (!cancelled) apply(result);
    });
    return () => { cancelled = true; };
  }, [humanSessionBridge]);

  useEffect(() => {
    if (!humanState.authenticated) return;
    const timer = window.setInterval(() => {
      void humanSessionBridge.refresh(createCorrelationId()).then(apply);
    }, 60_000);
    return () => window.clearInterval(timer);
  }, [humanSessionBridge, humanState.authenticated]);

  if (!humanState.authenticated) {
    return <CashierLoginPanel state={humanState} bridge={humanSessionBridge} onResult={apply} />;
  }

  return (
    <TerminalShell
      config={config}
      client={client}
      localJournalBridge={localJournalBridge}
      humanSessionBridge={humanSessionBridge}
      humanSessionState={humanState}
      onHumanSessionStateChange={setHumanState}
    />
  );
}

export function CashierLoginPanel({
  state,
  bridge,
  onResult,
}: {
  state: HumanSessionState;
  bridge: HumanSessionBridge;
  onResult: (result: HumanSessionBridgeResult) => void;
}) {
  const usernameRef = useRef<HTMLInputElement>(null);
  const loginInFlightRef = useRef(false);
  const [submitting, setSubmitting] = useState(false);

  async function login() {
    if (loginInFlightRef.current) return;
    loginInFlightRef.current = true;
    const username = usernameRef.current?.value ?? "";
    setSubmitting(true);
    try {
      onResult(await bridge.login(createCorrelationId(), username));
    } finally {
      loginInFlightRef.current = false;
      setSubmitting(false);
    }
  }

  return (
    <main className="login-shell" data-testid="apt-human-login-shell" data-app-ready="true">
      <section className="login-panel" aria-labelledby="cashier-login-heading">
        <p className="eyebrow">ExitPass Assisted Payment Terminal</p>
        <h1 id="cashier-login-heading">Cashier sign in</h1>
        <p>Terminal device trust must succeed before Central PMS can establish cashier authority.</p>
        <form autoComplete="off" onSubmit={(event) => { event.preventDefault(); void login(); }}>
          <label htmlFor="cashierUsername">Username</label>
          <input id="cashierUsername" ref={usernameRef} autoComplete="off" autoCapitalize="none" disabled={submitting} />
          <button type="submit" disabled={submitting || !state.deviceTrusted}>
            {submitting ? "Opening secure credential entry..." : "Sign in"}
          </button>
        </form>
        <p>Password entry is handled by a secure Windows dialog and is never stored in this web interface.</p>
        <div className={`status-notice ${state.errorCode ? "danger" : "info"}`} role={state.errorCode ? "alert" : "status"}>
          <strong>{state.errorCode ? "Cashier authority unavailable" : "Online authentication required"}</strong>
          <p>{state.safeMessage}</p>
          {state.safeSupportReference !== "Unavailable" && <p>Support reference: {state.safeSupportReference}</p>}
        </div>
        {(state.activeShift?.status === "Open" || state.activeCashCustodySession?.status === "Open") && (
          <div className="status-notice danger" role="status" aria-label="Preserved cash accountability">
            <strong>Cash accountability preserved</strong>
            <p>Current human authority is not available. Existing physical accountability remains open for governed recovery.</p>
            <dl className="human-session-summary">
              <div><dt>Shift</dt><dd>{state.activeShift?.status === "Open" ? "Open" : "Not open"}</dd></div>
              <div><dt>Cash custody</dt><dd>{state.activeCashCustodySession?.status === "Open" ? "Open" : "Not open"}</dd></div>
              <div><dt>New cash authority</dt><dd>Locked</dd></div>
            </dl>
          </div>
        )}
        <p>No offline login is available. This screen does not request an MFA code.</p>
      </section>
    </main>
  );
}

export function TerminalShell({
  config,
  client,
  localJournalBridge = defaultBridge,
  statutoryEvidenceBridge = defaultEvidenceBridge,
  initialReferenceType = "ticket",
  initialReferenceValue = "",
  restorePayableBasisOnMount = true,
  initialResolvedBasis,
  initialStatutoryState = noStatutoryWorkflow,
  initialCashEntryRequested = false,
  humanSessionBridge,
  humanSessionState,
  onHumanSessionStateChange,
}: {
  config: AptConfig;
  client: CentralPmsClient;
  localJournalBridge?: LocalJournalBridge;
  statutoryEvidenceBridge?: StatutoryEvidenceBridge;
  initialReferenceType?: PayableBasisReferenceType;
  initialReferenceValue?: string;
  restorePayableBasisOnMount?: boolean;
  initialResolvedBasis?: PayableBasisResponse;
  initialStatutoryState?: StatutoryDiscountWorkflowState;
  initialCashEntryRequested?: boolean;
  humanSessionBridge?: HumanSessionBridge;
  humanSessionState?: HumanSessionState;
  onHumanSessionStateChange?: (state: HumanSessionState) => void;
}) {
  const context = useMemo(() => buildTerminalContext(config, humanSessionState), [config, humanSessionState]);
  const [referenceValue, setReferenceValue] = useState(initialReferenceValue);
  const [ticketNumber, setTicketNumber] = useState(initialReferenceType === "ticket" ? initialReferenceValue : "");
  const [plateNumber, setPlateNumber] = useState(initialReferenceType === "plate" ? initialReferenceValue : "");
  const [lookupState, setLookupState] = useState<LookupState>(() => initialLookupState(initialResolvedBasis, initialStatutoryState, "fresh"));
  const [statutoryWorkflowState, setStatutoryWorkflowState] = useState<StatutoryDiscountWorkflowState>(initialStatutoryState);
  const [localPrerequisiteMessage, setLocalPrerequisiteMessage] = useState<string | null>(null);
  const [localJournalHealth, setLocalJournalHealth] = useState<LocalJournalHealth | null>(null);
  const [localJournalHealthMessage, setLocalJournalHealthMessage] = useState<string | null>(null);
  const latestRequestId = useRef(0);
  const statutoryWorkflowStateRef = useRef(statutoryWorkflowState);

  useEffect(() => {
    statutoryWorkflowStateRef.current = statutoryWorkflowState;
  }, [statutoryWorkflowState]);

  useEffect(() => {
    latestRequestId.current += 1;
    setReferenceValue(initialReferenceValue);
    setTicketNumber(initialReferenceType === "ticket" ? initialReferenceValue : "");
    setPlateNumber(initialReferenceType === "plate" ? initialReferenceValue : "");
    setLookupState(initialLookupState(initialResolvedBasis, initialStatutoryState, "fresh"));
    setStatutoryWorkflowState(initialStatutoryState);
    setLocalPrerequisiteMessage(null);
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

      setReferenceValue(result.payload.lookupReferenceValue);
      setTicketNumber(result.payload.lookupReferenceType === "ticket" ? result.payload.lookupReferenceValue : "");
      setPlateNumber(result.payload.lookupReferenceType === "plate" ? result.payload.lookupReferenceValue : "");
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

  useEffect(() => {
    let cancelled = false;
    const correlationId = createCorrelationId();

    async function loadLocalJournalHealth() {
      const healthRequest = {
        cashierId: context.cashierId,
        terminalId: context.terminalId,
        siteId: context.siteId,
        siteGroupId: context.siteGroupId,
        posServerId: context.posServerId,
      };
      const result = await localJournalBridge.health(correlationId, healthRequest);
      if (cancelled) {
        return;
      }

      if (result.ok) {
        setLocalJournalHealth(result.payload);
        postManualProofDiagnostic(context, healthRequest, result.payload);
        setLocalJournalHealthMessage(null);
        return;
      }

      setLocalJournalHealth(null);
      setLocalJournalHealthMessage(result.error.message);
    }

    void loadLocalJournalHealth();
    return () => {
      cancelled = true;
    };
  }, [context.cashierId, context.posServerId, context.siteGroupId, context.siteId, context.terminalId, localJournalBridge]);
  const displayedBasis = lookupState.status === "resolved" ? lookupState.basis : lookupState.status === "amount_changed" && lookupState.acknowledged ? lookupState.current : undefined;

  const authoritativeBasis = displayedBasis && !isProjectedSessionResponse(displayedBasis) ? displayedBasis : undefined;
  const tariffExpired = authoritativeBasis?.tariffValidUntil
    ? new Date(authoritativeBasis.tariffValidUntil).getTime() <= Date.now()
    : false;
  const statutoryWorkflowActive = statutoryWorkflowState.status !== "none";
  const centralReady = Boolean(authoritativeBasis?.readyForCashAcceptance) && !tariffExpired && lookupState.status !== "amount_changed";
  const statutoryCashGate = authoritativeBasis
    ? statutoryCashGateStatus(authoritativeBasis, statutoryWorkflowState, lookupState)
    : { ready: false, message: "No payable basis is resolved." };
  const cashBoundaryReady = centralReady && (!statutoryWorkflowActive || statutoryCashGate.ready);

  async function resolveReference() {
    const ticket = ticketNumber.trim();
    const plate = plateNumber.trim();
    if (!ticket && !plate) {
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
    setStatutoryWorkflowState(noStatutoryWorkflow);
    const primaryType: PayableBasisReferenceType = ticket ? "ticket" : "plate";
    const primaryValue = ticket || plate;
    setReferenceValue(primaryValue);
    setLookupState({ status: "loading", correlationId, requestId, referenceType: primaryType, referenceValue: primaryValue });

    const startedAt = performance.now();
    const [ticketResult, plateResult] = await Promise.all([
      ticket ? client.resolvePayableBasis("ticket", ticket, correlationId) : Promise.resolve(null),
      plate ? client.resolvePayableBasis("plate", plate, ticket ? createCorrelationId() : correlationId) : Promise.resolve(null),
    ]);
    recordPerformanceTiming("apt.resolve-payable-basis", startedAt);
    if (latestRequestId.current !== requestId) {
      return;
    }

    if (ticketResult && plateResult) {
      if (!ticketResult.ok) {
        setLookupState({ status: "failed", result: ticketResult });
        return;
      }
      if (!plateResult.ok) {
        setLookupState({ status: "failed", result: plateResult });
        return;
      }
      if (!sameResolvedSession(ticketResult.response, plateResult.response)) {
        setLookupState({ status: "failed", result: lookupMismatchFailure() });
        return;
      }
    }

    const result = ticketResult ?? plateResult;
    if (!result) return;
    if (result.ok) {
      if (isProjectedSessionResponse(result.response)) {
        setStatutoryWorkflowState(noStatutoryWorkflow);
        setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
        return;
      }
      const resolvedStatutoryState = statutoryStateFromPayableBasis(
        result.response,
        noStatutoryWorkflow,
      );
      setStatutoryWorkflowState(resolvedStatutoryState);
      await persistPayableBasis(result.response, primaryType, primaryValue, false, false, null, resolvedStatutoryState);
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return;
    }

    setLookupState({ status: "failed", result });
  }

  function resetLookup() {
    latestRequestId.current += 1;
    setLookupState({ status: "idle" });
    setReferenceValue("");
    setTicketNumber("");
    setPlateNumber("");
    setLocalPrerequisiteMessage(null);
  }

  function updateLookupInput(type: PayableBasisReferenceType, value: string) {
    latestRequestId.current += 1;
    if (type === "ticket") {
      setTicketNumber(value);
    } else {
      setPlateNumber(value);
    }
    setLookupState({ status: "idle" });
    setLocalPrerequisiteMessage(null);
  }

  async function preCashRevalidate(currentBasis: PayableBasisResponse): Promise<PreCashResult> {
    if (!currentBasis.readyForCashAcceptance) {
      return { ok: false, message: "Central PMS has not marked this payable basis ready for cash acceptance." };
    }

    const correlationId = createCorrelationId();
    const requestId = latestRequestId.current + 1;
    latestRequestId.current = requestId;
    const startedAt = performance.now();
    const result = await client.revalidatePayableBasis(currentBasis, correlationId);
    recordPerformanceTiming("apt.pre-cash-revalidation", startedAt);

    if (latestRequestId.current !== requestId) {
      return { ok: false, message: "The payable basis changed while revalidation was pending. Resolve the current reference again." };
    }

    if (!result.ok) {
      setLookupState({ status: "failed", result });
      return { ok: false, message: result.error.message };
    }

    const outcome = result.response.revalidationOutcome ?? "UNKNOWN";
    let nextStatutoryState = statutoryStateFromPayableBasis(result.response, statutoryWorkflowState, outcome);
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
      if (nextStatutoryState.status !== "none") {
        const evidenceResult = await statutoryEvidenceBridge.revalidate(
          createCorrelationId(),
          nextStatutoryState.statutoryDiscountDecisionCommandId ?? "",
        );
        if (!evidenceResult.ok) {
          return { ok: false, message: evidenceResult.error.message };
        }

        nextStatutoryState = {
          ...nextStatutoryState,
          evidenceRecovery: evidenceRecoveryFromResponse(evidenceResult.payload, nextStatutoryState.statutoryDiscountDecisionCommandId ?? ""),
          updatedAt: new Date().toISOString(),
        };
        statutoryWorkflowStateRef.current = nextStatutoryState;
        setStatutoryWorkflowState(nextStatutoryState);
        await persistPayableBasis(
          result.response,
          result.response.ticketReference ? "ticket" : "plate",
          result.response.ticketReference ?? result.response.plateNumber ?? referenceValue,
          false,
          false,
          currentBasis.authoritativeAmountMinorUnits,
          nextStatutoryState,
        );
        if (!evidenceRevalidationPassed(evidenceResult.payload, result.response)) {
          return { ok: false, message: evidenceResult.payload.safeMessage };
        }
      }
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return { ok: true, basis: result.response };
    }

    if (outcome === "PASSED_UNCHANGED" && result.response.readyForCashAcceptance) {
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return { ok: false, message: "Central PMS revalidation did not return the same applied statutory payable basis. Resolve or check statutory status again before accepting cash." };
    }

    if (outcome === "AMOUNT_CHANGED") {
      setLookupState({ status: "amount_changed", previous: currentBasis, current: result.response, correlationId, acknowledged: false });
      return { ok: false, message: "Parking fee was updated. Review the new amount before recording cash." };
    }

    if (outcome === "TARIFF_EXPIRED") {
      const refreshType: PayableBasisReferenceType = currentBasis.ticketReference ? "ticket" : "plate";
      const refreshValue = currentBasis.ticketReference ?? currentBasis.plateNumber ?? referenceValue;
      const refreshStartedAt = performance.now();
      const refreshed = await client.resolvePayableBasis(refreshType, refreshValue, createCorrelationId());
      recordPerformanceTiming("apt.expired-fee-refresh", refreshStartedAt);
      if (!refreshed.ok) {
        setLookupState({ status: "failed", result: refreshed });
        return { ok: false, message: refreshed.error.message };
      }
      if (isProjectedSessionResponse(refreshed.response)) {
        setStatutoryWorkflowState(noStatutoryWorkflow);
        setLookupState({ status: "resolved", basis: refreshed.response, source: "fresh" });
        return { ok: false, message: "Session found from projection; live payable amount is temporarily unavailable." };
      }
      await persistPayableBasis(
        refreshed.response,
        refreshType,
        refreshValue,
        true,
        true,
        currentBasis.authoritativeAmountMinorUnits,
        statutoryStateFromPayableBasis(refreshed.response, nextStatutoryState),
      );
      setLookupState({ status: "amount_changed", previous: currentBasis, current: refreshed.response, correlationId, acknowledged: false });
      return { ok: false, message: "Parking fee was updated. Review the current amount before recording cash." };
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

  async function authorizeHumanAndRevalidate(basis: PayableBasisResponse): Promise<PreCashResult> {
    if (humanSessionBridge) {
      const authorization = await humanSessionBridge.authorizeCash(createCorrelationId());
      if (!authorization.ok) {
        return { ok: false, message: authorization.error.message };
      }
      onHumanSessionStateChange?.(authorization.payload);
      if (!authorization.payload.cashOperationsAuthorized
        || !authorization.payload.activeShift
        || !authorization.payload.activeCashCustodySession) {
        return { ok: false, message: authorization.payload.safeMessage || "Current online cashier authority is required before cash can be accepted." };
      }
    }
    return preCashRevalidate(basis);
  }

  const activeShift = humanSessionState?.activeShift ?? localJournalHealth?.operationalState?.activeShift ?? null;
  const activeCashCustodySession = humanSessionState?.activeCashCustodySession ?? localJournalHealth?.operationalState?.activeCashCustodySession ?? null;
  const localPersistenceCashReady = localJournalHealth?.localPersistence?.cashOperationsAllowed === true;
  const durableShiftActive = activeShift?.status === "Open";
  const durableCashCustodyActive = activeCashCustodySession?.status === "Open";
  const humanCashAuthorized = humanSessionState?.cashOperationsAuthorized ?? true;
  const localPrerequisitesReady = localPersistenceCashReady
    && durableShiftActive
    && durableCashCustodyActive
    && humanCashAuthorized;
  const localPrerequisiteBlockers = localCashPrerequisiteBlockers(
    localJournalHealth,
    localJournalHealthMessage,
    activeShift,
    activeCashCustodySession,
  );
  if (!humanCashAuthorized) {
    localPrerequisiteBlockers.unshift(humanSessionState?.safeMessage ?? "Current online cashier authority is required.");
  }

  return (
    <main className="terminal-shell" data-testid="apt-terminal-shell" data-app-ready="true">
      <header className="brand-header">
        <div>
          <p className="eyebrow">ExitPass Assisted Payment Terminal</p>
          <h1>Cashier-Assisted Terminal</h1>
        </div>
      </header>

      <section className="workflow-stack">
        {humanSessionBridge && humanSessionState && onHumanSessionStateChange && (
          <HumanSessionPanel state={humanSessionState} bridge={humanSessionBridge} context={context} onStateChange={onHumanSessionStateChange} />
        )}
        {(!humanSessionBridge || !humanSessionState) && <OperationalContextPanel context={context} health={localJournalHealth} />}
        <section className="lookup-panel" aria-labelledby="lookup-heading">
          <div className="section-heading">
            <p className="eyebrow">1. Find Ticket</p>
            <h2 id="lookup-heading">Find parking session</h2>
          </div>
          <form
            className="lookup-form"
            onSubmit={(event) => {
              event.preventDefault();
              void resolveReference();
            }}
          >
            <div className="lookup-fields">
              <label htmlFor="ticketNumber">
                Ticket number
                <input
                  id="ticketNumber"
                  value={ticketNumber}
                  onChange={(event) => updateLookupInput("ticket", event.target.value)}
                  placeholder="Scan or type ticket number"
                  autoFocus
                  autoComplete="off"
                />
              </label>
              <label htmlFor="plateNumber">
                Plate number
                <input
                  id="plateNumber"
                  value={plateNumber}
                  onChange={(event) => updateLookupInput("plate", event.target.value)}
                  placeholder="Type plate number"
                  autoComplete="off"
                />
              </label>
            </div>
            <div className="lookup-actions">
              <button type="submit" disabled={(!ticketNumber.trim() && !plateNumber.trim()) || lookupState.status === "loading"}>
                Resolve
              </button>
            </div>
          </form>

          {lookupState.status === "loading" && (
            <StatusNotice tone="info" title="Resolving parking session">
              Finding the current parking fee and payment status.
            </StatusNotice>
          )}

          {lookupState.status === "failed" && <FailureNotice result={lookupState.result} onReset={resetLookup} />}
          {lookupState.status === "amount_changed" && (
            <AmountChangedNotice previous={lookupState.previous} current={lookupState.current} onAcknowledge={() => void acknowledgeAmountChange()} />
          )}

          {displayedBasis && (
            <div className="cashier-workflow">
              {isProjectedSessionResponse(displayedBasis)
                ? <ProjectionSessionSummary basis={displayedBasis} onRetry={() => void resolveReference()} />
                : <SessionSummary basis={displayedBasis} statutoryState={statutoryWorkflowState} />}
              {authoritativeBasis && <section className="payment-section" aria-labelledby="receive-payment-heading">
                <div className="section-heading">
                  <p className="eyebrow">3. Receive Payment</p>
                  <h2 id="receive-payment-heading">Receive payment</h2>
                </div>
                {!localPrerequisitesReady && (
                  <StatusNotice tone="danger" title={localPrerequisiteBlockers[0] ?? "Cashier session is not ready"} dataTestId="local-cash-prerequisites-notice">
                    Open or resume your own shift and cash custody in Cashier Session.
                  </StatusNotice>
                )}
                {!cashBoundaryReady && (
                  <StatusNotice tone="danger" title={statutoryWorkflowActive && !statutoryCashGate.ready ? statutoryStatusSummary(statutoryWorkflowState).message : blockerMessage(authoritativeBasis)}>
                    Resolve the blocker before recording cash.
                  </StatusNotice>
                )}
                {localPrerequisiteMessage && <p className="cash-error" role="alert">{localPrerequisiteMessage}</p>}
                <CashCapturePanel
                  config={config}
                  context={context}
                  session={authoritativeBasis}
                  tariffExpired={tariffExpired}
                  cashAcceptanceReady={localPrerequisitesReady && (cashBoundaryReady || tariffExpired)}
                  cashAcceptanceBlockedMessage={statutoryWorkflowActive && !statutoryCashGate.ready ? statutoryCashGate.message : blockerMessage(authoritativeBasis)}
                  activeCashCustodySessionId={activeCashCustodySession?.id ?? null}
                  onBeforeCashReceived={authorizeHumanAndRevalidate}
                  onLocalPrerequisiteFailure={setLocalPrerequisiteMessage}
                  bridge={localJournalBridge}
                />
              </section>}
            </div>
          )}
        </section>
      </section>
    </main>
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

function postManualProofDiagnostic(
  context: TerminalContext,
  healthRequest: {
    cashierId: string;
    terminalId: string;
    siteId: string;
    siteGroupId: string;
    posServerId: string;
  },
  health: LocalJournalHealth,
) {
  const activeShift = health.operationalState?.activeShift ?? null;
  const activeCustody = health.operationalState?.activeCashCustodySession ?? null;
  const renderedShiftLabel = activeShift?.status === "Open"
    ? "OPEN"
    : activeShift?.status === "Closed"
      ? "CLOSED"
      : "No active shift";

  try {
    window.chrome?.webview?.postMessage(JSON.stringify({
      source: "apt-manual-proof-diagnostic",
      event: "localJournalHealthReceived",
      shiftFilterSent: false,
      bridgeRequestScope: healthRequest,
      bridgeReturnedActiveShiftId: activeShift?.id ?? null,
      bridgeReturnedActiveShiftStatus: activeShift?.status ?? null,
      reactReceivedActiveShiftId: activeShift?.id ?? null,
      reactReceivedActiveShiftStatus: activeShift?.status ?? null,
      reactRenderedShiftLabel: renderedShiftLabel,
      activeCustodyId: activeCustody?.id ?? null,
      activeCustodyStatus: activeCustody?.status ?? null,
      cashBlockedWithoutCustody: activeShift?.status === "Open" && activeCustody?.status !== "Open",
    }));
  } catch {
    // Manual proof diagnostics must never affect terminal rendering.
  }
}

export function HumanSessionPanel({
  state,
  bridge,
  context,
  onStateChange,
}: {
  state: HumanSessionState;
  bridge: HumanSessionBridge;
  context?: TerminalContext;
  onStateChange: (state: HumanSessionState) => void;
}) {
  const [openingCashAmount, setOpeningCashAmount] = useState("0.00");
  const [closingCashAmount, setClosingCashAmount] = useState("0.00");
  const [busy, setBusy] = useState<string | null>(null);
  const shiftOpen = state.activeShift?.status === "Open";
  const custodyOpen = state.activeCashCustodySession?.status === "Open";
  const [expanded, setExpanded] = useState(!shiftOpen || !custodyOpen);

  async function invoke(name: string, action: () => Promise<HumanSessionBridgeResult>) {
    setBusy(name);
    try {
      const result = await action();
      if (result.ok) {
        onStateChange(result.payload);
        const nextShiftOpen = result.payload.activeShift?.status === "Open";
        const nextCustodyOpen = result.payload.activeCashCustodySession?.status === "Open";
        setExpanded(!nextShiftOpen || !nextCustodyOpen);
      } else {
        onStateChange({
          ...state,
          authenticationState: "LOCKED",
          authenticated: false,
          shiftOperationsAuthorized: false,
          custodyOperationsAuthorized: false,
          cashOperationsAuthorized: false,
          errorCode: result.error.code,
          safeMessage: result.error.message,
          retryable: false,
        });
      }
    } finally {
      setBusy(null);
    }
  }

  const expectedCash = state.activeCashCustodySession?.expectedClosingCashAmount;
  const closingAmount = Number(closingCashAmount);
  const variance = expectedCash != null && Number.isFinite(closingAmount) ? closingAmount - expectedCash : null;

  return (
    <section className="human-session-panel" aria-labelledby="human-session-heading">
      <div className="cashier-header">
        <h2 id="human-session-heading" className="visually-hidden">Cashier session</h2>
        <dl className="cashier-header-summary">
          <div><dt>Cashier</dt><dd>{state.displayName || state.username || "Cashier"}</dd></div>
          <div><dt>Site</dt><dd>{context?.siteName ?? "Unavailable"}</dd></div>
          <div><dt>Terminal</dt><dd>{context?.terminalDisplayName ?? "Unavailable"}</dd></div>
          <div><dt>Shift</dt><dd data-testid="cashier-header-shift">{shiftOpen ? "OPEN" : "CLOSED"}</dd></div>
          <div><dt>Cash Custody</dt><dd data-testid="cashier-header-custody">{custodyOpen ? "OPEN" : "CLOSED"}</dd></div>
        </dl>
        <div className="cashier-header-actions">
          <button type="button" className="secondary-action" aria-expanded={expanded} onClick={() => setExpanded((current) => !current)}>
            Cashier Session
          </button>
          <button type="button" className="secondary-action" disabled={busy !== null} onClick={() => void invoke("logout", () => bridge.logout(createCorrelationId()))}>
            Sign out
          </button>
        </div>
      </div>

      {state.errorCode === "OPEN_CUSTODY_LOGOUT_BLOCKED" && (
        <p className="cash-error" role="alert">Sign out is unavailable while you have open cash custody.</p>
      )}

      {expanded && (
        <div className="cashier-session-body" aria-label="Cashier Session">
          <section className="cashier-session-group">
            <h3>Shift</h3>
            <p>Status: {shiftOpen ? "Open" : "No active shift"}</p>
            {!shiftOpen && (
              <button type="button" disabled={busy !== null || !state.shiftOperationsAuthorized} onClick={() => void invoke("shift", () => bridge.openOrResumeShift(createCorrelationId()))}>
                Open Shift
              </button>
            )}
            {shiftOpen && !custodyOpen && <p>Your own open shift is ready for cash custody.</p>}
          </section>

          <section className="cashier-session-group">
            <h3>Cash Custody</h3>
            {!custodyOpen ? (
              <div className="session-form-row">
                <label htmlFor="openingCashAmount">Opening Cash Amount</label>
                <input id="openingCashAmount" inputMode="decimal" value={openingCashAmount} onChange={(event) => setOpeningCashAmount(event.target.value)} disabled={busy !== null} />
                <button
                  type="button"
                  disabled={busy !== null || !shiftOpen || !state.custodyOperationsAuthorized || !Number.isFinite(Number(openingCashAmount)) || Number(openingCashAmount) < 0}
                  onClick={() => void invoke("custody", () => bridge.openOrResumeCustody(createCorrelationId(), Number(openingCashAmount)))}
                >
                  Open Cash Custody
                </button>
              </div>
            ) : (
              <div className="cashier-closing-grid">
                <div><span>Opening Cash</span><strong>{formatCurrencyFromMajor(state.activeCashCustodySession?.openingCashAmount ?? 0)}</strong></div>
                <div><span>Expected Cash</span><strong>{expectedCash == null ? "Calculated on close" : formatCurrencyFromMajor(expectedCash)}</strong></div>
                <label htmlFor="closingCashAmount">Actual Closing Cash</label>
                <input id="closingCashAmount" inputMode="decimal" value={closingCashAmount} onChange={(event) => setClosingCashAmount(event.target.value)} disabled={busy !== null} />
                <div><span>Variance</span><strong>{variance == null ? "Calculated on close" : formatCurrencyFromMajor(variance)}</strong></div>
                <button
                  type="button"
                  disabled={busy !== null || !state.custodyOperationsAuthorized || !Number.isFinite(closingAmount) || closingAmount < 0}
                  onClick={() => void invoke("close-custody", () => bridge.closeOwnCustody(createCorrelationId(), closingAmount))}
                >
                  Close Cash Custody
                </button>
              </div>
            )}
          </section>

          {shiftOpen && (
            <section className="cashier-session-group end-shift">
              <h3>End Shift</h3>
              <button type="button" disabled={busy !== null || custodyOpen || !state.shiftOperationsAuthorized} onClick={() => void invoke("close-shift", () => bridge.closeOwnShift(createCorrelationId()))}>
                Close Cashier Shift
              </button>
            </section>
          )}
        </div>
      )}
    </section>
  );
}

function OperationalContextPanel({ context, health }: { context: TerminalContext; health: LocalJournalHealth | null }) {
  const activeShift = health?.operationalState?.activeShift ?? null;
  const activeCustody = health?.operationalState?.activeCashCustodySession ?? null;
  const recoveredShiftStatus = activeShift?.status === "Open"
    ? "OPEN"
    : activeShift?.status === "Closed"
      ? "CLOSED"
      : "No active shift";
  const summaryRows = [
    ["Site", context.siteName, "operational-site-summary"],
    ["Cashier", context.cashierDisplayName, "operational-cashier-summary"],
    ["Shift", recoveredShiftStatus, "operational-shift-summary"],
    ["Terminal", context.terminalDisplayName, "operational-terminal-summary"],
    ["POS readiness", context.posServerId ? "Configured" : "Unavailable", "operational-pos-readiness-summary"],
  ];

  const detailRows = [
    ["Terminal", context.terminalId ? "Configured" : "Unavailable", "configured-terminal-id"],
    ["Site scope", context.siteId ? "Configured" : "Unavailable", "configured-site-id"],
    ["Site-group scope", context.siteGroupId ? "Configured" : "Unavailable", "configured-site-group-id"],
    ["POS Server", context.posServerId ? "Configured" : "Unavailable", "configured-pos-server-id"],
    ["Recovered shift", activeShift ? activeShift.status : "None", "recovered-shift-id"],
    ["Cash custody", activeCustody ? activeCustody.status : "None", "active-custody-id"],
    ["Central PMS", context.centralPmsConnectionMode, "configured-central-pms-mode"],
  ];

  return (
    <aside className="context-panel compact" aria-label="Operational context">
      <div className="context-summary-grid">
        {summaryRows.map(([label, value, testId]) => (
          <div key={label} className="context-chip"><span>{label}</span><strong data-testid={testId}>{value}</strong></div>
        ))}
      </div>
      <details className="terminal-details">
        <summary>Terminal details</summary>
        <dl>{detailRows.map(([label, value, testId]) => <div key={label} className="context-row"><dt>{label}</dt><dd data-testid={testId}>{value}</dd></div>)}</dl>
      </details>
    </aside>
  );
}

function localCashPrerequisiteBlockers(
  health: LocalJournalHealth | null,
  healthMessage: string | null,
  activeShift: HumanSessionState["activeShift"],
  activeCustody: HumanSessionState["activeCashCustodySession"],
): string[] {
  const blockers: string[] = [];
  if (healthMessage) {
    blockers.push(`Local operational state could not be read: ${healthMessage}`);
    return blockers;
  }

  if (!health) {
    blockers.push("Local operational state is still being checked.");
    return blockers;
  }

  if (health.localPersistence?.cashOperationsAllowed !== true) {
    blockers.push(health.localPersistence?.safeAction ?? "Encrypted local persistence is not ready for cash operations.");
  }

  if (activeShift?.status !== "Open") {
    blockers.push("Open or resume your cashier shift.");
  }

  if (activeCustody?.status !== "Open") {
    blockers.push("Open or resume your cash custody.");
  }

  return blockers;
}

function SessionSummary({ basis, statutoryState }: { basis: PayableBasisResponse; statutoryState: StatutoryDiscountWorkflowState }) {
  const statutory = statutoryStatusSummary(statutoryState);
  const discountAmount = basis.statutoryDiscountReadiness?.statutoryDiscountAmountMinorUnits ?? 0;
  const taxPending = "Confirmed on Sales Invoice";
  const customerInformation = basis.customerInformationSubmitted === true
    ? "Submitted"
    : basis.customerInformationSubmitted === false
      ? "Not submitted"
      : "Unavailable";

  return (
    <section className="session-summary" aria-label="Parking Session Details" data-testid="payable-basis-summary">
      <div className="section-heading session-summary-heading">
        <p className="eyebrow">2. Parking Session Details</p>
        <h2>Parking session details</h2>
      </div>
      <dl className="approved-session-details">
        <div><dt>Ticket reference</dt><dd>{basis.ticketReference ?? "Unavailable"}</dd></div>
        <div><dt>Plate number</dt><dd>{basis.plateNumber ?? "Unavailable"}</dd></div>
        <div><dt>Entry timestamp</dt><dd>{formatDate(basis.entryTimestamp)}</dd></div>
        <div><dt>Parking duration</dt><dd>{basis.parkingDurationDisplay ?? formatParkingDuration(basis.entryTimestamp, basis.currentFeeCalculationTime ?? basis.tariffCalculatedAt)}</dd></div>
        <div><dt>Tariff calculated</dt><dd>{formatDate(basis.tariffCalculatedAt)}</dd></div>
        <div><dt>Fee valid until</dt><dd>{formatDate(basis.feeValidUntil ?? basis.tariffValidUntil)}</dd></div>
        <div><dt>Discount Request</dt><dd>{statutory.label}</dd></div>
        {statutory.reason && <div><dt>Reason</dt><dd>{statutory.reason}</dd></div>}
        <div><dt>Customer Information for Sales Invoice</dt><dd>{customerInformation}</dd></div>
      </dl>
      <div className="amount-summary" aria-label="Amount summary">
        <div className="amount-summary-total"><span>Amount Due</span><strong data-testid="payable-basis-amount">{formatCurrency(basis.authoritativeAmountMinorUnits, basis.currency)}</strong></div>
        <dl>
          <div><dt>Discount Amount</dt><dd>{formatCurrency(discountAmount, basis.currency)}</dd></div>
          <div><dt>VATable Sales</dt><dd>{formatOptionalCurrency(basis.vatableSalesMinorUnits, basis.currency, taxPending)}</dd></div>
          <div><dt>VAT Amount</dt><dd>{formatOptionalCurrency(basis.vatAmountMinorUnits ?? basis.statutoryDiscountReadiness?.vatAmountMinorUnits, basis.currency, taxPending)}</dd></div>
          <div><dt>VAT Exempt Sales</dt><dd>{formatOptionalCurrency(basis.vatExemptSalesMinorUnits, basis.currency, taxPending)}</dd></div>
          <div><dt>Zero Rated Sales</dt><dd>{formatOptionalCurrency(basis.zeroRatedSalesMinorUnits, basis.currency, taxPending)}</dd></div>
          <div className="amount-summary-total-row"><dt>Total Amount</dt><dd>{formatCurrency(basis.authoritativeAmountMinorUnits, basis.currency)}</dd></div>
        </dl>
      </div>
    </section>
  );
}

function ProjectionSessionSummary({ basis, onRetry }: { basis: ProjectedSessionResponse; onRetry: () => void }) {
  return (
    <section className="session-summary" aria-label="Projected parking session" data-testid="projection-session-summary">
      <div className="section-heading session-summary-heading">
        <p className="eyebrow">2. Parking Session Details</p>
        <h2>Session found from projection</h2>
      </div>
      <dl className="approved-session-details">
        <div><dt>Ticket reference</dt><dd>{basis.ticketReference ?? "Unavailable"}</dd></div>
        <div><dt>Plate number</dt><dd>{basis.plateNumber ?? "Unavailable"}</dd></div>
        <div><dt>Entry timestamp</dt><dd>{formatDate(basis.entryTimestamp)}</dd></div>
        <div><dt>Site</dt><dd>{basis.siteName ?? "Parking Site"}</dd></div>
        <div><dt>Session source</dt><dd>Continuity projection</dd></div>
      </dl>
      <StatusNotice tone="info" title="Live payable amount temporarily unavailable" dataTestId="projection-payable-basis-blocked">
        <p>Cash acceptance remains blocked until Central PMS obtains an authoritative live payable basis.</p>
        <button className="secondary-action" type="button" onClick={onRetry}>Retry live fee</button>
      </StatusNotice>
    </section>
  );
}

function sameResolvedSession(left: PayableBasisLookupResponse, right: PayableBasisLookupResponse): boolean {
  if (isProjectedSessionResponse(left) || isProjectedSessionResponse(right)) {
    return isProjectedSessionResponse(left) &&
      isProjectedSessionResponse(right) &&
      left.vendorSessionProjectionId === right.vendorSessionProjectionId &&
      left.siteId === right.siteId &&
      left.siteGroupId === right.siteGroupId;
  }

  return left.parkingSessionId === right.parkingSessionId;
}

function AmountChangedNotice({ previous, current, onAcknowledge }: { previous: PayableBasisResponse; current: PayableBasisResponse; onAcknowledge: () => void }) {
  const statutoryApplied = current.statutoryDiscountReadiness?.applicable === true;
  return (
    <StatusNotice tone="danger" title="Parking fee changed before cash acceptance">
      <p>Review the new authoritative payable basis before accepting cash. CASH_RECEIVED remains blocked until acknowledgement and a later unchanged revalidation.</p>
      <dl className="central-pms-details">
        <div><dt>Previous amount</dt><dd>{formatCurrency(previous.authoritativeAmountMinorUnits, previous.currency)}</dd></div>
        <div><dt>{statutoryApplied ? "Authoritative applied amount" : "New amount"}</dt><dd>{formatCurrency(current.authoritativeAmountMinorUnits, current.currency)}</dd></div>
        <div><dt>Tariff update</dt><dd>Authoritative version changed</dd></div>
        {statutoryApplied && <div><dt>Statutory decision</dt><dd>{current.statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId ? "Recorded" : "Unavailable"}</dd></div>}
        {statutoryApplied && <div><dt>Statutory application</dt><dd>{current.statutoryDiscountReadiness?.statutoryDiscountPayableBasisApplicationCommandId ? "Recorded" : "Unavailable"}</dd></div>}
        <div><dt>Recalculated at</dt><dd>{formatDate(current.tariffCalculatedAt)}</dd></div>
      </dl>
      <button className="secondary-action" type="button" onClick={onAcknowledge}>Acknowledge new amount</button>
    </StatusNotice>
  );
}

function FailureNotice({ result, onReset }: { result: Exclude<CentralPmsResult, { ok: true }>; onReset: () => void }) {
  const supportReference = cashierSafeSupportReference((result.error as { supportReference?: string | null }).supportReference);
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
      {supportReference && <p className="support-line">Support reference: {supportReference}</p>}
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
    const parsedWithLegacy = JSON.parse(raw) as StatutoryDiscountWorkflowState & { safeEvidenceReference?: unknown };
    const { safeEvidenceReference: _discardedLegacyEvidenceReference, ...parsed } = parsedWithLegacy;
    if (!parsed?.status) return noStatutoryWorkflow;
    return {
      ...parsed,
      restoredAfterRestart,
      evidenceRecovery: restoredAfterRestart && parsed.evidenceRecovery
        ? {
            ...parsed.evidenceRecovery,
            authoritative: false,
            readyForAptPreCash: false,
            lifecycleClassification: "STALE_LOCAL_STATE",
            fileReselectionRequired: true,
          }
        : parsed.evidenceRecovery,
    };
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
    return { ready: false, message: "Review the updated amount before recording cash." };
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

  const evidenceReadiness = basis.statutoryEvidenceReadiness ??
    basis.readinessDimensions?.find((dimension) => dimension.name === "statutoryEvidenceReadiness") ?? null;
  if (!evidenceReadiness?.ready) {
    return { ready: false, message: evidenceReadiness?.message ?? "Central PMS has not marked statutory evidence ready for cash acceptance." };
  }

  if (!statutoryState.evidenceRecovery?.readyForAptPreCash) {
    return { ready: false, message: "The discount request is not ready for payment." };
  }

  if (basis.blockingReasonCodes.length > 0) {
    return { ready: false, message: blockerMessage(basis) };
  }

  return { ready: true, message: "Statutory payable basis is ready for cash acceptance." };
}

function evidenceRecoveryFromResponse(
  response: StatutoryEvidenceChannelResponse,
  decisionCommandId: string,
) {
  return {
    authoritative: false as const,
    statutoryDiscountDecisionCommandId: decisionCommandId,
    evidenceSetReference: response.evidenceSetReference ?? null,
    evidenceItemReference: response.evidenceItemReference ?? null,
    opaqueUploadSessionReference: null,
    uploadSessionExpiresAt: null,
    lifecycleClassification: response.lifecycleClassification ?? "UNKNOWN_FAIL_CLOSED",
    replacementPosture: response.replacementPosture,
    readyForReview: response.readyForReview,
    readyForAptPreCash: response.readyForAptPreCash,
    retryable: response.retryable,
    blockingReasonCode: response.blockingReasonCode ?? null,
    correlationId: response.correlationId,
    lastSynchronizedAt: new Date().toISOString(),
    fileReselectionRequired: false,
  };
}

function evidenceRevalidationPassed(response: StatutoryEvidenceChannelResponse, basis: PayableBasisResponse): boolean {
  const evidenceReadiness = basis.statutoryEvidenceReadiness ??
    basis.readinessDimensions?.find((dimension) => dimension.name === "statutoryEvidenceReadiness") ?? null;
  return response.readyForAptPreCash &&
    response.classification !== "REJECTED" &&
    ["NOT_REQUIRED", "APPLIED"].includes(response.lifecycleClassification ?? "") &&
    evidenceReadiness?.ready === true;
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

function formatCurrencyFromMajor(amount: number): string {
  return new Intl.NumberFormat("en-PH", { style: "currency", currency: "PHP" }).format(amount);
}

function formatOptionalCurrency(amountMinorUnits: number | null | undefined, currency: string, fallback: string): string {
  return amountMinorUnits == null ? fallback : formatCurrency(amountMinorUnits, currency);
}

function formatParkingDuration(entryTimestamp?: string | null, calculationTimestamp?: string | null): string {
  if (!entryTimestamp || !calculationTimestamp) return "Unavailable";
  const milliseconds = new Date(calculationTimestamp).getTime() - new Date(entryTimestamp).getTime();
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return "Unavailable";
  const totalMinutes = Math.floor(milliseconds / 60_000);
  const days = Math.floor(totalMinutes / 1_440);
  const hours = Math.floor((totalMinutes % 1_440) / 60);
  const minutes = totalMinutes % 60;
  return [days ? `${days}d` : "", hours ? `${hours}h` : "", `${minutes}m`].filter(Boolean).join(" ");
}

function statutoryStatusSummary(state: StatutoryDiscountWorkflowState): { label: string; message: string; reason?: string } {
  switch (state.status) {
    case "none": return { label: "None", message: "No statutory discount request is active." };
    case "draft":
    case "submitting":
    case "awaiting_review":
    case "approved_application_not_requested":
    case "application_submitting":
    case "application_processing":
      return { label: "Submitted", message: "Statutory discount request is being processed." };
    case "applied": return { label: "Approved", message: "Approved statutory discount is included in the amount due." };
    case "rejected": return { label: "Rejected", message: "Statutory discount request was rejected.", reason: "The submitted request was not approved." };
    default: return { label: "Submitted", message: "Statutory discount status is temporarily unavailable." };
  }
}

function lookupMismatchFailure(): Exclude<CentralPmsResult, { ok: true }> {
  return {
    ok: false,
    kind: "invalid_request",
    error: {
      errorCode: "REFERENCE_MISMATCH",
      message: "Ticket and plate do not identify the same parking session.",
      correlationId: "local-validation",
      retryable: false,
    },
  };
}

function recordPerformanceTiming(name: string, startedAt: number): void {
  try {
    performance.measure(name, { start: startedAt, end: performance.now() });
  } catch {
    // Performance diagnostics must never affect cashier workflow.
  }
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
