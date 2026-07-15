namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashPaymentSubmissionAttempt
{
    public Guid Id { get; set; }

    public Guid LocalCommandId { get; set; }

    public TerminalCashPaymentOutboxCommand? LocalCommand { get; set; }

    public TerminalCashPaymentOutboxOperationType OperationType { get; set; }

    public int AttemptSequence { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public TerminalCashPaymentAttemptOutcome OutcomeClassification { get; set; }

    public int? HttpStatus { get; set; }

    public string? SafeErrorCode { get; set; }

    public required string CorrelationId { get; set; }
}
