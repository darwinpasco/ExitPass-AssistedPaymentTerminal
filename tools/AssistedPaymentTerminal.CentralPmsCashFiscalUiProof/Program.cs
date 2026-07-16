using System.Text.Json;
using AssistedPaymentTerminal.CentralPmsCashFiscalUiProof;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;

var proofOptions = ProofArguments.Parse(args);
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

const string baseUrl = "http://127.0.0.1:18080";
var cashClient = new ScriptedCentralPmsClient();
var fiscalClient = new ScriptedCentralPmsFiscalClient();
var handler = CreateBridge(fullDatabasePath, baseUrl, cashClient, fiscalClient, fiscalEnabled: true);

var tenderId = await CreateLocalCashReceivedAsync(handler, "FISCAL");
cashClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(PaymentSuccess(tenderId), 201));
var payment = await SubmitPaymentAsync(handler, tenderId);
Require(payment.GetProperty("command").GetProperty("status").GetString() == "Confirmed", "Canonical payment confirmation was not restored through the bridge.");
Console.WriteLine("canonical payment confirmation exists.");

var fiscalStatus = await GetFiscalStatusAsync(handler, tenderId);
Require(fiscalStatus.GetProperty("command").GetProperty("status").GetString() == "Pending", "Fiscal command was not available after canonical confirmation.");
Require(await CountFiscalCommandsAsync(fullDatabasePath, tenderId) == 1, "Expected exactly one fiscal command.");
Console.WriteLine("one fiscal command exists.");

fiscalClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());
var uncertain = await SubmitFiscalAsync(handler, tenderId);
Require(uncertain.GetProperty("command").GetProperty("status").GetString() == "ReadbackRequired", "Uncertain fiscal result was not persisted.");
Console.WriteLine("explicit fiscal submission invoked the persisted command and recorded uncertainty.");

handler = CreateBridge(fullDatabasePath, baseUrl, cashClient, fiscalClient, fiscalEnabled: true);
var fiscalCommand = await ReadFiscalCommandAsync(fullDatabasePath, tenderId);
fiscalClient.EnqueueReadback(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(fiscalCommand), 200));
var recorded = await SubmitFiscalAsync(handler, tenderId);
Require(recorded.GetProperty("command").GetProperty("status").GetString() == "Recorded", "Fiscal readback did not record completion.");
Require(fiscalClient.OperationsFor(tenderId).SequenceEqual([TerminalCashFiscalOperationType.Submit, TerminalCashFiscalOperationType.Readback]), "Fiscal retry did not read back before another POST.");
Require(recorded.GetProperty("command").GetProperty("fiscalIssuanceReferenceId").GetGuid() == Recorded(fiscalCommand).FiscalIssuanceReferenceId, "Fiscal reference was not restored.");
Require(recorded.GetProperty("command").GetProperty("posFiscalDocumentId").GetGuid() == Recorded(fiscalCommand).PosFiscalDocumentId, "POS fiscal document ID was not restored.");
Console.WriteLine("readback occurred before retry and restored recorded fiscal identifiers.");

var replayTenderId = await CreateConfirmedTenderAsync(fullDatabasePath, baseUrl, "REPLAY");
var replayCommand = await ReadFiscalCommandAsync(fullDatabasePath, replayTenderId);
var replayClient = new ScriptedCentralPmsFiscalClient();
handler = CreateBridge(fullDatabasePath, baseUrl, new ScriptedCentralPmsClient(), replayClient, fiscalEnabled: true);
replayClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(replayCommand, "IDEMPOTENT_REPLAY"), 200));
var replay = await SubmitFiscalAsync(handler, replayTenderId);
Require(replay.GetProperty("command").GetProperty("resultClassification").GetString() == "IDEMPOTENT_REPLAY", "Replay classification was not preserved.");
Console.WriteLine("replay preserved the same fiscal document identifiers without duplicate-document behavior.");

var conflictTenderId = await CreateConfirmedTenderAsync(fullDatabasePath, baseUrl, "CONFLICT");
var conflictClient = new ScriptedCentralPmsFiscalClient();
handler = CreateBridge(fullDatabasePath, baseUrl, new ScriptedCentralPmsClient(), conflictClient, fiscalEnabled: true);
conflictClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Conflict(409, "TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT"));
var conflict = await SubmitFiscalAsync(handler, conflictTenderId);
Require(conflict.GetProperty("command").GetProperty("status").GetString() == "Conflict", "Conflict did not map safely.");
Console.WriteLine("conflict mapped safely.");

var rejectedTenderId = await CreateConfirmedTenderAsync(fullDatabasePath, baseUrl, "REJECTED");
var rejectedClient = new ScriptedCentralPmsFiscalClient();
handler = CreateBridge(fullDatabasePath, baseUrl, new ScriptedCentralPmsClient(), rejectedClient, fiscalEnabled: true);
rejectedClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Rejected(400, "CORRELATION_ID_REQUIRED"));
var rejected = await SubmitFiscalAsync(handler, rejectedTenderId);
Require(rejected.GetProperty("command").GetProperty("status").GetString() == "Rejected", "Rejection did not map safely.");
Console.WriteLine("rejection mapped safely.");

var disabledHandler = CreateBridge(fullDatabasePath, baseUrl, new ScriptedCentralPmsClient(), new ScriptedCentralPmsFiscalClient(), fiscalEnabled: false);
using var disabledResponse = await SendAsync(
    disabledHandler,
    LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback,
    "proof-disabled",
    new { localCashTenderId = rejectedTenderId });
Require(!disabledResponse.RootElement.GetProperty("ok").GetBoolean(), "Disabled fiscal submission should not succeed.");
Require(disabledResponse.RootElement.GetProperty("error").GetProperty("code").GetString() == "feature_disabled", "Disabled fiscal submission did not return feature_disabled.");
Console.WriteLine("default-off fiscal behavior enforced at the bridge boundary.");

Console.WriteLine("no duplicate fiscal command was created.");
Console.WriteLine("no receipt, exit, gate, Payment Orchestrator, or direct POS Server behavior was executed.");
Console.WriteLine("Central PMS cash fiscal UI proof completed successfully.");

static async Task RunInteractiveHostAsync(ProofArguments options)
{
    await using var host = InteractiveCentralPmsFiscalProofHost.Start(options.Scenario, options.Port);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    Console.WriteLine($"Selected scenario: {host.Scenario}");
    Console.WriteLine($"CENTRAL_PMS_BASE_URL={host.BaseUrl}");
    Console.WriteLine($"PowerShell: $env:CENTRAL_PMS_BASE_URL = \"{host.BaseUrl}\"");
    Console.WriteLine("Press Ctrl+C to stop the focused Central PMS proof host.");
    Console.Out.Flush();
    await host.RunUntilCancelledAsync(cancellation.Token);
}

static LocalJournalBridgeHandler CreateBridge(
    string databasePath,
    string baseUrl,
    ScriptedCentralPmsClient cashClient,
    ScriptedCentralPmsFiscalClient fiscalClient,
    bool fiscalEnabled)
{
    var options = new LocalOperationsDatabaseOptions(
        databasePath,
        CentralPmsBaseUrl: baseUrl,
        EnableCentralPmsCashSubmission: true,
        EnableCentralPmsFiscalIssuance: fiscalEnabled);
    var journal = new CashJournalService(options);
    return new LocalJournalBridgeHandler(
        journal,
        enabled: true,
        centralPmsCashSubmissionEnabled: true,
        centralPmsFiscalIssuanceEnabled: fiscalEnabled,
        centralPmsBaseUrl: baseUrl,
        submissionService: new TerminalCashPaymentSubmissionService(cashClient, options),
        fiscalService: new TerminalCashFiscalSubmissionService(fiscalClient, options));
}

static async Task<Guid> CreateConfirmedTenderAsync(string databasePath, string baseUrl, string suffix)
{
    var cashClient = new ScriptedCentralPmsClient();
    var handler = CreateBridge(databasePath, baseUrl, cashClient, new ScriptedCentralPmsFiscalClient(), fiscalEnabled: true);
    var tenderId = await CreateLocalCashReceivedAsync(handler, suffix);
    cashClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(PaymentSuccess(tenderId), 201));
    await SubmitPaymentAsync(handler, tenderId);
    return tenderId;
}

static async Task<Guid> CreateLocalCashReceivedAsync(LocalJournalBridgeHandler handler, string suffix)
{
    var session = await SendAsync(handler, LocalJournalBridgeCommand.CreateOrGetDevelopmentSession, $"proof-session-{suffix}", new
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
        localIdempotencyIdentity = $"proof-idempotency-{Guid.NewGuid():N}"
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

static async Task<JsonElement> SubmitPaymentAsync(LocalJournalBridgeHandler handler, Guid tenderId)
{
    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId
    });
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Cash-payment submit/check command failed.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonElement> GetFiscalStatusAsync(LocalJournalBridgeHandler handler, Guid tenderId)
{
    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId
    });
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Fiscal status command failed.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonElement> SubmitFiscalAsync(LocalJournalBridgeHandler handler, Guid tenderId)
{
    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId
    });
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Fiscal submit/check command failed.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
{
    var request = JsonSerializer.Serialize(
        new
        {
            source = LocalJournalBridgeCommand.Source,
            command,
            correlationId,
            payload
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    var response = await handler.HandleWebMessageAsync(request)
        ?? throw new InvalidOperationException("Bridge did not return a response.");
    return JsonDocument.Parse(response);
}

static async Task<TerminalCashFiscalOutboxCommand> ReadFiscalCommandAsync(string databasePath, Guid tenderId)
{
    var options = new LocalOperationsDatabaseOptions(databasePath);
    await using var dbContext = new CashJournalService(options).CreateDbContext();
    return await dbContext.TerminalCashFiscalOutboxCommands
        .AsNoTracking()
        .SingleAsync(command => command.TerminalCashTenderId == tenderId);
}

static async Task<int> CountFiscalCommandsAsync(string databasePath, Guid tenderId)
{
    var options = new LocalOperationsDatabaseOptions(databasePath);
    await using var dbContext = new CashJournalService(options).CreateDbContext();
    return await dbContext.TerminalCashFiscalOutboxCommands.CountAsync(command => command.TerminalCashTenderId == tenderId);
}

static TerminalCashPaymentResponse PaymentSuccess(Guid terminalCashTenderId) =>
    new(
        terminalCashTenderId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "CONFIRMED",
        "CREATED",
        "scope",
        "terminal-cash-payment:sha256:v1",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Guid.NewGuid(),
        "NOT_STARTED_IN_THIS_SLICE");

static TerminalCashFiscalIssuanceResponse Recorded(
    TerminalCashFiscalOutboxCommand command,
    string classification = "NEWLY_CREATED") =>
    new(
        command.TerminalCashTenderId,
        command.CanonicalPaymentAttemptId,
        command.CanonicalPaymentConfirmationId,
        Guid.Parse("55555555-5555-4555-8555-555555555555"),
        "FISCAL_ISSUANCE_RECORDED",
        classification,
        Guid.Parse("66666666-6666-4666-8666-666666666666"),
        "SI-000001",
        DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
        "pos-server-semantic-hash:sha256:v1",
        DateTimeOffset.Parse("2026-07-15T00:04:00Z"),
        DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
        Guid.Parse(command.FiscalCorrelationId),
        null,
        null,
        true,
        false,
        false);

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed record ProofArguments(
    bool Interactive,
    FiscalUiProofScenario Scenario,
    int Port,
    string? DatabasePath)
{
    public static ProofArguments Parse(string[] args)
    {
        var interactive = false;
        var scenario = FiscalUiProofScenario.Recorded;
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
                if (!InteractiveCentralPmsFiscalProofHost.TryParseScenario(args[++index], out scenario))
                {
                    throw new ArgumentException($"Unsupported scenario '{args[index]}'. Supported scenarios: {string.Join(", ", Enum.GetNames<FiscalUiProofScenario>())}.");
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

        return new ProofArguments(interactive, scenario, port, databasePath);
    }
}

internal sealed class ScriptedCentralPmsClient : ICentralPmsTerminalCashPaymentClient
{
    private readonly Queue<object> _submitResults = new();

    public void EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse> result) =>
        _submitResults.Enqueue(result);

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(
        Uri baseUri,
        TerminalCashPaymentRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>)_submitResults.Dequeue());

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Proof cash-payment setup does not require readback.");
}

internal sealed class ScriptedCentralPmsFiscalClient : ICentralPmsTerminalCashFiscalClient
{
    private readonly Queue<object> _submitResults = new();
    private readonly Queue<object> _readbackResults = new();
    private readonly Dictionary<Guid, List<TerminalCashFiscalOperationType>> _operations = [];

    public IReadOnlyList<TerminalCashFiscalOperationType> OperationsFor(Guid terminalCashTenderId) =>
        _operations.TryGetValue(terminalCashTenderId, out var operations) ? operations : [];

    public void EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse> result) =>
        _submitResults.Enqueue(result);

    public void EnqueueReadback(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse> result) =>
        _readbackResults.Enqueue(result);

    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> SubmitAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        TerminalCashFiscalIssuanceRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Track(terminalCashTenderId, TerminalCashFiscalOperationType.Submit);
        return Task.FromResult((CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>)_submitResults.Dequeue());
    }

    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Track(terminalCashTenderId, TerminalCashFiscalOperationType.Readback);
        return Task.FromResult((CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>)_readbackResults.Dequeue());
    }

    private void Track(Guid terminalCashTenderId, TerminalCashFiscalOperationType operation)
    {
        if (!_operations.TryGetValue(terminalCashTenderId, out var operations))
        {
            operations = [];
            _operations[terminalCashTenderId] = operations;
        }

        operations.Add(operation);
    }
}
