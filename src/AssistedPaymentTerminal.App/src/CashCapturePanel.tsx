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

  const amountTendered = Number(amountTenderedText);
  const changeDue = Number.isFinite(amountTendered) ? Math.max(0, amountTendered - amountDue) : 0;

  useEffect(() => {
    setAmountTenderedText(amountDue.toFixed(2));
    setCashierAttested(false);
    setDenominationCounts({});
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

  const existingTender = status.kind === "ready" ? status.readback.tender : status.kind === "success" ? status.readback.tender : null;

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

      {existingTender && (
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

      <button className="secondary-action" type="button" onClick={() => void reloadLocalTender()}>
        Reload local tender
      </button>
    </section>
  );
}

function formatAmount(value: number): string {
  return value.toFixed(2);
}
