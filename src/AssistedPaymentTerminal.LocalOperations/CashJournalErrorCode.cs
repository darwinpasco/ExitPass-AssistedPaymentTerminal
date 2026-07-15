namespace AssistedPaymentTerminal.LocalOperations;

public enum CashJournalErrorCode
{
    NotFound = 1,
    DuplicateUnresolvedTender = 2,
    AmountTenderedBelowAmountDue = 3,
    CashierAttestationRequired = 4,
    InvalidStateTransition = 5
}
