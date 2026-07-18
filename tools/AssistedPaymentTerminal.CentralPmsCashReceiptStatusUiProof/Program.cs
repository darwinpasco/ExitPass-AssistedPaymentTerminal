using System.Text.Json;
using AssistedPaymentTerminal.CentralPmsCashReceiptStatusUiProof;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;

var proofOptions = ReceiptProofArguments.Parse(args);
if (proofOptions.Interactive)
{
    await RunInteractiveHostAsync(proofOptions);
    return;
}

var databasePath = proofOptions.DatabasePath
    ?? throw new ArgumentException("Missing required --database-path argument.");
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fullDatabasePath = Path.GetFullPath(databasePath);
if (fullDatabasePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Proof database path must be outside the Git repository.");
}

await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.Available, "Available", expectedStatus: "Available");
await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.NotReady, "NotReady", expectedStatus: "NotReady");
await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.RetryPending, "RetryPending", expectedStatus: "Unavailable");
await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.Inconsistent, "Inconsistent", expectedStatus: "Inconsistent");
await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.Rejected, "Rejected", expectedStatus: "Rejected");
await RunScenarioProofAsync(fullDatabasePath, ReceiptStatusUiProofScenario.Voided, "Voided", expectedStatus: "Voided");
await RunUnavailableThenAvailableAsync(fullDatabasePath);

Console.WriteLine("recorded fiscal state exists.");
Console.WriteLine("one receipt-retrieval record exists per terminal cash tender.");
Console.WriteLine("explicit retrieval invokes the persisted receipt command.");
Console.WriteLine("available metadata is returned without rendering the opaque payload.");
Console.WriteLine("not-ready, retry, inconsistent, rejected, and voided states map safely.");
Console.WriteLine("restart restores receipt status from the same database.");
Console.WriteLine("no duplicate retrieval record was created.");
Console.WriteLine("no rendering, printing, exit, gate, provider, or direct POS Server behavior was executed.");
Console.WriteLine("Central PMS cash receipt status UI proof completed successfully.");

static async Task RunInteractiveHostAsync(ReceiptProofArguments options)
{
    await using var host = InteractiveCentralPmsReceiptProofHost.Start(options.Scenario, options.Port);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    Console.WriteLine($"Selected scenario: {host.Scenario}");
    Console.WriteLine($"CENTRAL_PMS_BASE_URL={host.BaseUrl}");
    Console.WriteLine($"PowerShell: $env:CENTRAL_PMS_BASE_URL = \"{host.BaseUrl}\"");
    Console.WriteLine("Press Ctrl+C to stop the focused Central PMS receipt proof host.");
    Console.Out.Flush();
    await host.RunUntilCancelledAsync(cancellation.Token);
}

static async Task RunScenarioProofAsync(
    string databasePath,
    ReceiptStatusUiProofScenario scenario,
    string suffix,
    string expectedStatus)
{
    await using var host = InteractiveCentralPmsReceiptProofHost.Start(scenario);
    using var cancellation = new CancellationTokenSource();
    _ = Task.Run(() => host.RunUntilCancelledAsync(cancellation.Token));
    var handler = CreateBridge(databasePath, host.BaseUrl.ToString(), receiptEnabled: true);
    var tenderId = await CreateRecordedFiscalTenderAsync(handler, suffix);

    var status = await GetReceiptStatusAsync(handler, tenderId);
    Require(status.GetProperty("command").GetProperty("status").GetString() == "Pending", $"{scenario} receipt command did not start pending.");

    var retrieved = await RetrieveReceiptAsync(handler, tenderId);
    Require(retrieved.GetProperty("command").GetProperty("status").GetString() == expectedStatus, $"{scenario} did not map to {expectedStatus}.");
    Require(await CountReceiptCommandsAsync(databasePath, tenderId) == 1, $"{scenario} created duplicate receipt commands.");
    Require(!retrieved.GetProperty("command").TryGetProperty("authoritativePresentationJson", out _), "Raw authoritative payload leaked through the bridge.");
    cancellation.Cancel();
}

static async Task RunUnavailableThenAvailableAsync(string databasePath)
{
    await using var host = InteractiveCentralPmsReceiptProofHost.Start(ReceiptStatusUiProofScenario.UnavailableThenAvailable);
    using var cancellation = new CancellationTokenSource();
    _ = Task.Run(() => host.RunUntilCancelledAsync(cancellation.Token));
    var handler = CreateBridge(databasePath, host.BaseUrl.ToString(), receiptEnabled: true);
    var tenderId = await CreateRecordedFiscalTenderAsync(handler, "UnavailableThenAvailable");

    var first = await RetrieveReceiptAsync(handler, tenderId);
    Require(first.GetProperty("command").GetProperty("status").GetString() == "Unavailable", "First unavailable receipt result was not durable.");

    handler = CreateBridge(databasePath, host.BaseUrl.ToString(), receiptEnabled: true);
    var second = await RetrieveReceiptAsync(handler, tenderId);
    Require(second.GetProperty("command").GetProperty("status").GetString() == "Available", "Restart-style receipt check did not restore available status.");
    Require(await CountReceiptCommandsAsync(databasePath, tenderId) == 1, "UnavailableThenAvailable created duplicate receipt commands.");
    Require(
        host.RequestLog.Where(entry => entry.Operation == "terminal-cash-receipt-presentation").Select(entry => entry.Method).SequenceEqual(["GET", "GET"]),
        "Receipt proof did not use repeated GET readback.");
    cancellation.Cancel();
}

static LocalJournalBridgeHandler CreateBridge(string databasePath, string baseUrl, bool receiptEnabled)
{
    var options = new LocalOperationsDatabaseOptions(
        databasePath,
        CentralPmsBaseUrl: baseUrl,
        EnableCentralPmsCashSubmission: true,
        EnableCentralPmsFiscalIssuance: true,
        EnableCentralPmsReceiptRetrieval: receiptEnabled);
    var journal = new CashJournalService(options);
    return new LocalJournalBridgeHandler(
        journal,
        enabled: true,
        centralPmsCashSubmissionEnabled: true,
        centralPmsFiscalIssuanceEnabled: true,
        centralPmsReceiptRetrievalEnabled: receiptEnabled,
        centralPmsBaseUrl: baseUrl,
        submissionService: new TerminalCashPaymentSubmissionService(new CentralPmsTerminalCashPaymentClient(new HttpClient()), options),
        fiscalService: new TerminalCashFiscalSubmissionService(new CentralPmsTerminalCashFiscalClient(new HttpClient()), options),
        receiptService: new TerminalCashReceiptRetrievalService(new CentralPmsTerminalCashReceiptClient(new HttpClient()), options));
}

static async Task<Guid> CreateRecordedFiscalTenderAsync(LocalJournalBridgeHandler handler, string suffix)
{
    var tenderId = await CreateLocalCashReceivedAsync(handler, suffix);
    await SubmitPaymentAsync(handler, tenderId);
    var fiscal = await SubmitFiscalAsync(handler, tenderId);
    Require(fiscal.GetProperty("command").GetProperty("status").GetString() == "Recorded", "Fiscal document was not recorded before receipt retrieval.");
    return tenderId;
}

static async Task<Guid> CreateLocalCashReceivedAsync(LocalJournalBridgeHandler handler, string suffix)
{
    var session = await SendAsync(handler, LocalJournalBridgeCommand.CreateOrGetDevelopmentSession, $"receipt-proof-session-{suffix}", new
    {
        cashierId = $"cashier-{suffix}",
        authenticatedCashierSessionReference = $"auth-{suffix}",
        cashierShiftId = $"shift-{suffix}",
        terminalId = "terminal-proof",
        siteId = "11111111-1111-4111-8111-111111111111",
        siteGroupId = "22222222-2222-4222-8222-222222222222",
        posServerId = "pos-proof",
        openingCashAmount = 0m
    });
    Require(session.RootElement.GetProperty("ok").GetBoolean(), "Session creation failed.");
    var sessionId = session.RootElement.GetProperty("payload").GetProperty("id").GetGuid();

    var tender = await SendAsync(handler, LocalJournalBridgeCommand.StartTender, Guid.NewGuid().ToString("D"), new
    {
        cashCustodySessionId = sessionId,
        parkingSessionId = Guid.NewGuid().ToString("D"),
        tariffSnapshotId = Guid.NewGuid().ToString("D"),
        currency = "PHP",
        amountDue = 125m,
        amountTendered = 150m,
        localIdempotencyIdentity = $"receipt-proof-idempotency-{Guid.NewGuid():N}"
    });
    Require(tender.RootElement.GetProperty("ok").GetBoolean(), "Tender start failed.");
    var tenderId = tender.RootElement.GetProperty("payload").GetProperty("id").GetGuid();

    var received = await SendAsync(handler, LocalJournalBridgeCommand.RecordCashReceived, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId,
        cashierAttested = true,
        denominations = new[] { new { denominationCode = "PHP-100", denominationValue = 100m, quantity = 1 } }
    });
    Require(received.RootElement.GetProperty("ok").GetBoolean(), "CASH_RECEIVED failed.");
    return tenderId;
}

static Task<JsonElement> SubmitPaymentAsync(LocalJournalBridgeHandler handler, Guid tenderId) =>
    PayloadAsync(SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, Guid.NewGuid().ToString("D"), new { localCashTenderId = tenderId }));

static Task<JsonElement> SubmitFiscalAsync(LocalJournalBridgeHandler handler, Guid tenderId) =>
    PayloadAsync(SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, Guid.NewGuid().ToString("D"), new { localCashTenderId = tenderId }));

static Task<JsonElement> GetReceiptStatusAsync(LocalJournalBridgeHandler handler, Guid tenderId) =>
    PayloadAsync(SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus, Guid.NewGuid().ToString("D"), new { localCashTenderId = tenderId }));

static Task<JsonElement> RetrieveReceiptAsync(LocalJournalBridgeHandler handler, Guid tenderId) =>
    PayloadAsync(SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, Guid.NewGuid().ToString("D"), new { localCashTenderId = tenderId }));

static async Task<JsonElement> PayloadAsync(Task<JsonDocument> responseTask)
{
    using var response = await responseTask.ConfigureAwait(false);
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Bridge command failed.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
{
    var request = JsonSerializer.Serialize(
        new { source = LocalJournalBridgeCommand.Source, command, correlationId, payload },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var response = await handler.HandleWebMessageAsync(request)
        ?? throw new InvalidOperationException("Bridge did not return a response.");
    return JsonDocument.Parse(response);
}

static async Task<int> CountReceiptCommandsAsync(string databasePath, Guid tenderId)
{
    var options = new LocalOperationsDatabaseOptions(databasePath);
    await using var dbContext = new CashJournalService(options).CreateDbContext();
    return await dbContext.TerminalCashReceiptRetrievalCommands.CountAsync(command => command.TerminalCashTenderId == tenderId);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed record ReceiptProofArguments(
    bool Interactive,
    ReceiptStatusUiProofScenario Scenario,
    int Port,
    string? DatabasePath)
{
    public static ReceiptProofArguments Parse(string[] args)
    {
        var interactive = false;
        var scenario = ReceiptStatusUiProofScenario.Available;
        var port = 0;
        string? databasePath = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--interactive", StringComparison.OrdinalIgnoreCase))
            {
                interactive = true;
                continue;
            }

            if (string.Equals(args[index], "--scenario", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!InteractiveCentralPmsReceiptProofHost.TryParseScenario(args[++index], out scenario))
                {
                    throw new ArgumentException($"Unsupported scenario '{args[index]}'. Supported scenarios: {string.Join(", ", Enum.GetNames<ReceiptStatusUiProofScenario>())}.");
                }

                continue;
            }

            if (string.Equals(args[index], "--port", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!int.TryParse(args[++index], out port) || port < 0)
                {
                    throw new ArgumentException("Port must be zero or a positive TCP port number.");
                }

                continue;
            }

            if (string.Equals(args[index], "--database-path", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                databasePath = Path.GetFullPath(args[++index]);
            }
        }

        return new ReceiptProofArguments(interactive, scenario, port, databasePath);
    }
}
