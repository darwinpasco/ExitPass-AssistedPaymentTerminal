namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashFiscalAttempt
{
    public Guid Id { get; set; }

    public Guid LocalFiscalCommandId { get; set; }

    public TerminalCashFiscalOutboxCommand? LocalFiscalCommand { get; set; }

    public TerminalCashFiscalOperationType OperationType { get; set; }

    public int AttemptSequence { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public TerminalCashFiscalAttemptOutcome OutcomeClassification { get; set; }

    public int? HttpStatus { get; set; }

    public string? SafeErrorCode { get; set; }

    public required string CorrelationId { get; set; }
}
