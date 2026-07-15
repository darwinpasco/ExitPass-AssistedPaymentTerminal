namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CashJournalError(
    CashJournalErrorCode Code,
    string Message,
    Guid? ExistingCashTenderId = null,
    CashTenderState? ExistingCashTenderState = null);
