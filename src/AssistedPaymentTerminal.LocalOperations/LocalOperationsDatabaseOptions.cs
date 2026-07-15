namespace AssistedPaymentTerminal.LocalOperations;

public sealed record LocalOperationsDatabaseOptions(
    string? DatabasePath = null,
    bool CashDrawerEnabled = false,
    string CentralPmsBaseUrl = "UNCONFIGURED_CENTRAL_PMS",
    bool EnableCentralPmsCashSubmission = false,
    int CentralPmsTimeoutSeconds = 10);
