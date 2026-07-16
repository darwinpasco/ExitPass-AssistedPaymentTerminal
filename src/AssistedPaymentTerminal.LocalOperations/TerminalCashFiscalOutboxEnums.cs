namespace AssistedPaymentTerminal.LocalOperations;

public enum TerminalCashFiscalCommandStatus
{
    Pending = 1,
    Submitting = 2,
    ReadbackRequired = 3,
    RetryPending = 4,
    Recorded = 5,
    Conflict = 6,
    Rejected = 7,
    Unknown = 8
}

public enum TerminalCashFiscalOperationType
{
    Readback = 1,
    Submit = 2
}

public enum TerminalCashFiscalAttemptOutcome
{
    Recorded = 1,
    NotFound = 2,
    Conflict = 3,
    Rejected = 4,
    Timeout = 5,
    Unavailable = 6,
    Unknown = 7
}
