import { useEffect, useState } from "react";
import type {
  CentralPmsClient,
  PayableBasisResponse,
  SalesInvoicePresentationSection,
  StatutorySalesInvoicePresentationResponse,
} from "./api/centralPmsTypes";
import { createCorrelationId } from "./correlation";

type PresentationState =
  | { status: "loading" }
  | { status: "available"; receipt: StatutorySalesInvoicePresentationResponse }
  | { status: "failed"; message: string };

type PrintState =
  | { status: "idle" }
  | { status: "submitting" }
  | { status: "submitted"; message: string }
  | { status: "failed"; message: string };

export function ZeroPayableSalesInvoicePanel({
  basis,
  client,
}: {
  basis: PayableBasisResponse;
  client: CentralPmsClient;
}) {
  const [state, setState] = useState<PresentationState>({ status: "loading" });
  const [retrySequence, setRetrySequence] = useState(0);
  const [printState, setPrintState] = useState<PrintState>({ status: "idle" });
  const completion = basis.zeroPayableStatutoryCompletion;
  const decisionCommandId = basis.statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId;
  const applicationCommandId = basis.statutoryDiscountReadiness?.statutoryDiscountPayableBasisApplicationCommandId;

  useEffect(() => {
    let active = true;
    setState({ status: "loading" });

    if (!client.getStatutorySalesInvoicePresentation) {
      setState({ status: "failed", message: "Sales Invoice presentation is unavailable on this terminal." });
      return () => { active = false; };
    }

    void client.getStatutorySalesInvoicePresentation(basis, createCorrelationId()).then((result) => {
      if (!active) return;
      setState(result.ok
        ? { status: "available", receipt: result.response }
        : { status: "failed", message: result.error.message });
    });

    return () => { active = false; };
  }, [
    client,
    basis.parkingSessionId,
    completion?.fiscalIssuanceReferenceId,
    completion?.posServerFiscalDocumentId,
    completion?.fiscalDocumentNumber,
    decisionCommandId,
    applicationCommandId,
    retrySequence,
  ]);

  if (state.status === "loading") {
    return <p role="status">Loading the authoritative Sales Invoice...</p>;
  }

  if (state.status === "failed") {
    return (
      <div className="zero-payable-sales-invoice-error" role="alert">
        <p>{state.message}</p>
        <button className="secondary-action" type="button" onClick={() => setRetrySequence((value) => value + 1)}>Retry Sales Invoice</button>
      </div>
    );
  }

  const presentation = state.receipt.authoritativePresentation;
  const canonicalText = presentation.canonicalText?.trim();
  const sections = presentation.presentation?.sections ?? [];

  async function printSalesInvoice() {
    if (!client.printStatutorySalesInvoice) {
      setPrintState({ status: "failed", message: "The APT printer bridge is unavailable." });
      return;
    }

    setPrintState({ status: "submitting" });
    const result = await client.printStatutorySalesInvoice(basis, createCorrelationId());
    setPrintState(result.ok
      ? { status: "submitted", message: `${result.response.safeMessage} ${result.response.printerName}` }
      : { status: "failed", message: result.error.message });
  }

  return (
    <section className="zero-payable-sales-invoice" aria-label="Digital Sales Invoice">
      <div className="zero-payable-sales-invoice-actions">
        <h3>Digital Sales Invoice</h3>
        <button
          className="primary-action"
          type="button"
          disabled={printState.status === "submitting" || printState.status === "submitted"}
          onClick={() => void printSalesInvoice()}
        >
          {printState.status === "submitting" ? "Submitting to printer..." : printState.status === "submitted" ? "Sales Invoice submitted" : "Print Sales Invoice"}
        </button>
      </div>
      {printState.status === "submitted" && <p role="status">{printState.message}</p>}
      {printState.status === "failed" && <p role="alert">{printState.message}</p>}
      <article className="receipt-paper receipt-paper-80 zero-payable-sales-invoice-print" aria-label="Printable Sales Invoice">
        {canonicalText
          ? <pre className="zero-payable-sales-invoice-text">{canonicalText}</pre>
          : <StructuredPresentation sections={sections} documentNumber={state.receipt.fiscalDocumentNumber} />}
      </article>
    </section>
  );
}

function StructuredPresentation({
  sections,
  documentNumber,
}: {
  sections: SalesInvoicePresentationSection[];
  documentNumber?: string | null;
}) {
  return (
    <>
      <header className="receipt-paper-title"><h4>Sales Invoice</h4><p>{documentNumber}</p></header>
      {sections.map((section, sectionIndex) => {
        const rows = (section.rows ?? []).filter((row) =>
          row.label && row.posture !== "placeholder" && row.posture !== "deferred" && (row.displayValue ?? row.value) != null);
        if (rows.length === 0) return null;
        return (
          <section className="receipt-paper-section" key={`${section.label ?? section.title ?? "section"}-${sectionIndex}`}>
            <h4>{section.label ?? section.title}</h4>
            <dl className="receipt-paper-fields">
              {rows.map((row, rowIndex) => (
                <div key={`${row.label}-${rowIndex}`}><dt>{row.label}</dt><dd>{String(row.displayValue ?? row.value)}</dd></div>
              ))}
            </dl>
          </section>
        );
      })}
    </>
  );
}
