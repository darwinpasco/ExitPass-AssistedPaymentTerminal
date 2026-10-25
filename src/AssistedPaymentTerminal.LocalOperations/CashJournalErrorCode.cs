namespace AssistedPaymentTerminal.LocalOperations;

public enum CashJournalErrorCode
{
    NotFound = 1,
    DuplicateUnresolvedTender = 2,
    AmountTenderedBelowAmountDue = 3,
    CashierAttestationRequired = 4,
    InvalidStateTransition = 5,
    InvalidDenominationEvidence = 6,
    ConflictingDenominationEvidence = 7,
    PendingCashCustodyMovement = 8,
    CashCustodyMovementConflict = 9,
    InsufficientCashCustody = 10,
    InvalidOriginalMovement = 11,
    CashReturnLimitExceeded = 12,
    DistinctPrincipalRequired = 13
}
