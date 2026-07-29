using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

internal static class TestRequests
{
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

    public static CreateCashCustodySessionRequest CreateSession() =>
        new(
            CashierId: "cashier-001",
            AuthenticatedCashierSessionReference: "auth-session-001",
            CashierShiftId: "shift-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001",
            OpeningCashAmount: 1_000m,
            OpenedAt: DateTimeOffset.Parse("2026-07-15T00:00:00Z"));

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
