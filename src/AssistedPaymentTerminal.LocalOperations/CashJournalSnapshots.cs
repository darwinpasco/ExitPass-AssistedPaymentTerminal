namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CashCustodySessionSnapshot(
    Guid Id,
    string CashierId,
    string AuthenticatedCashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    string SiteId,
    string SiteGroupId,
    string PosServerId,
    decimal OpeningCashAmount,
    DateTimeOffset OpenedAt,
    CashCustodySessionStatus Status)
{
    public static CashCustodySessionSnapshot FromEntity(CashCustodySession session) =>
        new(
            session.Id,
            session.CashierId,
            session.AuthenticatedCashierSessionReference,
            session.CashierShiftId,
            session.TerminalId,
            session.SiteId,
            session.SiteGroupId,
            session.PosServerId,
            session.OpeningCashAmount,
            session.OpenedAt,
            session.Status);
}

public sealed record CashTenderSnapshot(
    Guid Id,
    Guid CashCustodySessionId,
    string ParkingSessionId,
    string TariffSnapshotId,
    string Currency,
    decimal AmountDue,
    decimal AmountTendered,
    decimal ChangeDue,
    string CorrelationId,
    string LocalIdempotencyIdentity,
    CashTenderState CurrentLocalState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CashTenderSnapshot FromEntity(CashTender tender) =>
        new(
            tender.Id,
            tender.CashCustodySessionId,
            tender.ParkingSessionId,
            tender.TariffSnapshotId,
            tender.Currency,
            tender.AmountDue,
            tender.AmountTendered,
            tender.ChangeDue,
            tender.CorrelationId,
            tender.LocalIdempotencyIdentity,
            tender.CurrentLocalState,
            tender.CreatedAt,
            tender.UpdatedAt);
}

public sealed record CashTenderEventSnapshot(
    Guid Id,
    Guid CashTenderId,
    CashTenderEventType EventType,
    DateTimeOffset OccurredAt,
    decimal AmountTendered,
    decimal ChangeDue,
    bool CashierAttested,
    string ActorCashierId,
    string CorrelationId,
    IReadOnlyList<CashDenominationEntrySnapshot> DenominationEntries)
{
    public static CashTenderEventSnapshot FromEntity(CashTenderEvent cashEvent) =>
        new(
            cashEvent.Id,
            cashEvent.CashTenderId,
            cashEvent.EventType,
            cashEvent.OccurredAt,
            cashEvent.AmountTendered,
            cashEvent.ChangeDue,
            cashEvent.CashierAttested,
            cashEvent.ActorCashierId,
            cashEvent.CorrelationId,
            cashEvent.DenominationEntries
                .OrderBy(entry => entry.DenominationValue)
                .Select(CashDenominationEntrySnapshot.FromEntity)
                .ToArray());
}

public sealed record CashDenominationEntrySnapshot(
    Guid Id,
    string DenominationCode,
    decimal DenominationValue,
    int Quantity)
{
    public static CashDenominationEntrySnapshot FromEntity(CashDenominationEntry entry) =>
        new(entry.Id, entry.DenominationCode, entry.DenominationValue, entry.Quantity);
}
