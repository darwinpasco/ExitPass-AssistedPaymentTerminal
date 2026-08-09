using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;

var databasePath = GetDatabasePath(args);
var service = new CashJournalService(new LocalOperationsDatabaseOptions(databasePath));

if (args.Any(value => string.Equals(value, "--create-plaintext", StringComparison.OrdinalIgnoreCase)))
{
    await CreatePlaintextFixtureAsync(databasePath);
    Console.WriteLine($"plaintext fixture created: {databasePath}");
    return;
}

if (args.Any(value => string.Equals(value, "--seed-active-shift-only", StringComparison.OrdinalIgnoreCase)))
{
    await RequireSuccess(service.OpenCashierShiftAsync(DevelopmentShift()));
    Console.WriteLine("active shift seeded: SHIFT-DEV-20260714-A");
    await PrintStateAsync(service);
    return;
}

if (args.Any(value => string.Equals(value, "--seed-active-shift-and-custody", StringComparison.OrdinalIgnoreCase)))
{
    await RequireSuccess(service.OpenCashierShiftAsync(DevelopmentShift()));
    var seededSession = await RequireSuccess(service.CreateOrGetCashCustodySessionAsync(DevelopmentCustody()));
    Console.WriteLine("active shift seeded: SHIFT-DEV-20260714-A");
    Console.WriteLine($"active cash-custody session seeded: {seededSession.Id}");
    await PrintStateAsync(service);
    return;
}

if (args.Any(value => string.Equals(value, "--seed-closed-shift", StringComparison.OrdinalIgnoreCase)))
{
    await RequireSuccess(service.OpenCashierShiftAsync(DevelopmentShift()));
    var seededSession = await RequireSuccess(service.CreateOrGetCashCustodySessionAsync(DevelopmentCustody()));
    await RequireSuccess(service.CloseCashierShiftAsync(new CloseCashierShiftRequest(
        "SHIFT-DEV-20260714-A",
        DateTimeOffset.Parse("2026-07-29T08:30:00Z"))));
    Console.WriteLine("closed shift seeded: SHIFT-DEV-20260714-A");
    Console.WriteLine($"cash-custody session linked to closed shift: {seededSession.Id}");
    await PrintStateAsync(service);
    return;
}

if (args.Any(value => string.Equals(value, "--inspect-state", StringComparison.OrdinalIgnoreCase)))
{
    await PrintStateAsync(service);
    return;
}

await RequireSuccess(service.OpenCashierShiftAsync(new OpenCashierShiftRequest(
    CashierShiftId: "proof-shift",
    CashierId: "proof-cashier",
    AuthenticatedCashierSessionReference: "proof-auth-session",
    TerminalId: "proof-terminal",
    SiteId: "11111111-1111-4111-8111-111111111111",
    SiteGroupId: "22222222-2222-4222-8222-222222222222",
    PosServerId: "proof-pos-server")));
var session = await RequireSuccess(service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
    CashierId: "proof-cashier",
    AuthenticatedCashierSessionReference: "proof-auth-session",
    CashierShiftId: "proof-shift",
    TerminalId: "proof-terminal",
    SiteId: "11111111-1111-4111-8111-111111111111",
    SiteGroupId: "22222222-2222-4222-8222-222222222222",
    PosServerId: "proof-pos-server",
    OpeningCashAmount: 500m)));

Console.WriteLine($"cash-custody session created: {session.Id}");

var tender = await RequireSuccess(service.StartCashTenderAsync(new StartCashTenderRequest(
    CashCustodySessionId: session.Id,
    ParkingSessionId: "33333333-3333-4333-8333-333333333333",
    TariffSnapshotId: "44444444-4444-4444-8444-444444444444",
    Currency: "PHP",
    AmountDue: 120m,
    AmountTendered: 150m,
    CorrelationId: "55555555-5555-4555-8555-555555555555",
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
    ParkingSessionId: "33333333-3333-4333-8333-333333333333",
    TariffSnapshotId: "66666666-6666-4666-8666-666666666666",
    Currency: "PHP",
    AmountDue: 120m,
    AmountTendered: 120m,
    CorrelationId: "77777777-7777-4777-8777-777777777777",
    LocalIdempotencyIdentity: "proof-idempotency-duplicate"));

if (duplicate.IsSuccess || duplicate.Error?.Code != CashJournalErrorCode.DuplicateUnresolvedTender)
{
    throw new InvalidOperationException("Duplicate tender was not rejected deterministically.");
}

Console.WriteLine($"duplicate tender rejected: {duplicate.Error.ExistingCashTenderId}");
Console.WriteLine("cash journal durability proof completed successfully.");

static async Task PrintStateAsync(CashJournalService service)
{
    var state = await service.GetLocalOperationalStateAsync(DevelopmentScope());
    Console.WriteLine($"database path: {service.DatabasePath}");
    Console.WriteLine($"active shift record count: {state.ActiveShiftRecordCount}");
    Console.WriteLine($"active cash-custody record count: {state.ActiveCashCustodySessionRecordCount}");
    Console.WriteLine($"active shift id: {state.ActiveShift?.Id ?? "None"}");
    Console.WriteLine($"active cash-custody id: {state.ActiveCashCustodySession?.Id.ToString("D") ?? "None"}");
    Console.WriteLine($"active shift status: {state.ActiveShift?.Status.ToString() ?? "None"}");
    Console.WriteLine($"active cash-custody status: {state.ActiveCashCustodySession?.Status.ToString() ?? "None"}");
}

static async Task CreatePlaintextFixtureAsync(string databasePath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    SQLitePCL.Batteries_V2.Init();
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE manual_plaintext_fixture (id TEXT NOT NULL PRIMARY KEY, known_value TEXT NOT NULL); INSERT INTO manual_plaintext_fixture VALUES ('fixture-1', 'APT-MANUAL-PLAINTEXT-FIXTURE-20260729');";
    await command.ExecuteNonQueryAsync();
}

static OpenCashierShiftRequest DevelopmentShift() =>
    new(
        CashierShiftId: "SHIFT-DEV-20260714-A",
        CashierId: "CASHIER-DEV-001",
        AuthenticatedCashierSessionReference: "manual-validation-auth-session",
        TerminalId: "APT-DEV-001",
        SiteId: "11111111-1111-1111-1111-111111111111",
        SiteGroupId: "22222222-2222-2222-2222-222222222222",
        PosServerId: "POS-DEV-001",
        OpenedAt: DateTimeOffset.Parse("2026-07-29T08:00:00Z"));

static CreateCashCustodySessionRequest DevelopmentCustody() =>
    new(
        CashierId: "CASHIER-DEV-001",
        AuthenticatedCashierSessionReference: "manual-validation-auth-session",
        CashierShiftId: "SHIFT-DEV-20260714-A",
        TerminalId: "APT-DEV-001",
        SiteId: "11111111-1111-1111-1111-111111111111",
        SiteGroupId: "22222222-2222-2222-2222-222222222222",
        PosServerId: "POS-DEV-001",
        OpeningCashAmount: 500m,
        CashCustodySessionId: Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa0001"),
        OpenedAt: DateTimeOffset.Parse("2026-07-29T08:05:00Z"));

static LocalOperationalStateRequest DevelopmentScope() =>
    new(
        CashierId: "CASHIER-DEV-001",
        CashierShiftId: "SHIFT-DEV-20260714-A",
        TerminalId: "APT-DEV-001",
        SiteId: "11111111-1111-1111-1111-111111111111",
        SiteGroupId: "22222222-2222-2222-2222-222222222222",
        PosServerId: "POS-DEV-001");

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
