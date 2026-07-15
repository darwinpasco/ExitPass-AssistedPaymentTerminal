namespace AssistedPaymentTerminal.LocalOperations;

public sealed record TerminalCashPaymentRequest(
    Guid TerminalCashTenderId,
    Guid CashCustodySessionId,
    Guid ParkingSessionId,
    Guid TariffSnapshotId,
    string CashierId,
    string CashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    Guid SiteId,
    Guid SiteGroupId,
    string PosServerId,
    string Currency,
    long AmountDueMinorUnits,
    long AmountTenderedMinorUnits,
    long ChangeDueMinorUnits,
    DateTimeOffset CashReceivedAt,
    IReadOnlyList<TerminalCashDenominationEntry> DenominationEntries,
    string LocalEventReference);

public sealed record TerminalCashDenominationEntry(
    string DenominationCode,
    long DenominationValueMinorUnits,
    int Quantity);

public sealed record TerminalCashPaymentResponse(
    Guid TerminalCashTenderId,
    Guid PaymentAttemptId,
    Guid PaymentConfirmationId,
    string CanonicalPaymentStatus,
    string ResultClassification,
    string IdempotencyScope,
    string SemanticHashSourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset LastUpdatedAt,
    Guid CorrelationId,
    string FiscalStatus);

public sealed record TerminalCashPaymentReadbackResponse(
    Guid TerminalCashTenderId,
    Guid CashCustodySessionId,
    Guid ParkingSessionId,
    Guid TariffSnapshotId,
    string TerminalId,
    Guid SiteId,
    Guid SiteGroupId,
    string PosServerId,
    string CashierId,
    string CashierShiftId,
    string Currency,
    long AmountDueMinorUnits,
    long AmountTenderedMinorUnits,
    long ChangeDueMinorUnits,
    string CanonicalPaymentStatus,
    Guid PaymentConfirmationId,
    string ResultClassification,
    string IdempotencyScope,
    string SemanticHashSourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset LastUpdatedAt,
    Guid CorrelationId,
    string FiscalStatus);

public sealed record CentralPmsSafeError(
    string ErrorCode,
    string Message,
    Guid CorrelationId,
    bool Retryable);
