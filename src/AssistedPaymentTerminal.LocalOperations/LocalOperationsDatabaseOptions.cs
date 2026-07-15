namespace AssistedPaymentTerminal.LocalOperations;

public sealed record LocalOperationsDatabaseOptions(
    string? DatabasePath = null,
    bool CashDrawerEnabled = false);
