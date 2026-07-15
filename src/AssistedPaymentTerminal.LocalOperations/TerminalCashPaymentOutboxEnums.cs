namespace AssistedPaymentTerminal.LocalOperations;

public enum TerminalCashPaymentCommandStatus
{
    Pending = 1,
    Submitting = 2,
    ReadbackRequired = 3,
    RetryPending = 4,
    Confirmed = 5,
    Conflict = 6,
    Rejected = 7
}

public enum TerminalCashPaymentOutboxOperationType
{
    Readback = 1,
    Submit = 2
}

public enum TerminalCashPaymentAttemptOutcome
{
    Confirmed = 1,
    NotFound = 2,
    Conflict = 3,
    Rejected = 4,
    Timeout = 5,
    Unavailable = 6,
    Unknown = 7
}
