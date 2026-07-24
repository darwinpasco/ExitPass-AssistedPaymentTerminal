namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashReceiptPrintJob
{
    public Guid Id { get; set; }

    public Guid TerminalCashTenderId { get; set; }

    public Guid LocalReceiptRetrievalId { get; set; }

    public TerminalCashReceiptRetrievalCommand? LocalReceiptRetrieval { get; set; }

    public Guid FiscalIssuanceReferenceId { get; set; }

    public Guid PosFiscalDocumentId { get; set; }

    public required string FiscalDocumentNumber { get; set; }

    public required string PresentationVersion { get; set; }

    public required string TemplateVersion { get; set; }

    public required string AuthoritativePayloadHash { get; set; }

    public string? SemanticRequestHash { get; set; }

    public int PaperWidthMm { get; set; }

    public required string PaperProfileId { get; set; }

    public required string ConfiguredPrinterName { get; set; }

    public TerminalCashReceiptPrintClassification Classification { get; set; }

    public int CopySequence { get; set; }

    public TerminalCashReceiptPrintJobStatus Status { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public string? RequestedBy { get; set; }

    public DateTimeOffset? SubmissionStartedAt { get; set; }

    public DateTimeOffset? SubmittedToSpoolerAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureClassification { get; set; }

    public bool Retryable { get; set; }

    public string? WindowsSpoolerJobId { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; }

    public required string CorrelationId { get; set; }
}
