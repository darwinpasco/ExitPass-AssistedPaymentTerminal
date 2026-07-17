namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashReceiptRetrievalAttempt
{
    public Guid Id { get; set; }

    public Guid LocalReceiptRetrievalId { get; set; }

    public TerminalCashReceiptRetrievalCommand? LocalReceiptRetrieval { get; set; }

    public int AttemptSequence { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public TerminalCashReceiptRetrievalAttemptOutcome OutcomeClassification { get; set; }

    public int? HttpStatus { get; set; }

    public string? SafeErrorCode { get; set; }

    public required string CorrelationId { get; set; }
}
