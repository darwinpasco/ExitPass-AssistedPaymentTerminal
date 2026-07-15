namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashPaymentOutboxCommand
{
    public Guid Id { get; set; }

    public Guid TerminalCashTenderId { get; set; }

    public Guid CashCustodySessionId { get; set; }

    public required string RequestPayloadJson { get; set; }

    public required string RequestPayloadHash { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string OriginalCorrelationId { get; set; }

    public required string CentralPmsTarget { get; set; }

    public TerminalCashPaymentCommandStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? FirstAttemptedAt { get; set; }

    public DateTimeOffset? LastAttemptedAt { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int? LastSafeHttpStatus { get; set; }

    public string? LastSafeErrorCode { get; set; }

    public string? ResultClassification { get; set; }

    public Guid? CanonicalPaymentAttemptId { get; set; }

    public Guid? CanonicalPaymentConfirmationId { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<TerminalCashPaymentSubmissionAttempt> Attempts { get; } = new List<TerminalCashPaymentSubmissionAttempt>();
}
