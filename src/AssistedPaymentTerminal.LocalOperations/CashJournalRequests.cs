namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CreateCashCustodySessionRequest(
    string CashierId,
    string AuthenticatedCashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    string SiteId,
    string SiteGroupId,
    string PosServerId,
    decimal OpeningCashAmount,
    Guid? CashCustodySessionId = null,
    DateTimeOffset? OpenedAt = null);

public sealed record StartCashTenderRequest(
    Guid CashCustodySessionId,
    string ParkingSessionId,
    string TariffSnapshotId,
    string Currency,
    decimal AmountDue,
    decimal AmountTendered,
    string CorrelationId,
    string LocalIdempotencyIdentity,
    Guid? LocalCashTenderId = null,
    DateTimeOffset? StartedAt = null);

public sealed record CommitCashReceivedRequest(
    Guid LocalCashTenderId,
    bool CashierAttested,
    IReadOnlyCollection<CashDenominationLine> Denominations,
    DateTimeOffset? ReceivedAt = null);

public sealed record CashDenominationLine(
    string DenominationCode,
    decimal DenominationValue,
    int Quantity);
