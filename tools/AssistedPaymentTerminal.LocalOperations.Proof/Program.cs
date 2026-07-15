using AssistedPaymentTerminal.LocalOperations;

var databasePath = GetDatabasePath(args);
var service = new CashJournalService(new LocalOperationsDatabaseOptions(databasePath));

var session = await RequireSuccess(service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
    CashierId: "proof-cashier",
    AuthenticatedCashierSessionReference: "proof-auth-session",
    CashierShiftId: "proof-shift",
    TerminalId: "proof-terminal",
    SiteId: "proof-site",
    SiteGroupId: "proof-site-group",
    PosServerId: "proof-pos-server",
    OpeningCashAmount: 500m)));

Console.WriteLine($"cash-custody session created: {session.Id}");

var tender = await RequireSuccess(service.StartCashTenderAsync(new StartCashTenderRequest(
    CashCustodySessionId: session.Id,
    ParkingSessionId: "proof-parking-session",
    TariffSnapshotId: "proof-tariff-snapshot",
    Currency: "PHP",
    AmountDue: 120m,
    AmountTendered: 150m,
    CorrelationId: "proof-correlation",
    LocalIdempotencyIdentity: "proof-idempotency")));

Console.WriteLine($"TENDER_STARTED recorded: {tender.Id}");

var cashReceived = await RequireSuccess(service.CommitCashReceivedAsync(new CommitCashReceivedRequest(
    LocalCashTenderId: tender.Id,
    CashierAttested: true,
    Denominations:
    [
        new CashDenominationLine("PHP-100", 100m, 1),
        new CashDenominationLine("PHP-50", 50m, 1)
    ])));

if (cashReceived.CurrentLocalState != CashTenderState.CashReceived)
{
    throw new InvalidOperationException("CASH_RECEIVED was not committed.");
}

Console.WriteLine($"CASH_RECEIVED committed with change due: {cashReceived.ChangeDue}");

service = new CashJournalService(new LocalOperationsDatabaseOptions(databasePath));
var readback = await service.GetCashTenderAsync(tender.Id)
    ?? throw new InvalidOperationException("CASH_RECEIVED tender was not found after database reopen.");

if (readback.CurrentLocalState != CashTenderState.CashReceived)
{
    throw new InvalidOperationException($"Unexpected state after reopen: {readback.CurrentLocalState}");
}

var events = await service.GetCashTenderEventsAsync(tender.Id);
if (events.Select(value => value.EventType).ToArray() is not [CashTenderEventType.TenderStarted, CashTenderEventType.CashReceived])
{
    throw new InvalidOperationException("Append-only event history was not preserved.");
}

Console.WriteLine("CASH_RECEIVED read back after database reopen.");
Console.WriteLine($"event history count: {events.Count}");

var duplicate = await service.StartCashTenderAsync(new StartCashTenderRequest(
    CashCustodySessionId: session.Id,
    ParkingSessionId: "proof-parking-session",
    TariffSnapshotId: "proof-tariff-snapshot-duplicate",
    Currency: "PHP",
    AmountDue: 120m,
    AmountTendered: 120m,
    CorrelationId: "proof-correlation-duplicate",
    LocalIdempotencyIdentity: "proof-idempotency-duplicate"));

if (duplicate.IsSuccess || duplicate.Error?.Code != CashJournalErrorCode.DuplicateUnresolvedTender)
{
    throw new InvalidOperationException("Duplicate tender was not rejected deterministically.");
}

Console.WriteLine($"duplicate tender rejected: {duplicate.Error.ExistingCashTenderId}");
Console.WriteLine("cash journal durability proof completed successfully.");

static string GetDatabasePath(string[] args)
{
    const string databasePathArgument = "--database-path";

    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], databasePathArgument, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(args[index + 1]);
        }
    }

    throw new ArgumentException("Missing required --database-path argument.");
}

static async Task<T> RequireSuccess<T>(Task<CashJournalResult<T>> operation)
{
    var result = await operation;

    if (!result.IsSuccess)
    {
        throw new InvalidOperationException($"{result.Error?.Code}: {result.Error?.Message}");
    }

    return result.Value!;
}
