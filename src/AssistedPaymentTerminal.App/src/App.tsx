import { useEffect, useMemo, useRef, useState } from "react";
import type { AptConfig, ConfigLoadResult } from "./config";
import { loadAptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import { createCentralPmsClient } from "./api/clientFactory";
import type { CentralPmsClient, CentralPmsResult, PayableBasisReferenceType, PayableBasisResponse } from "./api/centralPmsTypes";
import { CashCapturePanel } from "./CashCapturePanel";
import { ReceiptVisualSmokeShell, shouldUseReceiptVisualSmoke } from "./ReceiptVisualSmoke";
import {
  PayableBasisVisualSmokeShell,
  shouldUsePayableBasisVisualSmoke,
} from "./PayableBasisVisualSmoke";
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

  return <TerminalShell config={configResult.config} client={createCentralPmsClient(configResult.config)} />;
}

export function TerminalShell({
  config,
  client,
  localJournalBridge = defaultBridge,
  initialReferenceType = "ticket",
  initialReferenceValue = "",
  restorePayableBasisOnMount = true,
}: {
  config: AptConfig;
  client: CentralPmsClient;
  localJournalBridge?: LocalJournalBridge;
  initialReferenceType?: PayableBasisReferenceType;
  initialReferenceValue?: string;
  restorePayableBasisOnMount?: boolean;
}) {
  const context = useMemo(() => buildTerminalContext(config), [config]);
  const [referenceType, setReferenceType] = useState<PayableBasisReferenceType>(initialReferenceType);
  const [referenceValue, setReferenceValue] = useState(initialReferenceValue);
  const [lookupState, setLookupState] = useState<LookupState>({ status: "idle" });
  const [localPrerequisiteMessage, setLocalPrerequisiteMessage] = useState<string | null>(null);
  const [cashEntryRequested, setCashEntryRequested] = useState(false);
  const [preCashStatus, setPreCashStatus] = useState<"idle" | "revalidating" | "passed" | "blocked">("idle");
  const latestRequestId = useRef(0);

  useEffect(() => {
    latestRequestId.current += 1;
    setReferenceType(initialReferenceType);
    setReferenceValue(initialReferenceValue);
    setLookupState({ status: "idle" });
    setLocalPrerequisiteMessage(null);
    setCashEntryRequested(false);
    setPreCashStatus("idle");
  }, [initialReferenceType, initialReferenceValue]);

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
      setLookupState({ status: "resolved", basis: basisFromState(result.payload), source: "restored" });
    }

    void restore();
    return () => {
      cancelled = true;
    };
  }, [context.siteId, context.terminalId, localJournalBridge, restorePayableBasisOnMount]);
  const displayedBasis = lookupState.status === "resolved" ? lookupState.basis : lookupState.status === "amount_changed" && lookupState.acknowledged ? lookupState.current : undefined;
  const tariffExpired = displayedBasis ? new Date(displayedBasis.tariffValidUntil).getTime() <= Date.now() : false;
  const centralReady = Boolean(displayedBasis?.readyForCashAcceptance) && !tariffExpired && lookupState.status !== "amount_changed";

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
    setLookupState({ status: "loading", correlationId, requestId, referenceType, referenceValue: trimmed });

    const result = await client.resolvePayableBasis(referenceType, trimmed, correlationId);
    if (latestRequestId.current !== requestId) {
      return;
    }

    if (result.ok) {
      await persistPayableBasis(result.response, referenceType, trimmed, false, false, null);
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

  async function preCashRevalidate(currentBasis: PayableBasisResponse): Promise<PreCashResult> {
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
    await persistPayableBasis(
      result.response,
      result.response.ticketReference ? "ticket" : "plate",
      result.response.ticketReference ?? result.response.plateNumber ?? referenceValue,
      outcome === "AMOUNT_CHANGED",
      false,
      currentBasis.authoritativeAmountMinorUnits,
    );

    if (outcome === "PASSED_UNCHANGED" && result.response.readyForCashAcceptance) {
      setCashEntryRequested(false);
      setPreCashStatus("idle");
      setLookupState({ status: "resolved", basis: result.response, source: "fresh" });
      return { ok: true, basis: result.response };
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
    await persistPayableBasis(
      lookupState.current,
      lookupState.current.ticketReference ? "ticket" : "plate",
      lookupState.current.ticketReference ?? lookupState.current.plateNumber ?? referenceValue,
      true,
      true,
      lookupState.previous.authoritativeAmountMinorUnits,
    );
    setReferenceValue(lookupState.current.ticketReference ?? lookupState.current.plateNumber ?? referenceValue);
    setLookupState({ status: "resolved", basis: lookupState.current, source: "fresh" });
  }

  async function persistPayableBasis(
    basis: PayableBasisResponse,
    persistedReferenceType: PayableBasisReferenceType,
    persistedReferenceValue: string,
    amountChanged: boolean,
    cashierAcknowledgementRequired: boolean,
    priorAmountMinorUnits: number | null,
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
    });
  }

  async function handleContinueToCash(currentBasis: PayableBasisResponse) {
    if (!centralReady || !localPrerequisitesReady) {
      setPreCashStatus("blocked");
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
    setLookupState({ status: "idle" });
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
                  <SessionSummary basis={displayedBasis} restored={lookupState.status === "resolved" && lookupState.source === "restored"} />
                  <ReadinessPanel basis={displayedBasis} tariffExpired={tariffExpired} />
                  {!localPrerequisitesReady && (
                    <StatusNotice tone="danger" title="Local cash prerequisites unavailable">
                      Local cash capture is disabled in this terminal profile. Local prerequisites can only restrict Central PMS readiness.
                    </StatusNotice>
                  )}
                  {localPrerequisiteMessage && <p className="cash-error">{localPrerequisiteMessage}</p>}
                </div>
                <div className="cash-column">
                  <PreCashBoundaryPanel
                    basis={displayedBasis}
                    centralReady={centralReady}
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
                      cashAcceptanceReady={centralReady && localPrerequisitesReady}
                      cashAcceptanceBlockedMessage={blockerMessage(displayedBasis)}
                      onBeforeCashReceived={preCashRevalidate}
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
    <section className="status-notice info" aria-label="Pre-cash acceptance boundary">
      <h3>Pre-cash acceptance</h3>
      <p>{statusText}</p>
      <p>CASH_RECEIVED has not occurred. Continue to Cash runs Central PMS revalidation before local cash custody can be recorded.</p>
      <dl className="central-pms-details">
        <div><dt>readyForCashAcceptance</dt><dd>{basis.readyForCashAcceptance ? "true" : "false"}</dd></div>
        <div><dt>Local prerequisites</dt><dd>{localPrerequisitesReady ? "Satisfied" : "Blocked"}</dd></div>
        <div><dt>Tariff snapshot</dt><dd>{basis.tariffSnapshotId}</dd></div>
      </dl>
      <button type="button" className="primary-action" disabled={disabled} onClick={onContinue}>
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

function SessionSummary({ basis, restored }: { basis: PayableBasisResponse; restored: boolean }) {
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
    <section className="session-summary" aria-label="Resolved parking session">
      <div className="amount-band">
        <div>
          <p className="eyebrow">Authoritative payable basis</p>
          <strong>{amount}</strong>
        </div>
        <span className={basis.readyForCashAcceptance ? "status-badge success" : "status-badge"}>{restored ? "Previously resolved" : basis.readyForCashAcceptance ? "Ready" : "Blocked"}</span>
      </div>
      <dl className="summary-primary">{primaryRows.map(([label, value]) => <div key={label} className="summary-row"><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      <details className="session-details">
        <summary>Session details</summary>
        <dl>{secondaryRows.map(([label, value]) => <div key={label} className="summary-row"><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      </details>
    </section>
  );
}

function ReadinessPanel({ basis, tariffExpired }: { basis: PayableBasisResponse; tariffExpired: boolean }) {
  const ready = basis.readyForCashAcceptance && !tariffExpired;
  const dimensions = [
    ["Session", basis.sessionReadiness ?? basis.parkingStatus],
    ["Tariff", tariffExpired ? "EXPIRED" : basis.tariffReadiness ?? "UNKNOWN"],
    ["Payment eligibility", basis.paymentEligibility ?? basis.paymentStatus],
    ["Terminal cash", basis.terminalCashAvailability ?? "UNKNOWN"],
    ["Sales Invoice configuration", basis.salesInvoiceConfigurationReadiness ?? "UNKNOWN"],
    ["Fiscal readiness", basis.fiscalReadiness ?? "UNKNOWN"],
  ];

  return (
    <StatusNotice tone={ready ? "success" : basis.retryable ? "info" : "danger"} title={ready ? "Ready for cash acceptance" : "Cash acceptance blocked"}>
      <p>{ready ? "Central PMS confirmed all pre-cash readiness checks. Revalidation will still run immediately before CASH_RECEIVED." : blockerMessage(basis)}</p>
      <dl className="central-pms-details">
        {dimensions.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{friendlyCode(value)}</dd></div>)}
      </dl>
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
  return (
    <StatusNotice tone="danger" title="Parking fee changed before cash acceptance">
      <p>Review the new authoritative payable basis before accepting cash. CASH_RECEIVED remains blocked until acknowledgement and a later unchanged revalidation.</p>
      <dl className="central-pms-details">
        <div><dt>Previous amount</dt><dd>{formatCurrency(previous.authoritativeAmountMinorUnits, previous.currency)}</dd></div>
        <div><dt>New amount</dt><dd>{formatCurrency(current.authoritativeAmountMinorUnits, current.currency)}</dd></div>
        <div><dt>New tariff snapshot</dt><dd>{current.tariffSnapshotId}</dd></div>
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

function StatusNotice({ tone, title, children }: { tone: "info" | "success" | "danger"; title: string; children: React.ReactNode }) {
  return <section className={`status-notice ${tone}`} role={tone === "danger" ? "alert" : "status"}><h3>{title}</h3><div>{children}</div></section>;
}

function basisFromState(state: PayableBasisStateSnapshot): PayableBasisResponse {
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
  };
  return basis.safeMessage ?? messages[first] ?? friendlyCode(first);
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
