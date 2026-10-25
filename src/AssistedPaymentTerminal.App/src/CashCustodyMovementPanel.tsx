import { useMemo, useRef, useState } from "react";
import { CashDenominationGrid } from "./CashDenominationGrid";
import type { CashCustodyDenominationDefinition, CashCustodyDenominationLine } from "./cashCustodyDenominations";
import type {
  CashCustodyMovement,
  CashCustodyMovementEventType,
  CashCustodyMovementType,
  HumanSessionBridge,
  HumanSessionBridgeResult,
  HumanSessionState,
} from "./humanSessionBridge";

type Props = {
  state: HumanSessionState;
  bridge: HumanSessionBridge;
  catalogue: CashCustodyDenominationDefinition[];
  disabled: boolean;
  invoke: (name: string, action: () => Promise<HumanSessionBridgeResult>) => Promise<HumanSessionBridgeResult>;
};

export function CashCustodyMovementPanel({ state, bridge, catalogue, disabled, invoke }: Props) {
  const movements = state.activeCashCustodySummary?.movements ?? [];
  const [movementType, setMovementType] = useState<CashCustodyMovementType>("SafeDrop");
  const [counterpartyReference, setCounterpartyReference] = useState("");
  const [handoverReference, setHandoverReference] = useState("");
  const [originalMovementId, setOriginalMovementId] = useState("");
  const initiationKey = useRef(`cash-custody-movement:${crypto.randomUUID()}`);
  const completedOutbound = movements.filter((movement) =>
    movement.status === "Completed" && (movement.movementType === "SafeDrop" || movement.movementType === "DirectRemittance"));
  const pendingCount = movements.filter((movement) => movement.status === "PendingAcknowledgment").length;

  async function initiate(denominations: CashCustodyDenominationLine[]) {
    const result = await invoke("initiate-movement", () => bridge.initiateCashCustodyMovement(crypto.randomUUID(), {
      movementType,
      originalMovementId: movementType === "CashReturn" ? originalMovementId || null : null,
      intendedCounterpartyReference: counterpartyReference.trim(),
      externalHandoverReference: handoverReference.trim(),
      idempotencyKey: initiationKey.current,
      denominations,
    }));
    if (result.ok) {
      initiationKey.current = `cash-custody-movement:${crypto.randomUUID()}`;
    }
  }

  return (
    <section className="cashier-session-group cash-custody-movements" aria-label="Cash custody movements">
      <h3>Cash Movements</h3>
      {pendingCount > 0 && (
        <div className="status-notice danger" role="alert">
          <strong>{pendingCount} movement{pendingCount === 1 ? "" : "s"} awaiting acknowledgment</strong>
          <p>Pending physical handovers must be completed, cancelled, or rejected before custody can close.</p>
        </div>
      )}

      {state.movementInitiationAuthorized ? (
        <div className="movement-entry-form">
          <label>
            Movement type
            <select value={movementType} disabled={disabled} onChange={(event) => setMovementType(event.target.value as CashCustodyMovementType)}>
              <option value="SafeDrop">Safe Drop</option>
              <option value="DirectRemittance">Direct Cash Remittance</option>
              <option value="CashReturn">Referenced Cash Return</option>
            </select>
          </label>
          {movementType === "CashReturn" && (
            <label>
              Original completed transfer
              <select value={originalMovementId} disabled={disabled} onChange={(event) => setOriginalMovementId(event.target.value)}>
                <option value="">Select a movement</option>
                {completedOutbound.map((movement) => (
                  <option value={movement.id} key={movement.id}>{movementReference(movement.id)} - {formatPhp(movement.amountMinorUnits)}</option>
                ))}
              </select>
            </label>
          )}
          <label>
            Authorized counterparty username
            <input value={counterpartyReference} disabled={disabled} onChange={(event) => setCounterpartyReference(event.target.value)} autoComplete="off" />
          </label>
          <label>
            Handover or remittance reference
            <input value={handoverReference} disabled={disabled} onChange={(event) => setHandoverReference(event.target.value)} autoComplete="off" />
          </label>
          <CashDenominationGrid
            purpose="movement"
            catalogue={catalogue}
            disabled={disabled
              || !counterpartyReference.trim()
              || !handoverReference.trim()
              || (movementType === "CashReturn" && !originalMovementId)}
            onConfirm={(denominations) => void initiate(denominations)}
          />
          <p>Initiation records a pending request only. Cash custody changes after independent acknowledgment.</p>
        </div>
      ) : (
        <p>Cash movement initiation requires an explicit site-scoped assignment.</p>
      )}

      {state.movementReadAuthorized && (
        <MovementHistory movements={movements} bridge={bridge} disabled={disabled} invoke={invoke} />
      )}
    </section>
  );
}

function MovementHistory({ movements, bridge, disabled, invoke }: {
  movements: CashCustodyMovement[];
  bridge: HumanSessionBridge;
  disabled: boolean;
  invoke: Props["invoke"];
}) {
  const [counterpartyUsernames, setCounterpartyUsernames] = useState<Record<string, string>>({});
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [noteType, setNoteType] = useState<CashCustodyMovementEventType>("CorrectionNoted");
  const [noteMetadata, setNoteMetadata] = useState<Record<string, string>>({});
  const ordered = useMemo(() => [...movements].sort((a, b) => b.createdAt.localeCompare(a.createdAt)), [movements]);

  return (
    <div className="movement-history" aria-label="Cash movement history">
      <h4>Movement History</h4>
      {ordered.length === 0 ? <p>No cash movements recorded for this custody session.</p> : ordered.map((movement) => {
        const username = counterpartyUsernames[movement.id] ?? movement.intendedCounterpartyReference;
        const reason = reasons[movement.id] ?? "";
        const terminalEvent = movement.events.find((event) =>
          event.eventType === "Completed" || event.eventType === "Cancelled" || event.eventType === "Rejected");
        return (
          <article className="movement-history-entry" key={movement.id}>
            <dl className="cash-custody-summary">
              <div><dt>Reference</dt><dd>{movementReference(movement.id)}</dd></div>
              <div><dt>Type</dt><dd>{movementTypeLabel(movement.movementType)}</dd></div>
              <div><dt>Status</dt><dd>{movementStatusLabel(movement.status)}</dd></div>
              <div><dt>Amount</dt><dd>{formatPhp(movement.amountMinorUnits)}</dd></div>
              <div><dt>Counterparty</dt><dd>{movement.intendedCounterpartyReference}</dd></div>
              <div><dt>Handover reference</dt><dd>{movement.externalHandoverReference}</dd></div>
              <div><dt>Initiated</dt><dd>{new Date(movement.createdAt).toLocaleString("en-PH")}</dd></div>
              {terminalEvent?.eventType === "Completed" && <div><dt>Acknowledged by</dt><dd>{terminalEvent.authorizedActorId}</dd></div>}
              {terminalEvent?.eventType === "Completed" && <div><dt>Acknowledgment reference</dt><dd>{terminalEvent.correlationId}</dd></div>}
              {terminalEvent && <div><dt>{terminalEvent.eventType === "Completed" ? "Completed" : "Resolved"}</dt><dd>{new Date(terminalEvent.occurredAt).toLocaleString("en-PH")}</dd></div>}
              {movement.originalMovementId && <div><dt>Original movement</dt><dd>{movementReference(movement.originalMovementId)}</dd></div>}
            </dl>
            <details>
              <summary>Denomination and audit evidence</summary>
              <dl className="movement-denominations">
                {movement.denominations.map((entry) => (
                  <div key={entry.denominationCode}><dt>{entry.denominationCode}</dt><dd>{entry.quantity} x {formatPhp(entry.denominationValueMinorUnits)} = {formatPhp(entry.calculatedAmountMinorUnits)}</dd></div>
                ))}
              </dl>
              <ul>
                {movement.events.map((event) => <li key={event.id}>{event.eventType} - {new Date(event.occurredAt).toLocaleString("en-PH")}{event.reasonCode ? ` - ${event.reasonCode}` : ""}</li>)}
              </ul>
            </details>
            {movement.status === "PendingAcknowledgment" && (
              <div className="movement-actions">
                <label>
                  Authorized counterparty username
                  <input value={username} disabled={disabled} onChange={(event) => setCounterpartyUsernames((current) => ({ ...current, [movement.id]: event.target.value }))} autoComplete="off" />
                </label>
                <label>
                  Rejection or cancellation reason
                  <input value={reason} disabled={disabled} onChange={(event) => setReasons((current) => ({ ...current, [movement.id]: event.target.value }))} />
                </label>
                <button type="button" disabled={disabled || !username.trim()} onClick={() => void invoke("acknowledge-movement", () => bridge.acknowledgeCashCustodyMovement(
                  crypto.randomUUID(), movement.id, username.trim(), false, null, `movement:${movement.id}:completed`))}>
                  Receiver Acknowledgment
                </button>
                <button type="button" className="secondary-action" disabled={disabled || !username.trim() || !reason.trim()} onClick={() => void invoke("reject-movement", () => bridge.acknowledgeCashCustodyMovement(
                  crypto.randomUUID(), movement.id, username.trim(), true, reason.trim(), `movement:${movement.id}:rejected`))}>
                  Reject Handover
                </button>
                <button type="button" className="secondary-action" disabled={disabled || !reason.trim()} onClick={() => void invoke("cancel-movement", () => bridge.cancelCashCustodyMovement(
                  crypto.randomUUID(), movement.id, reason.trim(), `movement:${movement.id}:cancelled`))}>
                  Cancel Request
                </button>
              </div>
            )}
            {movement.status !== "PendingAcknowledgment" && (
              <details className="movement-note-form">
                <summary>Append correction or dispute note</summary>
                <label>Authorized correcting username<input value={username} disabled={disabled} onChange={(event) => setCounterpartyUsernames((current) => ({ ...current, [movement.id]: event.target.value }))} autoComplete="off" /></label>
                <label>Note type<select value={noteType} disabled={disabled} onChange={(event) => setNoteType(event.target.value as CashCustodyMovementEventType)}><option value="CorrectionNoted">Correction note</option><option value="DisputeRaised">Dispute raised</option><option value="DisputeResolved">Dispute resolved</option></select></label>
                <label>Reason code<input value={reason} disabled={disabled} onChange={(event) => setReasons((current) => ({ ...current, [movement.id]: event.target.value }))} /></label>
                <label>Controlled note<textarea value={noteMetadata[movement.id] ?? ""} disabled={disabled} onChange={(event) => setNoteMetadata((current) => ({ ...current, [movement.id]: event.target.value }))} /></label>
                <button type="button" disabled={disabled || !username.trim() || !reason.trim() || !(noteMetadata[movement.id] ?? "").trim()} onClick={() => void invoke("append-movement-note", () => bridge.appendCashCustodyMovementNote(
                  crypto.randomUUID(), movement.id, noteType, username.trim(), reason.trim(), (noteMetadata[movement.id] ?? "").trim(), `movement:${movement.id}:note:${crypto.randomUUID()}`))}>
                  Authenticate and append note
                </button>
              </details>
            )}
          </article>
        );
      })}
    </div>
  );
}

function movementReference(id: string): string {
  return `MOV-${id.replaceAll("-", "").slice(0, 12).toUpperCase()}`;
}

function movementTypeLabel(type: CashCustodyMovementType): string {
  return type === "SafeDrop" ? "Safe Drop" : type === "DirectRemittance" ? "Direct Cash Remittance" : "Cash Return";
}

function movementStatusLabel(status: CashCustodyMovement["status"]): string {
  return status === "PendingAcknowledgment" ? "Pending acknowledgment" : status;
}

function formatPhp(amountMinorUnits: number): string {
  return new Intl.NumberFormat("en-PH", { style: "currency", currency: "PHP" }).format(amountMinorUnits / 100);
}
