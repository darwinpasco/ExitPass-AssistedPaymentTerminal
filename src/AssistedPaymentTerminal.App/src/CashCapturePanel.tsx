import { useEffect, useMemo, useState } from "react";
import type { AptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import type { ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import type { TerminalContext } from "./terminalContext";
import {
  createWebViewLocalJournalBridge,
  type BridgeError,
  type CashCustodySessionSnapshot,
  type CashTenderSnapshot,
  type CentralPmsCashFiscalStatus,
  type CentralPmsCashSubmissionStatus,
  type LocalJournalBridge,
  type LocalJournalHealth,
  type LocalTenderReadback,
} from "./localJournalBridge";

type PanelStatus =
  | { kind: "idle" }
  | { kind: "checking"; message: string }
  | { kind: "ready"; health: LocalJournalHealth; session: CashCustodySessionSnapshot; readback: LocalTenderReadback }
  | { kind: "success"; tender: CashTenderSnapshot; readback: LocalTenderReadback; correlationId: string }
  | { kind: "conflict"; existingTenderId?: string; existingState?: string; message: string; correlationId: string }
  | { kind: "error"; message: string };

type CentralPmsPanelStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashSubmissionStatus; correlationId: string }
  | { kind: "error"; message: string; correlationId: string };

type FiscalPanelStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashFiscalStatus; correlationId: string }
  | { kind: "error"; message: string; correlationId: string };

const defaultBridge = createWebViewLocalJournalBridge();
const denominations = [
  { code: "PHP-1000", value: 1000 },
  { code: "PHP-500", value: 500 },
  { code: "PHP-100", value: 100 },
  { code: "PHP-50", value: 50 },
  { code: "PHP-20", value: 20 },
  { code: "PHP-10", value: 10 },
  { code: "PHP-5", value: 5 },
  { code: "PHP-1", value: 1 },
];

export function CashCapturePanel({
  config,
  context,
  session,
  tariffExpired,
  bridge = defaultBridge,
}: {
  config: AptConfig;
  context: TerminalContext;
  session: ResolveVendorParkingResponse;
  tariffExpired: boolean;
  bridge?: LocalJournalBridge;
}) {
  const amountDue = session.netPayableMinorUnits / 100;
  const [amountTenderedText, setAmountTenderedText] = useState(amountDue.toFixed(2));
  const [cashierAttested, setCashierAttested] = useState(false);
  const [denominationCounts, setDenominationCounts] = useState<Record<string, number>>({});
  const [status, setStatus] = useState<PanelStatus>({ kind: "idle" });
  const [centralPmsStatus, setCentralPmsStatus] = useState<CentralPmsPanelStatus>({ kind: "idle" });
  const [fiscalStatus, setFiscalStatus] = useState<FiscalPanelStatus>({ kind: "idle" });

  const amountTendered = Number(amountTenderedText);
  const changeDue = Number.isFinite(amountTendered) ? Math.max(0, amountTendered - amountDue) : 0;

  useEffect(() => {
    setAmountTenderedText(amountDue.toFixed(2));
    setCashierAttested(false);
    setDenominationCounts({});
    setCentralPmsStatus({ kind: "idle" });
    setFiscalStatus({ kind: "idle" });
  }, [amountDue, session.parkingSessionId]);

  useEffect(() => {
    if (!config.nonLiveCashCaptureEnabled || tariffExpired) {
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setStatus({ kind: "checking", message: "Checking local journal readiness..." });

    async function prepare() {
      const health = await bridge.health(correlationId);
      if (cancelled) return;

      if (!health.ok || !health.payload.enabled || !health.payload.healthy) {
        setStatus({ kind: "error", message: health.ok ? "Local journal bridge is disabled." : health.error.message });
        return;
      }

      const sessionResult = await bridge.createOrGetDevelopmentSession(createCorrelationId(), {
        cashierId: context.cashierId,
        authenticatedCashierSessionReference: `dev-auth:${context.cashierId}:${context.shiftId}`,
        cashierShiftId: context.shiftId,
        terminalId: context.terminalId,
        siteId: context.siteId,
        siteGroupId: context.siteGroupId,
        posServerId: context.posServerId,
        openingCashAmount: 0,
      });

      if (cancelled) return;
      if (!sessionResult.ok) {
        setStatus({ kind: "error", message: sessionResult.error.message });
        return;
      }

      const readback = await bridge.readTenderByParkingSession(createCorrelationId(), session.parkingSessionId);
      if (cancelled) return;

      setStatus({
        kind: "ready",
        health: health.payload,
        session: sessionResult.payload,
        readback: readback.ok ? readback.payload : { tender: null, events: [] },
      });
    }

    void prepare();

    return () => {
      cancelled = true;
    };
  }, [bridge, config.nonLiveCashCaptureEnabled, context, session.parkingSessionId, tariffExpired]);

  const existingTender =
    status.kind === "ready" ? status.readback.tender : status.kind === "success" ? status.readback.tender ?? status.tender : null;
  const centralPmsConfig = centralPmsSubmissionConfig(config);
  const fiscalConfig = centralPmsFiscalConfig(config);
  const centralPmsCommand = centralPmsStatus.kind === "ready" ? centralPmsStatus.status.command : null;
  const canonicalPaymentConfirmed = centralPmsCommand?.status === "Confirmed";

  useEffect(() => {
    if (!config.centralPmsCashSubmissionEnabled || !existingTender || existingTender.currentLocalState !== "CashReceived") {
      return;
    }

    if (!centralPmsConfig.valid) {
      setCentralPmsStatus({ kind: "unavailable", message: centralPmsConfig.message });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setCentralPmsStatus({ kind: "loading", message: "Checking Central PMS status..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashSubmissionStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setCentralPmsStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setCentralPmsStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [bridge, centralPmsConfig.message, centralPmsConfig.valid, config.centralPmsCashSubmissionEnabled, existingTender?.id, existingTender?.currentLocalState]);

  useEffect(() => {
    if (!existingTender || existingTender.currentLocalState !== "CashReceived" || !canonicalPaymentConfirmed) {
      setFiscalStatus({ kind: "idle" });
      return;
    }

    if (!config.centralPmsFiscalIssuanceEnabled) {
      setFiscalStatus({ kind: "idle" });
      return;
    }

    if (!fiscalConfig.valid) {
      setFiscalStatus({ kind: "unavailable", message: fiscalConfig.message });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setFiscalStatus({ kind: "loading", message: "Checking fiscal issuance status..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashFiscalStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setFiscalStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setFiscalStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [
    bridge,
    canonicalPaymentConfirmed,
    config.centralPmsFiscalIssuanceEnabled,
    existingTender?.id,
    existingTender?.currentLocalState,
    fiscalConfig.message,
    fiscalConfig.valid,
  ]);

  const denominationPayload = useMemo(
    () =>
      denominations.map((denomination) => ({
        denominationCode: denomination.code,
        denominationValue: denomination.value,
        quantity: denominationCounts[denomination.code] ?? 0,
      })).filter((denomination) => denomination.quantity > 0),
    [denominationCounts],
  );

  if (!config.nonLiveCashCaptureEnabled) {
    return null;
  }

  if (tariffExpired) {
    return (
      <section className="cash-capture-panel unavailable" aria-label="Non-live cash capture unavailable">
        <p className="eyebrow">Non-live development simulation</p>
        <h2>Cash capture unavailable</h2>
        <p>Cash custody recording is blocked until the payable basis is current and non-expired.</p>
      </section>
    );
  }

  async function recordCashReceived() {
    if (!Number.isFinite(amountTendered) || amountTendered < amountDue) {
      setStatus({ kind: "error", message: "Amount tendered must be greater than or equal to amount due." });
      return;
    }

    if (!cashierAttested) {
      setStatus({ kind: "error", message: "Cashier attestation is required before CASH_RECEIVED." });
      return;
    }

    if (status.kind !== "ready" && status.kind !== "success") {
      setStatus({ kind: "error", message: "Local journal is not ready." });
      return;
    }

    const cashSession = status.kind === "ready" ? status.session : undefined;
    if (!cashSession) {
      setStatus({ kind: "error", message: "Local cash-custody session is not available." });
      return;
    }

    const correlationId = createCorrelationId();
    const tariffSnapshotId = session.effectiveTariffSnapshotId ?? session.tariffSnapshotId;
    const started = await bridge.startTender(correlationId, {
      cashCustodySessionId: cashSession.id,
      parkingSessionId: session.parkingSessionId,
      tariffSnapshotId,
      currency: session.currency,
      amountDue,
      amountTendered,
      localIdempotencyIdentity: `local-cash:${session.parkingSessionId}:${tariffSnapshotId}`,
    });

    if (!started.ok) {
      setConflict(started.error, correlationId);
      return;
    }

    const received = await bridge.recordCashReceived(correlationId, {
      localCashTenderId: started.payload.id,
      cashierAttested,
      denominations: denominationPayload,
    });

    if (!received.ok) {
      setStatus({ kind: "error", message: received.error.message });
      return;
    }

    const readback = await bridge.readTenderByParkingSession(createCorrelationId(), session.parkingSessionId);
    setStatus({
      kind: "success",
      tender: received.payload,
      readback: readback.ok ? readback.payload : { tender: received.payload, events: [] },
      correlationId,
    });
  }

  async function submitOrReadbackCentralPms() {
    if (!existingTender) {
      setCentralPmsStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!centralPmsConfig.valid) {
      setCentralPmsStatus({ kind: "unavailable", message: centralPmsConfig.message });
      return;
    }

    const correlationId = createCorrelationId();
    setCentralPmsStatus({ kind: "loading", message: "Submitting or checking Central PMS..." });
    const result = await bridge.submitOrReadbackCentralPmsCashSubmission(correlationId, existingTender.id);
    if (result.ok) {
      setCentralPmsStatus({ kind: "ready", status: result.payload, correlationId });
    } else {
      setCentralPmsStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function submitOrReadbackFiscal() {
    if (!existingTender) {
      setFiscalStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!canonicalPaymentConfirmed) {
      setFiscalStatus({ kind: "error", message: "Canonical payment must be confirmed before fiscal issuance.", correlationId: "unavailable" });
      return;
    }

    if (!fiscalConfig.valid) {
      setFiscalStatus({ kind: "unavailable", message: fiscalConfig.message });
      return;
    }

    const correlationId = createCorrelationId();
    setFiscalStatus({ kind: "loading", message: "Submitting or checking fiscal issuance..." });
    const result = await bridge.submitOrReadbackCentralPmsCashFiscal(correlationId, existingTender.id);
    if (result.ok) {
      setFiscalStatus({ kind: "ready", status: result.payload, correlationId });
    } else {
      setFiscalStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function reloadLocalTender() {
    const readback = await bridge.readTenderByParkingSession(createCorrelationId(), session.parkingSessionId);
    if (!readback.ok) {
      setStatus({ kind: "error", message: readback.error.message });
      return;
    }

    if (status.kind === "ready") {
      setStatus({ ...status, readback: readback.payload });
    } else {
      setStatus({ kind: "success", tender: readback.payload.tender!, readback: readback.payload, correlationId: "readback" });
    }
  }

  async function attemptDuplicateTender() {
    if (status.kind !== "ready" && status.kind !== "success") {
      return;
    }

    const cashSessionId = status.kind === "ready" ? status.session.id : status.readback.tender?.cashCustodySessionId;
    if (!cashSessionId) {
      return;
    }

    const correlationId = createCorrelationId();
    const duplicate = await bridge.startTender(correlationId, {
      cashCustodySessionId: cashSessionId,
      parkingSessionId: session.parkingSessionId,
      tariffSnapshotId: session.effectiveTariffSnapshotId ?? session.tariffSnapshotId,
      currency: session.currency,
      amountDue,
      amountTendered: amountDue,
      localIdempotencyIdentity: `local-cash-duplicate:${session.parkingSessionId}`,
    });

    if (!duplicate.ok) {
      setConflict(duplicate.error, correlationId);
    }
  }

  function setConflict(error: BridgeError, correlationId: string) {
    setStatus({
      kind: "conflict",
      existingTenderId: error.detail?.existingCashTenderId,
      existingState: error.detail?.existingCashTenderState,
      message: error.message,
      correlationId,
    });
  }

  return (
    <section className="cash-capture-panel" aria-label="Non-live cash custody capture">
      <div className="section-heading">
        <p className="eyebrow">Non-live development simulation</p>
        <h2>Local cash custody capture</h2>
      </div>

      <div className="authority-warning" role="status">
        <strong>Local cash only.</strong> Canonical payment not submitted. Fiscal issuance not started. Exit authorization unavailable.
      </div>

      {status.kind === "checking" && <p className="support-line">{status.message}</p>}
      {status.kind === "error" && <p className="cash-error" role="alert">{status.message}</p>}
      {status.kind === "conflict" && (
        <div className="cash-error" role="alert">
          <strong>Duplicate local cash tender rejected.</strong>
          <p>{status.message}</p>
          <p>Existing local tender ID: {status.existingTenderId ?? "Unavailable"}</p>
          <p>Existing local state: {status.existingState ?? "Unavailable"}</p>
          <p>Correlation ID: {status.correlationId}</p>
        </div>
      )}

      {existingTender && status.kind !== "success" && (
        <div className="cash-readback">
          <h3>Existing local custody record</h3>
          <p>Local tender ID: {existingTender.id}</p>
          <p>Local state: {existingTender.currentLocalState}</p>
          <p>Correlation ID: {existingTender.correlationId}</p>
          <button className="secondary-action" type="button" onClick={attemptDuplicateTender}>
            Attempt duplicate cash tender
          </button>
        </div>
      )}

      {!existingTender && (
        <>
          <div className="cash-grid">
            <label>
              Amount due
              <input value={formatAmount(amountDue)} readOnly />
            </label>
            <label>
              Amount tendered
              <input
                type="number"
                min="0"
                step="0.01"
                value={amountTenderedText}
                onChange={(event) => setAmountTenderedText(event.target.value)}
              />
            </label>
            <label>
              Change due
              <input value={formatAmount(changeDue)} readOnly />
            </label>
          </div>

          <fieldset className="denomination-grid">
            <legend>Optional denomination inputs</legend>
            {denominations.map((denomination) => (
              <label key={denomination.code}>
                {denomination.code}
                <input
                  type="number"
                  min="0"
                  step="1"
                  value={denominationCounts[denomination.code] ?? 0}
                  onChange={(event) =>
                    setDenominationCounts((current) => ({
                      ...current,
                      [denomination.code]: Math.max(0, Math.floor(Number(event.target.value) || 0)),
                    }))
                  }
                />
              </label>
            ))}
          </fieldset>

          <label className="attestation-row">
            <input
              type="checkbox"
              checked={cashierAttested}
              onChange={(event) => setCashierAttested(event.target.checked)}
            />
            I attest: cash received at this terminal.
          </label>

          <button type="button" onClick={() => void recordCashReceived()}>
            Record Cash Received
          </button>
        </>
      )}

      {status.kind === "success" && (
        <div className="cash-success" role="status">
          <h3>Cash received locally</h3>
          <p>Local tender ID: {status.tender.id}</p>
          <p>Local state: {status.tender.currentLocalState}</p>
          <p>Correlation ID: {status.correlationId}</p>
          <p>Event history entries: {status.readback.events.length}</p>
        </div>
      )}

      {config.centralPmsCashSubmissionEnabled && existingTender?.currentLocalState === "CashReceived" && (
        <CentralPmsCanonicalPaymentPanel
          centralPmsStatus={centralPmsStatus}
          onSubmitOrReadback={() => void submitOrReadbackCentralPms()}
        />
      )}

      {existingTender?.currentLocalState === "CashReceived" && canonicalPaymentConfirmed && (
        <CentralPmsFiscalIssuancePanel
          enabled={config.centralPmsFiscalIssuanceEnabled}
          fiscalStatus={fiscalStatus}
          onSubmitOrReadback={() => void submitOrReadbackFiscal()}
        />
      )}

      <button className="secondary-action" type="button" onClick={() => void reloadLocalTender()}>
        Reload local tender
      </button>
    </section>
  );
}

function formatAmount(value: number): string {
  return value.toFixed(2);
}

function centralPmsSubmissionConfig(config: AptConfig): { valid: boolean; message: string } {
  if (!config.centralPmsCashSubmissionEnabled) {
    return { valid: false, message: "Central PMS cash submission is disabled." };
  }

  try {
    const url = new URL(config.centralPmsBaseUrl);
    if (!["http:", "https:"].includes(url.protocol) || url.hostname.endsWith(".example.invalid")) {
      return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for cash submission." };
    }

    return { valid: true, message: "Central PMS cash submission is available." };
  } catch {
    return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for cash submission." };
  }
}

function centralPmsFiscalConfig(config: AptConfig): { valid: boolean; message: string } {
  if (!config.centralPmsFiscalIssuanceEnabled) {
    return { valid: false, message: "Central PMS fiscal issuance is disabled." };
  }

  try {
    const url = new URL(config.centralPmsBaseUrl);
    if (!["http:", "https:"].includes(url.protocol) || url.hostname.endsWith(".example.invalid")) {
      return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for fiscal issuance." };
    }

    return { valid: true, message: "Central PMS fiscal issuance is available." };
  } catch {
    return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for fiscal issuance." };
  }
}

function CentralPmsCanonicalPaymentPanel({
  centralPmsStatus,
  onSubmitOrReadback,
}: {
  centralPmsStatus: CentralPmsPanelStatus;
  onSubmitOrReadback: () => void;
}) {
  if (centralPmsStatus.kind === "unavailable") {
    return (
      <section className="central-pms-panel unavailable" aria-label="Central PMS canonical payment">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (centralPmsStatus.kind === "loading") {
    return (
      <section className="central-pms-panel" aria-label="Central PMS canonical payment">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (centralPmsStatus.kind === "error") {
    return (
      <section className="central-pms-panel blocked" aria-label="Central PMS canonical payment" role="alert">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
        <p>Correlation ID: {centralPmsStatus.correlationId}</p>
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Submit / Check Central PMS
        </button>
      </section>
    );
  }

  const command = centralPmsStatus.kind === "ready" ? centralPmsStatus.status.command : null;
  const status = command?.status ?? "Pending";
  const confirmed = status === "Confirmed";
  const conflict = status === "Conflict";
  const rejected = status === "Rejected";
  const retry =
    status === "Pending" || status === "RetryPending" || status === "ReadbackRequired" || status === "Submitting" || !command;
  const replay = command?.resultClassification === "IDEMPOTENT_REPLAY";

  return (
    <section
      className={`central-pms-panel ${confirmed ? "confirmed" : conflict || rejected ? "blocked" : ""}`}
      aria-label="Central PMS canonical payment"
      role={conflict || rejected ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Central PMS canonical payment</h3>
        <strong>{confirmed ? "Canonical payment confirmed" : conflict ? "Conflict - support review required" : rejected ? "Rejected - reconciliation required" : retry ? "Canonical payment not yet confirmed" : command?.statusLabel}</strong>
      </div>

      <p>Local cash custody: cash received locally.</p>
      {confirmed ? (
        <>
          <p>{replay ? "Idempotent replay confirmed the existing command; no new charge was created." : "Central PMS accepted the persisted cash-payment command."}</p>
          <dl className="central-pms-details">
            <div>
              <dt>Payment-attempt ID</dt>
              <dd>{command?.canonicalPaymentAttemptId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Payment-confirmation ID</dt>
              <dd>{command?.canonicalPaymentConfirmationId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Result classification</dt>
              <dd>{command?.resultClassification ?? "CONFIRMED"}</dd>
            </div>
            <div>
              <dt>Confirmation timestamp</dt>
              <dd>{command?.confirmedAt ? formatDateTime(command.confirmedAt) : "Unavailable"}</dd>
            </div>
            <div>
              <dt>Correlation ID</dt>
              <dd>{command?.originalCorrelationId ?? "Unavailable"}</dd>
            </div>
          </dl>
        </>
      ) : conflict ? (
        <>
          <p>Central PMS reported a semantic conflict. Supervisor or support review is required.</p>
          <p>Existing local tender reference: {command?.terminalCashTenderId ?? "Unavailable"}</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "CONFLICT"}</p>
        </>
      ) : rejected ? (
        <>
          <p>Central PMS rejected the persisted command. Local CASH_RECEIVED evidence is retained.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "REJECTED"}</p>
          <p>Support or reconciliation handling is required.</p>
        </>
      ) : (
        <>
          <p>Status: {command?.statusLabel ?? "Pending"}</p>
          <p>{command?.lastSafeErrorCode ? `Safe error code: ${command.lastSafeErrorCode}` : "Use the persisted command to submit or check Central PMS."}</p>
        </>
      )}

      <p>Fiscal issuance not started. Exit authorization unavailable.</p>
      {!confirmed && !conflict && !rejected && (
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Submit / Check Central PMS
        </button>
      )}
    </section>
  );
}

function CentralPmsFiscalIssuancePanel({
  enabled,
  fiscalStatus,
  onSubmitOrReadback,
}: {
  enabled: boolean;
  fiscalStatus: FiscalPanelStatus;
  onSubmitOrReadback: () => void;
}) {
  if (!enabled) {
    return (
      <section className="central-pms-panel fiscal unavailable" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>Central PMS fiscal issuance is disabled.</p>
        <p>Cash received locally. Canonical payment confirmed. Fiscal issuance not started. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "unavailable") {
    return (
      <section className="central-pms-panel fiscal unavailable" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment remains confirmed. Fiscal issuance not completed. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "loading") {
    return (
      <section className="central-pms-panel fiscal" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment confirmed. Fiscal issuance pending. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "error") {
    return (
      <section className="central-pms-panel fiscal blocked" aria-label="Central PMS fiscal issuance" role="alert">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment remains confirmed. Fiscal issuance incomplete. Supervisor or support review is required.</p>
        <p>Correlation ID: {fiscalStatus.correlationId}</p>
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Issue / Check Fiscal Document
        </button>
      </section>
    );
  }

  const command = fiscalStatus.kind === "ready" ? fiscalStatus.status.command : null;
  const recorded = command?.status === "Recorded";
  const conflict = command?.status === "Conflict";
  const rejected = command?.status === "Rejected";
  const uncertain =
    command?.status === "ReadbackRequired" || command?.status === "RetryPending" || command?.status === "Unknown";
  const pending = !command || command.status === "Pending" || command.status === "Submitting" || uncertain;
  const replay = command?.resultClassification === "IDEMPOTENT_REPLAY";

  return (
    <section
      className={`central-pms-panel fiscal ${recorded ? "confirmed" : conflict || rejected ? "blocked" : ""}`}
      aria-label="Central PMS fiscal issuance"
      role={conflict || rejected ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Fiscal issuance</h3>
        <strong>
          {recorded
            ? "Fiscal document recorded"
            : conflict
              ? "Fiscal conflict - support review required"
              : rejected
                ? "Fiscal rejected - reconciliation required"
                : uncertain
                  ? command?.statusLabel
                  : "Fiscal issuance pending"}
        </strong>
      </div>

      {recorded ? (
        <>
          <p>{replay ? "Idempotent replay restored the existing fiscal document; no duplicate document was created." : "Central PMS recorded the fiscal workflow result."}</p>
          <dl className="central-pms-details">
            <div>
              <dt>Fiscal-issuance reference</dt>
              <dd>{command?.fiscalIssuanceReferenceId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>POS fiscal-document ID</dt>
              <dd>{command?.posFiscalDocumentId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-document number</dt>
              <dd>{command?.fiscalDocumentNumber ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-number assigned</dt>
              <dd>{command?.fiscalNumberAssignedAt ? formatDateTime(command.fiscalNumberAssignedAt) : "Unavailable"}</dd>
            </div>
            <div>
              <dt>Result classification</dt>
              <dd>{command?.resultClassification ?? "RECORDED"}</dd>
            </div>
            <div>
              <dt>Fiscal state</dt>
              <dd>{command?.fiscalIssuanceState ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Correlation ID</dt>
              <dd>{command?.fiscalCorrelationId ?? "Unavailable"}</dd>
            </div>
          </dl>
        </>
      ) : conflict ? (
        <>
          <p>Central PMS reported a fiscal conflict. Supervisor or support review is required.</p>
          <p>Fiscal command reference: {command?.localFiscalCommandId ?? "Unavailable"}</p>
          <p>Terminal cash tender: {command?.terminalCashTenderId ?? "Unavailable"}</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "CONFLICT"}</p>
        </>
      ) : rejected ? (
        <>
          <p>Central PMS rejected fiscal issuance. Canonical payment remains confirmed; fiscal issuance was not completed.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "REJECTED"}</p>
          <p>Support or reconciliation handling is required.</p>
        </>
      ) : (
        <>
          <p>{pending ? "Fiscal issuance pending." : `Fiscal status: ${command?.statusLabel}`}</p>
          <p>{command?.fiscalIssuanceReferenceId ? `Fiscal reference: ${command.fiscalIssuanceReferenceId}` : "No fiscal document recorded yet."}</p>
          <p>{command?.lastSafeErrorCode ? `Safe error code: ${command.lastSafeErrorCode}` : "Use the persisted fiscal command to issue or check status."}</p>
        </>
      )}

      <p>Receipt not rendered or printed. Exit authorization unavailable.</p>
      {!recorded && !conflict && !rejected && (
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Issue / Check Fiscal Document
        </button>
      )}
    </section>
  );
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat("en-PH", {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}
