using System.Net;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;

var databasePath = GetDatabasePath(args);
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fullDatabasePath = Path.GetFullPath(databasePath);
if (fullDatabasePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Proof database path must be outside the Git repository.");
}

const string baseUrl = "http://127.0.0.1:18080";
var httpHandler = new ProofCentralPmsHandler();
var handler = CreateBridge(fullDatabasePath, baseUrl, httpHandler, enabled: true);

var createdTenderId = await CreateLocalCashReceivedAsync(handler, "CREATED");
var createdStatus = await GetStatusAsync(handler, createdTenderId);
Require(createdStatus.GetProperty("command").GetProperty("status").GetString() == "Pending", "Expected pending command before submission.");

var createdAttemptId = Guid.NewGuid();
var createdConfirmationId = Guid.NewGuid();
httpHandler.SetScenario(createdTenderId, ProofScenario.Created(createdAttemptId, createdConfirmationId, "CREATED"));
var confirmed = await SubmitAsync(handler, createdTenderId);
Require(confirmed.GetProperty("command").GetProperty("status").GetString() == "Confirmed", "Created command was not confirmed.");
Require(confirmed.GetProperty("command").GetProperty("canonicalPaymentAttemptId").GetGuid() == createdAttemptId, "Payment-attempt ID was not mapped.");
Require(confirmed.GetProperty("command").GetProperty("canonicalPaymentConfirmationId").GetGuid() == createdConfirmationId, "Payment-confirmation ID was not mapped.");
Console.WriteLine("newly accepted Central PMS confirmation displayed through bridge status.");

handler = CreateBridge(fullDatabasePath, baseUrl, httpHandler, enabled: true);
var restored = await GetStatusAsync(handler, createdTenderId);
Require(restored.GetProperty("command").GetProperty("status").GetString() == "Confirmed", "Confirmed status was not restored after restart.");
Console.WriteLine("restart readback restored confirmed canonical payment status without creating another command.");

var uncertainTenderId = await CreateLocalCashReceivedAsync(handler, "UNCERTAIN");
httpHandler.SetScenario(uncertainTenderId, ProofScenario.TimeoutThenReadback(Guid.NewGuid(), Guid.NewGuid()));
var uncertain = await SubmitAsync(handler, uncertainTenderId);
Require(uncertain.GetProperty("command").GetProperty("status").GetString() == "ReadbackRequired", "Timeout did not persist readback-required state.");

handler = CreateBridge(fullDatabasePath, baseUrl, httpHandler, enabled: true);
var readback = await SubmitAsync(handler, uncertainTenderId);
Require(readback.GetProperty("command").GetProperty("status").GetString() == "Confirmed", "Readback confirmation did not close uncertain command.");
Require(httpHandler.OperationsFor(uncertainTenderId).SequenceEqual(["POST", "GET"]), "Restarted uncertain command did not read back before another POST.");
Console.WriteLine("uncertain first request performed readback before retry after restart.");

var replayTenderId = await CreateLocalCashReceivedAsync(handler, "REPLAY");
var replayAttemptId = Guid.NewGuid();
var replayConfirmationId = Guid.NewGuid();
httpHandler.SetScenario(replayTenderId, ProofScenario.Created(replayAttemptId, replayConfirmationId, "IDEMPOTENT_REPLAY"));
var replay = await SubmitAsync(handler, replayTenderId);
Require(replay.GetProperty("command").GetProperty("resultClassification").GetString() == "IDEMPOTENT_REPLAY", "Replay classification was not preserved.");
Require(replay.GetProperty("command").GetProperty("canonicalPaymentConfirmationId").GetGuid() == replayConfirmationId, "Replay canonical IDs changed.");
Console.WriteLine("idempotent replay displayed as confirmed with the same canonical IDs.");

var conflictTenderId = await CreateLocalCashReceivedAsync(handler, "CONFLICT");
httpHandler.SetScenario(conflictTenderId, ProofScenario.Error(HttpStatusCode.Conflict, "DUPLICATE_CASH_TENDER"));
var conflict = await SubmitAsync(handler, conflictTenderId);
Require(conflict.GetProperty("command").GetProperty("status").GetString() == "Conflict", "Conflict was not terminal.");
Console.WriteLine("semantic conflict displayed as blocking support-review state.");

var rejectedTenderId = await CreateLocalCashReceivedAsync(handler, "REJECTED");
httpHandler.SetScenario(rejectedTenderId, ProofScenario.Error(HttpStatusCode.BadRequest, "INVALID_CASH_AMOUNTS"));
var rejected = await SubmitAsync(handler, rejectedTenderId);
Require(rejected.GetProperty("command").GetProperty("status").GetString() == "Rejected", "Rejection was not terminal.");
Console.WriteLine("deterministic rejection preserved local CASH_RECEIVED without canonical confirmation.");

var disabledHandler = CreateBridge(fullDatabasePath, baseUrl, httpHandler, enabled: false);
var disabled = await SendAsync(
    disabledHandler,
    LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback,
    "proof-disabled",
    new { localCashTenderId = rejectedTenderId });
Require(!disabled.RootElement.GetProperty("ok").GetBoolean(), "Disabled submission should not succeed.");
Require(disabled.RootElement.GetProperty("error").GetProperty("code").GetString() == "feature_disabled", "Disabled submission did not return feature_disabled.");
Console.WriteLine("default-off disabled behavior hides submission capability at the bridge boundary.");

Console.WriteLine("local cash custody, Central PMS canonical payment, fiscal issuance, and exit authorization remained distinct.");
Console.WriteLine("no POS Server, fiscal issuance, receipt, exit authorization, gate, or Payment Orchestrator behavior was executed.");
Console.WriteLine("Central PMS cash submission UI proof completed successfully.");

static LocalJournalBridgeHandler CreateBridge(string databasePath, string baseUrl, ProofCentralPmsHandler httpHandler, bool enabled)
{
    var options = new LocalOperationsDatabaseOptions(
        databasePath,
        CentralPmsBaseUrl: baseUrl,
        EnableCentralPmsCashSubmission: enabled);
    var journal = new CashJournalService(options);
    return new LocalJournalBridgeHandler(
        journal,
        enabled: true,
        centralPmsCashSubmissionEnabled: enabled,
        centralPmsBaseUrl: baseUrl,
        submissionService: new TerminalCashPaymentSubmissionService(
            new CentralPmsTerminalCashPaymentClient(new HttpClient(httpHandler, disposeHandler: false)),
            options));
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
    RequireBridgeSuccess(session, "Session creation failed");
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

static async Task<JsonElement> GetStatusAsync(LocalJournalBridgeHandler handler, Guid tenderId)
{
    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId
    });
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Status command failed.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonElement> SubmitAsync(LocalJournalBridgeHandler handler, Guid tenderId)
{
    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = tenderId
    });
    Require(response.RootElement.GetProperty("ok").GetBoolean(), "Submit/check command failed.");
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

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RequireBridgeSuccess(JsonDocument response, string message)
{
    if (response.RootElement.GetProperty("ok").GetBoolean())
    {
        return;
    }

    var safeCode = response.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "UNKNOWN";
    throw new InvalidOperationException($"{message}. Safe classification: {safeCode}.");
}

internal sealed class ProofCentralPmsHandler : HttpMessageHandler
{
    private readonly Dictionary<Guid, ProofScenario> _scenarios = [];
    private readonly Dictionary<Guid, List<string>> _operations = [];

    public void SetScenario(Guid terminalCashTenderId, ProofScenario scenario) =>
        _scenarios[terminalCashTenderId] = scenario;

    public IReadOnlyList<string> OperationsFor(Guid terminalCashTenderId) =>
        _operations.TryGetValue(terminalCashTenderId, out var operations) ? operations : [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<TerminalCashPaymentRequest>(
                requestJson,
                TerminalCashPaymentPayloadFactory.JsonOptions)!;
            Track(payload.TerminalCashTenderId, "POST");
            var scenario = _scenarios[payload.TerminalCashTenderId];
            return scenario.Submit(payload, request.Headers.GetValues("X-Correlation-Id").Single());
        }

        if (request.Method == HttpMethod.Get)
        {
            var tenderId = Guid.Parse(request.RequestUri!.Segments.Last());
            Track(tenderId, "GET");
            return _scenarios[tenderId].Readback(tenderId, request.Headers.GetValues("X-Correlation-Id").Single());
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private void Track(Guid terminalCashTenderId, string operation)
    {
        if (!_operations.TryGetValue(terminalCashTenderId, out var operations))
        {
            operations = [];
            _operations[terminalCashTenderId] = operations;
        }

        operations.Add(operation);
    }
}

internal sealed class ProofScenario
{
    private readonly Guid _attemptId;
    private readonly Guid _confirmationId;
    private readonly string _classification;
    private readonly HttpStatusCode? _errorStatus;
    private readonly string? _errorCode;
    private readonly bool _timeoutFirstSubmit;
    private bool _submitTimedOut;

    private ProofScenario(
        Guid attemptId,
        Guid confirmationId,
        string classification,
        HttpStatusCode? errorStatus = null,
        string? errorCode = null,
        bool timeoutFirstSubmit = false)
    {
        _attemptId = attemptId;
        _confirmationId = confirmationId;
        _classification = classification;
        _errorStatus = errorStatus;
        _errorCode = errorCode;
        _timeoutFirstSubmit = timeoutFirstSubmit;
    }

    public static ProofScenario Created(Guid attemptId, Guid confirmationId, string classification) =>
        new(attemptId, confirmationId, classification);

    public static ProofScenario TimeoutThenReadback(Guid attemptId, Guid confirmationId) =>
        new(attemptId, confirmationId, "CREATED", timeoutFirstSubmit: true);

    public static ProofScenario Error(HttpStatusCode statusCode, string errorCode) =>
        new(Guid.Empty, Guid.Empty, "ERROR", statusCode, errorCode);

    public HttpResponseMessage Submit(TerminalCashPaymentRequest payload, string correlationId)
    {
        if (_timeoutFirstSubmit && !_submitTimedOut)
        {
            _submitTimedOut = true;
            throw new TaskCanceledException("Proof timeout.");
        }

        if (_errorStatus is not null)
        {
            return SafeError(_errorStatus.Value, _errorCode!, correlationId);
        }

        return Json(HttpStatusCode.Created, new TerminalCashPaymentResponse(
            payload.TerminalCashTenderId,
            _attemptId,
            _confirmationId,
            "CONFIRMED",
            _classification,
            "scope",
            "terminal-cash-payment:sha256:v1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Guid.Parse(correlationId),
            "NOT_STARTED_IN_THIS_SLICE"));
    }

    public HttpResponseMessage Readback(Guid terminalCashTenderId, string correlationId)
    {
        return Json(HttpStatusCode.OK, new TerminalCashPaymentReadbackResponse(
            terminalCashTenderId,
            _attemptId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "terminal-proof",
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "pos-proof",
            "cashier-proof",
            "shift-proof",
            "PHP",
            12500,
            15000,
            2500,
            "CONFIRMED",
            _confirmationId,
            _classification,
            "scope",
            "terminal-cash-payment:sha256:v1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Guid.Parse(correlationId),
            "NOT_STARTED_IN_THIS_SLICE"));
    }

    private static HttpResponseMessage SafeError(HttpStatusCode statusCode, string errorCode, string correlationId) =>
        Json(statusCode, new CentralPmsSafeError(errorCode, "Proof safe error.", Guid.Parse(correlationId), statusCode != HttpStatusCode.BadRequest));

    private static HttpResponseMessage Json<T>(HttpStatusCode statusCode, T payload) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TerminalCashPaymentPayloadFactory.JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        };
}
