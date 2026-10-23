using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

internal static class TestRequests
{
    public static async Task<CashCustodySessionSnapshot> OpenShiftAndCreateSessionAsync(CashJournalService service)
    {
        var shift = await service.OpenCashierShiftAsync(OpenShift());
        if (!shift.IsSuccess)
        {
            throw new InvalidOperationException(shift.Error?.Message ?? "Test cashier shift setup failed.");
        }

        var session = await service.CreateCashCustodySessionAsync(CreateSession());
        if (!session.IsSuccess)
        {
            throw new InvalidOperationException(session.Error?.Message ?? "Test cash-custody setup failed.");
        }

        return session.Value!;
    }

    public static OpenCashierShiftRequest OpenShift() =>
        new(
            CashierShiftId: "shift-001",
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001",
            OpenedAt: DateTimeOffset.Parse("2026-07-15T00:00:00Z"));

    public static LocalOperationalStateRequest LocalOperationalState() =>
        new(
            CashierId: "cashier-001",
            CashierShiftId: "shift-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001");

    public static CashCustodyOverviewRequest CashCustodyOverview() =>
        new(
            CashierId: "cashier-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001");

    public static CreateCashCustodySessionRequest CreateSession(decimal openingCashAmount = 1_000m) =>
        new(
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-001",
            CashierShiftId: "shift-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001",
            Denominations: CustodyDenominations(openingCashAmount),
            OpenedAt: DateTimeOffset.Parse("2026-07-15T00:00:00Z"));

    public static CloseCashCustodySessionRequest CloseSession(
        Guid cashCustodySessionId,
        decimal closingCashAmount = 1_000m) =>
        new(
            CashCustodySessionId: cashCustodySessionId,
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-close-001",
            CashierShiftId: "shift-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001",
            Denominations: CustodyDenominations(closingCashAmount),
            ClosedAt: DateTimeOffset.Parse("2026-07-15T08:00:00Z"));

    public static IReadOnlyCollection<CashCustodyDenominationLine> CustodyDenominations(decimal amount)
    {
        var minorUnits = decimal.ToInt64(amount * 100m);
        var lines = new List<CashCustodyDenominationLine>(CashCustodyDenominationCatalog.Supported.Count);
        foreach (var denomination in CashCustodyDenominationCatalog.Supported)
        {
            var quantity = checked((int)(minorUnits / denomination.ValueMinorUnits));
            minorUnits %= denomination.ValueMinorUnits;
            lines.Add(new CashCustodyDenominationLine(denomination.Code, denomination.ValueMinorUnits, quantity));
        }

        if (minorUnits != 0)
        {
            throw new InvalidOperationException("The test amount cannot be represented by the custody denomination catalogue.");
        }

        return lines;
    }

    public static CloseCashierShiftRequest CloseShift() =>
        new(
            CashierShiftId: "shift-001",
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-close-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001",
            ClosedAt: DateTimeOffset.Parse("2026-07-15T08:01:00Z"));

    public static StartCashTenderRequest StartTender(
        Guid cashCustodySessionId,
        string parkingSessionId = "33333333-3333-4333-8333-333333333333",
        string tariffSnapshotId = "44444444-4444-4444-8444-444444444444",
        string localIdempotencyIdentity = "idem-001",
        decimal amountDue = 100m,
        decimal amountTendered = 100m) =>
        new(
            CashCustodySessionId: cashCustodySessionId,
            ParkingSessionId: parkingSessionId,
            TariffSnapshotId: tariffSnapshotId,
            Currency: "PHP",
            AmountDue: amountDue,
            AmountTendered: amountTendered,
            CorrelationId: Guid.NewGuid().ToString("D"),
            LocalIdempotencyIdentity: localIdempotencyIdentity,
            StartedAt: DateTimeOffset.Parse("2026-07-15T00:01:00Z"));

    public static CommitCashReceivedRequest CommitCashReceived(
        Guid localCashTenderId,
        bool cashierAttested = true,
        IReadOnlyCollection<CashDenominationLine>? denominations = null,
        StatutoryTenderEvidence? statutoryTenderEvidence = null) =>
        new(
            LocalCashTenderId: localCashTenderId,
            CashierAttested: cashierAttested,
            Denominations: denominations ?? [],
            StatutoryTenderEvidence: statutoryTenderEvidence,
            ReceivedAt: DateTimeOffset.Parse("2026-07-15T00:02:00Z"));
}
