namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashFiscalOutboxCommand
{
    public Guid Id { get; set; }

    public Guid TerminalCashTenderId { get; set; }

    public Guid RelatedCashPaymentOutboxCommandId { get; set; }

    public TerminalCashPaymentOutboxCommand? RelatedCashPaymentOutboxCommand { get; set; }

    public Guid CanonicalPaymentAttemptId { get; set; }

    public Guid CanonicalPaymentConfirmationId { get; set; }

    public required string RequestRepresentationJson { get; set; }

    public required string RequestHash { get; set; }

    public required string FiscalIdempotencyKey { get; set; }

    public required string FiscalCorrelationId { get; set; }

    public required string CentralPmsTarget { get; set; }

    public TerminalCashFiscalCommandStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? FirstAttemptedAt { get; set; }

    public DateTimeOffset? LastAttemptedAt { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int? LastSafeHttpStatus { get; set; }

    public string? LastSafeErrorCode { get; set; }

    public string? ResultClassification { get; set; }

    public Guid? FiscalIssuanceReferenceId { get; set; }

    public string? FiscalIssuanceState { get; set; }

    public Guid? PosFiscalDocumentId { get; set; }

    public string? FiscalDocumentNumber { get; set; }

    public DateTimeOffset? FiscalNumberAssignedAt { get; set; }

    public string? SemanticHashSourceVersion { get; set; }

    public DateTimeOffset? RecordedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<TerminalCashFiscalAttempt> Attempts { get; } = new List<TerminalCashFiscalAttempt>();
}
