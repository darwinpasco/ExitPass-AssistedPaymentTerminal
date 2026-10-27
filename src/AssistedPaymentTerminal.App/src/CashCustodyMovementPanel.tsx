import { useEffect, useMemo, useRef, useState } from "react";
import { CashDenominationGrid } from "./CashDenominationGrid";
import type { CashCustodyDenominationDefinition, CashCustodyDenominationLine } from "./cashCustodyDenominations";
import type {
  CashCustodyMovement,
  CashCustodyCounterpartyOption,
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
  const [initiationDraft, setInitiationDraft] = useState(createMovementInitiationDraft);
  const [originalMovementId, setOriginalMovementId] = useState("");
  const [counterpartyDirectories, setCounterpartyDirectories] = useState<Record<string, CashCustodyCounterpartyOption[]>>({});
  const loadedDirectoryContexts = useRef(new Set<string>());
  const completedOutbound = movements.filter((movement) =>
    movement.status === "Completed" && (movement.movementType === "SafeDrop" || movement.movementType === "DirectRemittance"));
  const pendingCount = movements.filter((movement) => movement.status === "PendingAcknowledgment").length;
  const selectionContext = directoryContext(movementType, movementType === "CashReturn" ? originalMovementId || null : null, null);
  const receivingPeople = counterpartyDirectories[selectionContext]
    ?? (state.movementCounterpartyContext === selectionContext ? state.movementCounterparties ?? [] : []);

  useEffect(() => {
    const requests: Array<{ key: string; type: CashCustodyMovementType; originalId: string | null; requiredReference: string | null }> = [];
    if (movementType !== "CashReturn" || originalMovementId) {
      requests.push({ key: selectionContext, type: movementType, originalId: movementType === "CashReturn" ? originalMovementId : null, requiredReference: null });
    }
    for (const movement of movements) {
      requests.push({
        key: directoryContext(movement.movementType, movement.originalMovementId ?? null, movement.intendedCounterpartyReference),
        type: movement.movementType,
        originalId: movement.originalMovementId ?? null,
        requiredReference: movement.intendedCounterpartyReference,
      });
    }

    let cancelled = false;
    void (async () => {
      for (const request of requests) {
        if (cancelled || loadedDirectoryContexts.current.has(request.key)) continue;
        loadedDirectoryContexts.current.add(request.key);
        const result = await bridge.listCashCustodyMovementCounterparties(
          crypto.randomUUID(), request.type, request.originalId, request.requiredReference);
        if (cancelled) return;
        if (result.ok) {
          setCounterpartyDirectories((current) => ({
            ...current,
            [request.key]: result.payload.movementCounterparties ?? [],
          }));
        } else {
          loadedDirectoryContexts.current.delete(request.key);
        }
      }
    })();
    return () => { cancelled = true; };
    // Directory readback is local presentation state; financial commands still use invoke.
  }, [bridge, movementType, originalMovementId, selectionContext, movementDirectorySignature(movements), state.userReference]);

  useEffect(() => {
    if (counterpartyReference && !receivingPeople.some((person) => person.userReference === counterpartyReference)) {
      setCounterpartyReference("");
    }
  }, [counterpartyReference, receivingPeople]);

  async function initiate(denominations: CashCustodyDenominationLine[]) {
    const result = await invoke("initiate-movement", () => bridge.initiateCashCustodyMovement(crypto.randomUUID(), {
      movementType,
      originalMovementId: movementType === "CashReturn" ? originalMovementId || null : null,
      intendedCounterpartyReference: counterpartyReference.trim(),
      externalHandoverReference: initiationDraft.externalHandoverReference,
      idempotencyKey: initiationDraft.idempotencyKey,
      denominations,
    }));
    if (result.ok) {
      setInitiationDraft(createMovementInitiationDraft());
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
            Receiving Person
            <select value={counterpartyReference} disabled={disabled || receivingPeople.length === 0} onChange={(event) => setCounterpartyReference(event.target.value)}>
              <option value="">Select an authorized person</option>
              {receivingPeople.map((person) => (
                <option value={person.userReference} key={person.userReference}>{counterpartyOptionLabel(person, receivingPeople)}</option>
              ))}
            </select>
          </label>
          {receivingPeople.length === 0 && <p className="cash-error" role="alert">No authorized receiving person is currently available for this movement.</p>}
          <label>
            Remittance / Handover Reference No.
            <input value={initiationDraft.externalHandoverReference} disabled={disabled} readOnly aria-readonly="true" />
          </label>
          <CashDenominationGrid
            purpose="movement"
            catalogue={catalogue}
            disabled={disabled
              || !counterpartyReference.trim()
              || !initiationDraft.externalHandoverReference
              || (movementType === "CashReturn" && !originalMovementId)}
            onConfirm={(denominations) => void initiate(denominations)}
          />
          <p>Initiation records a pending request only. Cash custody changes after independent acknowledgment.</p>
        </div>
      ) : (
        <p>Cash movement initiation requires an explicit site-scoped assignment.</p>
      )}

      {state.movementReadAuthorized && (
        <MovementHistory movements={movements} bridge={bridge} disabled={disabled} invoke={invoke} counterpartyDirectories={counterpartyDirectories} />
      )}
    </section>
  );
}

function MovementHistory({ movements, bridge, disabled, invoke, counterpartyDirectories }: {
  movements: CashCustodyMovement[];
  bridge: HumanSessionBridge;
  disabled: boolean;
  invoke: Props["invoke"];
  counterpartyDirectories: Record<string, CashCustodyCounterpartyOption[]>;
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
        const username = counterpartyUsernames[movement.id] ?? "";
        const directory = counterpartyDirectories[directoryContext(
          movement.movementType,
          movement.originalMovementId ?? null,
          movement.intendedCounterpartyReference)] ?? [];
        const receivingPerson = directory[0];
        const receivingPersonLabel = receivingPerson
          ? `${receivingPerson.displayName} - ${receivingPerson.roleLabel}`
          : movement.intendedCounterpartyReference;
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
              <div><dt>Receiving person</dt><dd>{receivingPersonLabel}</dd></div>
              <div><dt>Remittance / Handover Reference No.</dt><dd>{movement.externalHandoverReference}</dd></div>
              <div><dt>Initiated</dt><dd>{new Date(movement.createdAt).toLocaleString("en-PH")}</dd></div>
              {terminalEvent?.eventType === "Completed" && <div><dt>Acknowledged by</dt><dd>{receivingPerson?.displayName ?? terminalEvent.authorizedActorId}</dd></div>}
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
                <p><strong>Receiving person:</strong> {receivingPersonLabel}</p>
                <label>
                  Rejection or cancellation reason
                  <input value={reason} disabled={disabled} onChange={(event) => setReasons((current) => ({ ...current, [movement.id]: event.target.value }))} />
                </label>
                <button type="button" disabled={disabled || !receivingPerson} onClick={() => void invoke("acknowledge-movement", () => bridge.acknowledgeCashCustodyMovement(
                  crypto.randomUUID(), movement.id, false, null, `movement:${movement.id}:completed`))}>
                  Receiver Acknowledgment
                </button>
                <button type="button" className="secondary-action" disabled={disabled || !receivingPerson || !reason.trim()} onClick={() => void invoke("reject-movement", () => bridge.acknowledgeCashCustodyMovement(
                  crypto.randomUUID(), movement.id, true, reason.trim(), `movement:${movement.id}:rejected`))}>
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

function directoryContext(type: CashCustodyMovementType, originalMovementId: string | null, requiredCounterpartyReference: string | null): string {
  return `${type}:${originalMovementId ?? "none"}:${requiredCounterpartyReference ?? "all"}`;
}

function movementDirectorySignature(movements: CashCustodyMovement[]): string {
  return movements.map((movement) => `${movement.id}:${movement.status}:${movement.intendedCounterpartyReference}`).sort().join("|");
}

function counterpartyOptionLabel(person: CashCustodyCounterpartyOption, people: CashCustodyCounterpartyOption[]): string {
  const duplicateName = people.filter((candidate) => candidate.displayName === person.displayName).length > 1;
  const suffix = duplicateName ? ` (${person.userReference.slice(0, 8)})` : "";
  return `${person.displayName} - ${person.roleLabel}${suffix}`;
}

function createMovementInitiationDraft(): { externalHandoverReference: string; idempotencyKey: string } {
  const identity = crypto.randomUUID();
  return {
    externalHandoverReference: `HND-${identity.replaceAll("-", "").toUpperCase()}`,
    idempotencyKey: `cash-custody-movement:${identity}`,
  };
}
