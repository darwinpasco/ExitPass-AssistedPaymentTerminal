import { useEffect, useMemo, useState } from "react";
import type { AptConfig, ConfigLoadResult } from "./config";
import { loadAptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import { createCentralPmsClient } from "./api/clientFactory";
import type { CentralPmsClient, CentralPmsResult, ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import { buildTerminalContext, type TerminalContext } from "./terminalContext";

type LookupState =
  | { status: "idle" }
  | { status: "loading"; correlationId: string; action: "lookup" | "recalculate" }
  | { status: "resolved"; session: ResolveVendorParkingResponse; recalculated: boolean }
  | { status: "failed"; result: Exclude<CentralPmsResult, { ok: true }> };

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

  return <TerminalShell config={configResult.config} client={createCentralPmsClient(configResult.config)} />;
}

export function TerminalShell({ config, client }: { config: AptConfig; client: CentralPmsClient }) {
  const context = useMemo(() => buildTerminalContext(config), [config]);
  const [ticketReference, setTicketReference] = useState("");
  const [lookupState, setLookupState] = useState<LookupState>({ status: "idle" });

  async function resolveTicket() {
    const trimmed = ticketReference.trim();
    if (!trimmed) return;

    const correlationId = createCorrelationId();
    setLookupState({ status: "loading", correlationId, action: "lookup" });

    const result = await client.resolveTicket(trimmed, correlationId);
    setLookupState(result.ok ? { status: "resolved", session: result.response, recalculated: false } : { status: "failed", result });
  }

  async function recalculateFee(session: ResolveVendorParkingResponse) {
    const correlationId = createCorrelationId();
    setLookupState({ status: "loading", correlationId, action: "recalculate" });

    const result = await client.recalculateFee(session.ticketReference ?? ticketReference, correlationId);
    setLookupState(result.ok ? { status: "resolved", session: result.response, recalculated: true } : { status: "failed", result });
  }

  function resetLookup() {
    setLookupState({ status: "idle" });
    setTicketReference("");
  }

  const resolvedSession = lookupState.status === "resolved" ? lookupState.session : undefined;
  const tariffExpired = resolvedSession ? new Date(resolvedSession.tariffExpiresAt).getTime() <= Date.now() : false;

  return (
    <main className="terminal-shell">
      <header className="brand-header">
        <div>
          <p className="eyebrow">ExitPass Assisted Payment Terminal</p>
          <h1>Cashier-Assisted Terminal</h1>
        </div>
        <span className="mode-badge">Mode 1</span>
      </header>

      <section className="workflow-grid">
        <TerminalContextPanel context={context} />

        <section className="lookup-panel" aria-labelledby="ticket-lookup-heading">
          <div className="section-heading">
            <p className="eyebrow">Session resolution</p>
            <h2 id="ticket-lookup-heading">Ticket lookup</h2>
          </div>
          <form
            className="lookup-form"
            onSubmit={(event) => {
              event.preventDefault();
              void resolveTicket();
            }}
          >
            <label htmlFor="ticketReference">Ticket reference</label>
            <div className="lookup-row">
              <input
                id="ticketReference"
                value={ticketReference}
                onChange={(event) => setTicketReference(event.target.value)}
                placeholder="Scan or type ticket reference"
                autoFocus
                autoComplete="off"
              />
              <button type="submit" disabled={!ticketReference.trim() || lookupState.status === "loading"}>
                Resolve
              </button>
            </div>
          </form>

          {lookupState.status === "loading" && (
            <StatusNotice tone="info" title={lookupState.action === "recalculate" ? "Recalculating fee" : "Resolving session"}>
              Request sent to {context.centralPmsConnectionMode}. Support reference: <strong>{lookupState.correlationId}</strong>
            </StatusNotice>
          )}

          {lookupState.status === "failed" && <FailureNotice result={lookupState.result} onReset={resetLookup} />}

          {lookupState.status === "resolved" && (
            <>
              <SessionSummary session={lookupState.session} recalculated={lookupState.recalculated} />
              {tariffExpired ? (
                <ExpiredTariffNotice session={lookupState.session} onRecalculate={() => void recalculateFee(lookupState.session)} />
              ) : (
                <ActiveTariffNotice session={lookupState.session} />
              )}
              <PaymentStage blockedByExpiredTariff={tariffExpired} />
            </>
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
        <ul>
          {result.errors.map((error) => (
            <li key={error}>{error}</li>
          ))}
        </ul>
      </div>
    </main>
  );
}

function TerminalContextPanel({ context }: { context: TerminalContext }) {
  const rows = [
    ["Operating mode", context.operatingMode],
    ["Terminal ID", context.terminalId],
    ["Terminal display name", context.terminalDisplayName],
    ["Site ID", context.siteId],
    ["Site name", context.siteName],
    ["Site-group ID", context.siteGroupId],
    ["POS Server", context.posServerId],
    ["Cashier", `${context.cashierDisplayName} (${context.cashierId})`],
    ["Shift ID", context.shiftId],
    ["Shift status", context.shiftStatus],
    ["Central PMS", context.centralPmsConnectionMode],
  ];

  return (
    <aside className="context-panel" aria-label="Bound terminal context">
      <div className="section-heading">
        <p className="eyebrow">Bound context</p>
        <h2>{context.terminalDisplayName}</h2>
      </div>
      <dl>
        {rows.map(([label, value]) => (
          <div key={label} className="context-row">
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </aside>
  );
}

function SessionSummary({ session, recalculated }: { session: ResolveVendorParkingResponse; recalculated: boolean }) {
  const amount = new Intl.NumberFormat("en-PH", { style: "currency", currency: session.currency }).format(
    session.netPayableMinorUnits / 100,
  );

  const rows = [
    ["Parking session ID", session.parkingSessionId],
    ["Ticket reference", session.ticketReference ?? "Unavailable"],
    ["Masked plate", maskPlate(session.plateNumber)],
    ["Site", session.siteName ?? session.siteId],
    ["Entry timestamp", formatDate(session.entryTime)],
    ["Currency", session.currency],
    ["Tariff snapshot ID", session.effectiveTariffSnapshotId ?? session.tariffSnapshotId],
    ["Tariff calculated", formatDate(session.currentFeeCalculationTime)],
    ["Tariff expiry", formatDate(session.tariffExpiresAt)],
    ["Payment status", session.paymentStatus],
    ["Statutory discount", session.statutoryDiscountApplied ? "Applied" : "Not applied"],
    ["Correlation ID", session.correlationId],
  ];

  return (
    <section className="session-summary" aria-label="Resolved parking session">
      <div className="amount-band">
        <div>
          <p className="eyebrow">Authoritative payable basis</p>
          <strong>{amount}</strong>
        </div>
        <span className={recalculated ? "status-badge success" : "status-badge"}>{recalculated ? "Recalculated" : "Resolved"}</span>
      </div>
      <dl>
        {rows.map(([label, value]) => (
          <div key={label} className="summary-row">
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function ActiveTariffNotice({ session }: { session: ResolveVendorParkingResponse }) {
  const minutes = Math.max(0, Math.ceil((new Date(session.tariffExpiresAt).getTime() - Date.now()) / 60000));
  return (
    <StatusNotice tone="success" title="Payable basis is current">
      The current tariff is valid for approximately {minutes} minute{minutes === 1 ? "" : "s"} and expires at{" "}
      <strong>{formatDate(session.tariffExpiresAt)}</strong>.
    </StatusNotice>
  );
}

function ExpiredTariffNotice({ session, onRecalculate }: { session: ResolveVendorParkingResponse; onRecalculate: () => void }) {
  return (
    <StatusNotice tone="danger" title="Tariff expired">
      <p>
        Payment is blocked because the payable amount must be recalculated by an approved backend contract before collection can
        begin.
      </p>
      <button className="secondary-action" type="button" onClick={onRecalculate}>
        Recalculate Fee
      </button>
      <p className="support-line">Support reference: {session.correlationId}</p>
    </StatusNotice>
  );
}

function PaymentStage({ blockedByExpiredTariff }: { blockedByExpiredTariff: boolean }) {
  return (
    <section className="payment-stage" aria-label="Payment stage disabled">
      <div>
        <p className="eyebrow">Payment stage</p>
        <h2>{blockedByExpiredTariff ? "Blocked by expired tariff" : "Unavailable in this slice"}</h2>
        <p>
          Session resolution and payable-basis display are complete. Payment collection, fiscal issuance, receipt numbers, and exit
          authorization are not enabled in this development slice.
        </p>
      </div>
      <button type="button" disabled>
        Collect payment
      </button>
    </section>
  );
}

function FailureNotice({ result, onReset }: { result: Exclude<CentralPmsResult, { ok: true }>; onReset: () => void }) {
  const titleByKind: Record<string, string> = {
    not_found: "Ticket not found",
    inactive: "Session inactive or invalid",
    ambiguous: "Ambiguous ticket result",
    service_unavailable: "Central PMS unavailable",
    timeout: "Central PMS timeout",
    malformed_response: "Malformed Central PMS response",
    invalid_request: "Invalid lookup request",
    recalculation_pending: "Recalculation integration pending",
    unknown: "Lookup failed",
  };

  return (
    <StatusNotice tone="danger" title={titleByKind[result.kind] ?? "Lookup failed"}>
      <p>{result.error.message}</p>
      <p className="support-line">Support reference: {result.error.correlationId}</p>
      <button className="secondary-action" type="button" onClick={onReset}>
        Back to ticket lookup
      </button>
    </StatusNotice>
  );
}

function StatusNotice({ tone, title, children }: { tone: "info" | "success" | "danger"; title: string; children: React.ReactNode }) {
  return (
    <section className={`status-notice ${tone}`} role={tone === "danger" ? "alert" : "status"}>
      <h3>{title}</h3>
      <div>{children}</div>
    </section>
  );
}

function maskPlate(plate?: string | null): string {
  if (!plate) return "Unavailable";
  const compact = plate.replace(/[^a-z0-9]/gi, "");
  if (compact.length <= 3) return "***";
  return `${compact.slice(0, 3)}-${"*".repeat(Math.max(2, compact.length - 3))}`;
}

function formatDate(value?: string | null): string {
  if (!value) return "Unavailable";
  return new Intl.DateTimeFormat("en-PH", {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}
