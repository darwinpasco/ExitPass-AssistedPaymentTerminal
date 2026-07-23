namespace AssistedPaymentTerminal.LocalOperations;

public enum TerminalCashReceiptRetrievalStatus
{
    Pending = 1,
    Retrieving = 2,
    NotReady = 3,
    RetryPending = 4,
    Available = 5,
    Voided = 6,
    Rejected = 7,
    Inconsistent = 8,
    Unavailable = 9,
    Unsupported = 10,
    Malformed = 11
}

public enum TerminalCashReceiptRetrievalAttemptOutcome
{
    Available = 1,
    NotFound = 2,
    NotReady = 3,
    Rejected = 4,
    Inconsistent = 5,
    Timeout = 6,
    Unavailable = 7,
    Unknown = 8,
    Unsupported = 9,
    Malformed = 10
}
