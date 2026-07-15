using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

internal static class TestRequests
{
    public static CreateCashCustodySessionRequest CreateSession() =>
        new(
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-001",
            CashierShiftId: "shift-001",
            TerminalId: "terminal-001",
            SiteId: "site-001",
            SiteGroupId: "site-group-001",
            PosServerId: "pos-server-001",
            OpeningCashAmount: 1_000m,
            OpenedAt: DateTimeOffset.Parse("2026-07-15T00:00:00Z"));

    public static StartCashTenderRequest StartTender(
        Guid cashCustodySessionId,
        string parkingSessionId = "parking-session-001",
        string localIdempotencyIdentity = "idem-001",
        decimal amountDue = 100m,
        decimal amountTendered = 100m) =>
        new(
            CashCustodySessionId: cashCustodySessionId,
            ParkingSessionId: parkingSessionId,
            TariffSnapshotId: "tariff-snapshot-001",
            Currency: "PHP",
            AmountDue: amountDue,
            AmountTendered: amountTendered,
            CorrelationId: $"corr-{Guid.NewGuid():N}",
            LocalIdempotencyIdentity: localIdempotencyIdentity,
            StartedAt: DateTimeOffset.Parse("2026-07-15T00:01:00Z"));

    public static CommitCashReceivedRequest CommitCashReceived(
        Guid localCashTenderId,
        bool cashierAttested = true,
        IReadOnlyCollection<CashDenominationLine>? denominations = null) =>
        new(
            LocalCashTenderId: localCashTenderId,
            CashierAttested: cashierAttested,
            Denominations: denominations ?? [],
            ReceivedAt: DateTimeOffset.Parse("2026-07-15T00:02:00Z"));
}
