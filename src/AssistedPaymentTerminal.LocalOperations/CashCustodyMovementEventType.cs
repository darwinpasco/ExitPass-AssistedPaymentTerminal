namespace AssistedPaymentTerminal.LocalOperations;

public enum CashCustodyMovementEventType
{
    Initiated = 1,
    Completed = 2,
    Cancelled = 3,
    Rejected = 4,
    CorrectionNoted = 5,
    DisputeRaised = 6,
    DisputeResolved = 7
}
