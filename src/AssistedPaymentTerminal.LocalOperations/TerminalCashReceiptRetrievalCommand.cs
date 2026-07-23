namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashReceiptRetrievalCommand
{
    public Guid Id { get; set; }

    public Guid TerminalCashTenderId { get; set; }

    public Guid RelatedCashPaymentOutboxCommandId { get; set; }

    public TerminalCashPaymentOutboxCommand? RelatedCashPaymentOutboxCommand { get; set; }

    public Guid RelatedFiscalCommandId { get; set; }

    public TerminalCashFiscalOutboxCommand? RelatedFiscalCommand { get; set; }

    public Guid CanonicalPaymentAttemptId { get; set; }

    public Guid CanonicalPaymentConfirmationId { get; set; }

    public string? CanonicalPaymentStatus { get; set; }

    public Guid FiscalIssuanceReferenceId { get; set; }

    public Guid PosFiscalDocumentId { get; set; }

    public required string RetrievalCorrelationId { get; set; }

    public required string CentralPmsTarget { get; set; }

    public TerminalCashReceiptRetrievalStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? FirstAttemptedAt { get; set; }

    public DateTimeOffset? LastAttemptedAt { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int? LastSafeHttpStatus { get; set; }

    public string? LastSafeErrorCode { get; set; }

    public bool? LastRetryable { get; set; }

    public string? LastCentralPmsCorrelationId { get; set; }

    public string? ResultClassification { get; set; }

    public string? ReceiptAvailabilityState { get; set; }

    public string? FiscalDocumentNumber { get; set; }

    public string? FiscalDocumentStatus { get; set; }

    public string? PresentationVersion { get; set; }

    public string? TemplateVersion { get; set; }

    public string? SemanticRequestHash { get; set; }

    public string? SemanticRequestHashVersion { get; set; }

    public string? SemanticRequestHashStatus { get; set; }

    public string? ContentType { get; set; }

    public string? AuthoritativePresentationJson { get; set; }

    public string? AuthoritativePayloadHash { get; set; }

    public string? VoidStatus { get; set; }

    public string? VoidReasonCode { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public DateTimeOffset? RetrievedAt { get; set; }

    public DateTimeOffset? LastUpdatedFromCentralPms { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<TerminalCashReceiptRetrievalAttempt> Attempts { get; } = new List<TerminalCashReceiptRetrievalAttempt>();
}
