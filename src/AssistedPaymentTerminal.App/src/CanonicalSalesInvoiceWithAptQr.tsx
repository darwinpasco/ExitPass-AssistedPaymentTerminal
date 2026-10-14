export function CanonicalSalesInvoiceWithAptQr({
  canonicalText,
  ticketNumber,
  qrCodeDataUrl,
  appendExitQr,
}: {
  canonicalText: string;
  ticketNumber: string;
  qrCodeDataUrl: string;
  appendExitQr: boolean;
}) {
  const lines = canonicalText.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  const closingIndex = lines.findIndex((line) => line.includes("NOTHING FOLLOWS"));
  if (closingIndex < 0 || !appendExitQr) {
    return <pre className="canonical-sales-invoice-text">{canonicalText}</pre>;
  }

  return (
    <>
      <pre className="canonical-sales-invoice-text">{lines.slice(0, closingIndex + 1).join("\n")}</pre>
      <figure className="apt-ticket-qr">
        <img src={qrCodeDataUrl} alt={`Ticket ${ticketNumber} QR code`} />
        <figcaption>EXIT QR<br />Present at exit validator</figcaption>
      </figure>
      {lines.slice(closingIndex + 1).some((line) => line.length > 0) && (
        <pre className="canonical-sales-invoice-text">{lines.slice(closingIndex + 1).join("\n")}</pre>
      )}
    </>
  );
}
