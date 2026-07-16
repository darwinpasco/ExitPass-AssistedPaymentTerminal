using System.Net;
using System.Net.Http.Json;
using AssistedPaymentTerminal.LocalOperations;

var databasePath = GetDatabasePath(args);
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fullDatabasePath = Path.GetFullPath(databasePath);
if (fullDatabasePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Proof database path must be outside the Git repository.");
}

var options = new LocalOperationsDatabaseOptions(
    fullDatabasePath,
    CentralPmsBaseUrl: "https://central-pms-proof.local",
    EnableCentralPmsCashSubmission: true,
    EnableCentralPmsFiscalIssuance: true);

var paymentCommand = await CreateConfirmedPaymentAsync(options, "RECORDED");
var fiscalService = new TerminalCashFiscalSubmissionService(
    new CentralPmsTerminalCashFiscalClient(new HttpClient(new ProofFiscalHttpHandler())),
    options);
var fiscalCommand = await fiscalService.GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId)
    ?? throw new InvalidOperationException("Confirmed canonical payment did not create a fiscal command.");

var originalFiscalIdempotencyKey = fiscalCommand.FiscalIdempotencyKey;
var originalFiscalCorrelationId = fiscalCommand.FiscalCorrelationId;
var originalRequest = fiscalCommand.RequestRepresentationJson;
var originalHash = fiscalCommand.RequestHash;

Console.WriteLine($"confirmed canonical cash payment exists: {paymentCommand.TerminalCashTenderId}");
Console.WriteLine($"one fiscal command exists: {fiscalCommand.Id}");
Console.WriteLine($"stable fiscal idempotency key: {originalFiscalIdempotencyKey}");
Console.WriteLine($"stable fiscal correlation ID: {originalFiscalCorrelationId}");

var uncertainHandler = new ProofFiscalHttpHandler();
uncertainHandler.EnqueueTimeout();
var uncertain = await new TerminalCashFiscalSubmissionService(
        new CentralPmsTerminalCashFiscalClient(new HttpClient(uncertainHandler)),
        options)
    .SubmitOrReadbackFiscalAsync(fiscalCommand.Id);

Require(uncertain.Status == TerminalCashFiscalCommandStatus.ReadbackRequired, "Uncertain result was not persisted as readback required.");
Require(uncertainHandler.Operations.SequenceEqual(["POST"]), "First fiscal execution did not submit exactly once.");
Require(uncertainHandler.SubmittedIdempotencyKeys.Single() == originalFiscalIdempotencyKey, "Fiscal idempotency key was not reused.");
Require(uncertainHandler.SubmittedCorrelationIds.Single() == originalFiscalCorrelationId, "Fiscal correlation ID was not reused.");
Console.WriteLine("uncertain first fiscal result recorded.");

fiscalService = new TerminalCashFiscalSubmissionService(
    new CentralPmsTerminalCashFiscalClient(new HttpClient(new ProofFiscalHttpHandler())),
    options);
var reopened = await fiscalService.GetFiscalCommandByTenderAsync(fiscalCommand.TerminalCashTenderId)
    ?? throw new InvalidOperationException("Fiscal command was not found after database reopen.");
Require(reopened.FiscalIdempotencyKey == originalFiscalIdempotencyKey, "Fiscal idempotency key changed after reopen.");
Require(reopened.FiscalCorrelationId == originalFiscalCorrelationId, "Fiscal correlation ID changed after reopen.");
Require(reopened.RequestRepresentationJson == originalRequest, "Fiscal request changed after reopen.");
Require(reopened.RequestHash == originalHash, "Fiscal request hash changed after reopen.");
Require(reopened.CanonicalPaymentAttemptId == paymentCommand.CanonicalPaymentAttemptId, "Canonical payment-attempt ID changed after reopen.");
Require(reopened.CanonicalPaymentConfirmationId == paymentCommand.CanonicalPaymentConfirmationId, "Canonical payment-confirmation ID changed after reopen.");
Console.WriteLine("database reopened with stable fiscal identities and request representation.");

var readbackHandler = new ProofFiscalHttpHandler();
readbackHandler.EnqueueJson(HttpStatusCode.OK, Recorded(reopened));
var recorded = await new TerminalCashFiscalSubmissionService(
        new CentralPmsTerminalCashFiscalClient(new HttpClient(readbackHandler)),
        options)
    .SubmitOrReadbackFiscalAsync(reopened.Id);

Require(recorded.Status == TerminalCashFiscalCommandStatus.Recorded, "Readback completion did not close the fiscal command.");
Require(readbackHandler.Operations.SequenceEqual(["GET"]), "Fiscal readback did not occur before retry or performed duplicate POST.");
Require(recorded.FiscalIssuanceReferenceId == Recorded(reopened).FiscalIssuanceReferenceId, "Fiscal reference was not retained.");
Require(recorded.PosFiscalDocumentId == Recorded(reopened).PosFiscalDocumentId, "POS fiscal document ID was not retained.");
Require(recorded.FiscalDocumentNumber == Recorded(reopened).FiscalDocumentNumber, "Fiscal document number was not retained.");
Console.WriteLine("fiscal readback occurred before retry and closed the command without duplicate POST.");

var replayCommand = await CreateFiscalCommandAsync(options, "REPLAY");
var replayHandler = new ProofFiscalHttpHandler();
replayHandler.EnqueueJson(HttpStatusCode.OK, Recorded(replayCommand, "IDEMPOTENT_REPLAY"));
var replay = await new TerminalCashFiscalSubmissionService(
        new CentralPmsTerminalCashFiscalClient(new HttpClient(replayHandler)),
        options)
    .SubmitOrReadbackFiscalAsync(replayCommand.Id);
Require(replay.Status == TerminalCashFiscalCommandStatus.Recorded, "Idempotent replay did not record the fiscal result.");
Require(replay.ResultClassification == "IDEMPOTENT_REPLAY", "Replay classification was not preserved.");
Console.WriteLine("idempotent replay did not create a duplicate fiscal command or document.");

var conflictCommand = await CreateFiscalCommandAsync(options, "CONFLICT");
var conflictHandler = new ProofFiscalHttpHandler();
conflictHandler.EnqueueJson(HttpStatusCode.Conflict, new CentralPmsSafeError(
    "TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT",
    "Proof safe conflict.",
    Guid.Parse(conflictCommand.FiscalCorrelationId),
    false));
var conflict = await new TerminalCashFiscalSubmissionService(
        new CentralPmsTerminalCashFiscalClient(new HttpClient(conflictHandler)),
        options)
    .SubmitOrReadbackFiscalAsync(conflictCommand.Id);
Require(conflict.Status == TerminalCashFiscalCommandStatus.Conflict, "Conflict was not terminal.");
Console.WriteLine("conflict persisted as CONFLICT.");

var rejectedCommand = await CreateFiscalCommandAsync(options, "REJECTED");
var rejectionHandler = new ProofFiscalHttpHandler();
rejectionHandler.EnqueueJson(HttpStatusCode.BadRequest, new CentralPmsSafeError(
    "CORRELATION_ID_REQUIRED",
    "Proof safe rejection.",
    Guid.Parse(rejectedCommand.FiscalCorrelationId),
    false));
var rejected = await new TerminalCashFiscalSubmissionService(
        new CentralPmsTerminalCashFiscalClient(new HttpClient(rejectionHandler)),
        options)
    .SubmitOrReadbackFiscalAsync(rejectedCommand.Id);
Require(rejected.Status == TerminalCashFiscalCommandStatus.Rejected, "Rejection was not terminal.");
Console.WriteLine("deterministic rejection persisted as REJECTED.");

var attempts = await fiscalService.GetFiscalAttemptsAsync(fiscalCommand.Id);
Require(attempts.Select(attempt => attempt.AttemptSequence).SequenceEqual([1, 2]), "Fiscal attempt history was not append-only.");
Console.WriteLine($"append-only fiscal attempt history count: {attempts.Count}");

Console.WriteLine("no direct POS Server client, receipt, exit, gate, Payment Orchestrator, or cash amount mutation was executed.");
Console.WriteLine("Central PMS cash fiscal outbox proof completed successfully.");

static async Task<TerminalCashPaymentOutboxCommand> CreateConfirmedPaymentAsync(
    LocalOperationsDatabaseOptions options,
    string suffix)
{
    var service = new CashJournalService(options);
    var command = await CreatePaymentCommandAsync(service, options, suffix);
    var cashClient = new ScriptedCentralPmsClient();
    cashClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(PaymentSuccess(command), 201));

    return await new TerminalCashPaymentSubmissionService(cashClient, options).SubmitOrReadbackAsync(command.Id);
}

static async Task<TerminalCashFiscalOutboxCommand> CreateFiscalCommandAsync(
    LocalOperationsDatabaseOptions options,
    string suffix)
{
    var payment = await CreateConfirmedPaymentAsync(options, suffix);
    return await new TerminalCashFiscalSubmissionService(
            new CentralPmsTerminalCashFiscalClient(new HttpClient(new ProofFiscalHttpHandler())),
            options)
        .GetFiscalCommandByTenderAsync(payment.TerminalCashTenderId)
        ?? throw new InvalidOperationException("Fiscal command was not created.");
}

static async Task<TerminalCashPaymentOutboxCommand> CreatePaymentCommandAsync(
    CashJournalService service,
    LocalOperationsDatabaseOptions options,
    string suffix)
{
    var session = await RequireSuccess(service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
        CashierId: $"proof-cashier-{suffix}",
        AuthenticatedCashierSessionReference: $"proof-auth-session-{suffix}",
        CashierShiftId: $"proof-shift-{suffix}",
        TerminalId: "proof-terminal",
        SiteId: "11111111-1111-4111-8111-111111111111",
        SiteGroupId: "22222222-2222-4222-8222-222222222222",
        PosServerId: "proof-pos-server",
        OpeningCashAmount: 500m)));

    var tender = await RequireSuccess(service.StartCashTenderAsync(new StartCashTenderRequest(
        CashCustodySessionId: session.Id,
        ParkingSessionId: Guid.NewGuid().ToString("D"),
        TariffSnapshotId: Guid.NewGuid().ToString("D"),
        Currency: "PHP",
        AmountDue: 120m,
        AmountTendered: 150m,
        CorrelationId: Guid.NewGuid().ToString("D"),
        LocalIdempotencyIdentity: $"proof-idempotency-{Guid.NewGuid():N}")));

    await RequireSuccess(service.CommitCashReceivedAsync(new CommitCashReceivedRequest(
        LocalCashTenderId: tender.Id,
        CashierAttested: true,
        Denominations:
        [
            new CashDenominationLine("PHP-100", 100m, 1),
            new CashDenominationLine("PHP-50", 50m, 1)
        ],
        CentralPmsTarget: options.CentralPmsBaseUrl)));

    return await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id)
        ?? throw new InvalidOperationException("CASH_RECEIVED did not create a cash-payment outbox command.");
}

static TerminalCashPaymentResponse PaymentSuccess(TerminalCashPaymentOutboxCommand command) =>
    new(
        command.TerminalCashTenderId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "CONFIRMED",
        "CREATED",
        "scope",
        "terminal-cash-payment:sha256:v1",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Guid.Parse(command.OriginalCorrelationId),
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

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class ProofFiscalHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<string> Operations { get; } = [];

    public List<string> SubmittedIdempotencyKeys { get; } = [];

    public List<string> SubmittedCorrelationIds { get; } = [];

    public void EnqueueTimeout() =>
        _responses.Enqueue(_ => throw new TaskCanceledException("Proof timeout."));

    public void EnqueueJson<T>(HttpStatusCode statusCode, T payload) =>
        _responses.Enqueue(_ => Json(statusCode, payload));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Operations.Add(request.Method == HttpMethod.Get ? "GET" : "POST");

        if (request.Method == HttpMethod.Post)
        {
            SubmittedIdempotencyKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            SubmittedCorrelationIds.Add(request.Headers.GetValues("X-Correlation-Id").Single());
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode statusCode, T payload) =>
        new(statusCode)
        {
            Content = JsonContent.Create(payload, options: TerminalCashPaymentPayloadFactory.JsonOptions)
        };
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
