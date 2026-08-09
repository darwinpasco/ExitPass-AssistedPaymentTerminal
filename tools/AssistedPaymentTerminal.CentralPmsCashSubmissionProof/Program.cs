using System.Text.Json;
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
    CentralPmsBaseUrl: "https://central-pms.example.invalid",
    EnableCentralPmsCashSubmission: true);

var service = new CashJournalService(options);
var command = await CreateOutboxCommandAsync(service, options, "CREATED");
var originalIdempotencyKey = command.IdempotencyKey;
var originalCorrelationId = command.OriginalCorrelationId;
var originalPayload = command.RequestPayloadJson;
var originalPayloadHash = command.RequestPayloadHash;

Console.WriteLine($"CASH_RECEIVED and outbox committed atomically: {command.Id}");
Console.WriteLine($"stable idempotency key: {originalIdempotencyKey}");
Console.WriteLine($"stable correlation ID: {originalCorrelationId}");

var uncertainClient = new ScriptedCentralPmsClient();
uncertainClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());
var uncertain = await new TerminalCashPaymentSubmissionService(uncertainClient, options)
    .SubmitOrReadbackAsync(command.Id);

Require(uncertain.Status == TerminalCashPaymentCommandStatus.ReadbackRequired, "Timeout did not persist an uncertain readback-required state.");
Require(uncertainClient.Operations.SequenceEqual([TerminalCashPaymentOutboxOperationType.Submit]), "First attempt did not submit exactly once.");
Require(uncertainClient.SubmittedIdempotencyKeys.Single() == originalIdempotencyKey, "Submitted idempotency key was not persisted.");
Require(uncertainClient.SubmittedCorrelationIds.Single() == originalCorrelationId, "Submitted correlation ID was not persisted.");
Console.WriteLine("uncertain first submission persisted.");

service = new CashJournalService(options);
var reopened = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(command.TerminalCashTenderId)
    ?? throw new InvalidOperationException("Outbox command was not found after database reopen.");
Require(reopened.IdempotencyKey == originalIdempotencyKey, "Idempotency key changed after reopen.");
Require(reopened.OriginalCorrelationId == originalCorrelationId, "Correlation ID changed after reopen.");
Require(reopened.RequestPayloadJson == originalPayload, "Immutable request payload changed after reopen.");
Require(reopened.RequestPayloadHash == originalPayloadHash, "Payload hash changed after reopen.");
Console.WriteLine("database reopened with stable command identities and payload.");

var readbackClient = new ScriptedCentralPmsClient();
readbackClient.EnqueueReadback(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.Confirmed(Readback(reopened), 200));
var confirmed = await new TerminalCashPaymentSubmissionService(readbackClient, options)
    .SubmitOrReadbackAsync(reopened.Id);

Require(confirmed.Status == TerminalCashPaymentCommandStatus.Confirmed, "Readback confirmation did not close the command.");
Require(readbackClient.Operations.SequenceEqual([TerminalCashPaymentOutboxOperationType.Readback]), "Readback confirmation should not perform another POST.");
Console.WriteLine("readback occurred before retry and closed the command without duplicate POST.");

var attempts = await service.GetTerminalCashPaymentAttemptsAsync(command.Id);
Require(attempts.Select(attempt => attempt.AttemptSequence).SequenceEqual([1, 2]), "Attempt history was not append-only.");
Console.WriteLine($"append-only attempt history count: {attempts.Count}");

var conflictCommand = await CreateOutboxCommandAsync(service, options, "CONFLICT");
var conflictClient = new ScriptedCentralPmsClient();
conflictClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Conflict(409, "DUPLICATE_CASH_TENDER"));
var conflict = await new TerminalCashPaymentSubmissionService(conflictClient, options)
    .SubmitOrReadbackAsync(conflictCommand.Id);
Require(conflict.Status == TerminalCashPaymentCommandStatus.Conflict, "Semantic conflict was not terminal.");
Console.WriteLine("semantic conflict persisted as CONFLICT.");

var rejectionCommand = await CreateOutboxCommandAsync(service, options, "REJECTED");
var rejectionClient = new ScriptedCentralPmsClient();
rejectionClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Rejected(400, "INVALID_CASH_AMOUNTS"));
var rejected = await new TerminalCashPaymentSubmissionService(rejectionClient, options)
    .SubmitOrReadbackAsync(rejectionCommand.Id);
Require(rejected.Status == TerminalCashPaymentCommandStatus.Rejected, "Validation rejection was not terminal.");
Console.WriteLine("deterministic rejection persisted as REJECTED.");

Console.WriteLine("no POS Server, fiscal, exit, gate, or provider behavior was executed.");
Console.WriteLine("Central PMS cash submission outbox proof completed successfully.");

static async Task<TerminalCashPaymentOutboxCommand> CreateOutboxCommandAsync(
    CashJournalService service,
    LocalOperationsDatabaseOptions options,
    string suffix)
{
    await RequireSuccess(service.OpenCashierShiftAsync(new OpenCashierShiftRequest(
        CashierShiftId: $"proof-shift-{suffix}",
        CashierId: $"proof-cashier-{suffix}",
        AuthenticatedCashierSessionReference: $"proof-auth-session-{suffix}",
        TerminalId: "proof-terminal",
        SiteId: "11111111-1111-4111-8111-111111111111",
        SiteGroupId: "22222222-2222-4222-8222-222222222222",
        PosServerId: "proof-pos-server")));
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

    var received = await RequireSuccess(service.CommitCashReceivedAsync(new CommitCashReceivedRequest(
        LocalCashTenderId: tender.Id,
        CashierAttested: true,
        Denominations:
        [
            new CashDenominationLine("PHP-100", 100m, 1),
            new CashDenominationLine("PHP-50", 50m, 1)
        ],
        CentralPmsTarget: options.CentralPmsBaseUrl)));

    Require(received.CurrentLocalState == CashTenderState.CashReceived, "CASH_RECEIVED was not committed.");

    return await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id)
        ?? throw new InvalidOperationException("CASH_RECEIVED did not create an outbox command.");
}

static TerminalCashPaymentReadbackResponse Readback(TerminalCashPaymentOutboxCommand command)
{
    var payload = JsonSerializer.Deserialize<TerminalCashPaymentRequest>(
        command.RequestPayloadJson,
        TerminalCashPaymentPayloadFactory.JsonOptions)!;

    return new TerminalCashPaymentReadbackResponse(
        payload.TerminalCashTenderId,
        Guid.NewGuid(),
        payload.CashCustodySessionId,
        payload.ParkingSessionId,
        payload.TariffSnapshotId,
        payload.TerminalId,
        payload.SiteId,
        payload.SiteGroupId,
        payload.PosServerId,
        payload.CashierId,
        payload.CashierShiftId,
        payload.Currency,
        payload.AmountDueMinorUnits,
        payload.AmountTenderedMinorUnits,
        payload.ChangeDueMinorUnits,
        "CONFIRMED",
        Guid.NewGuid(),
        "CREATED",
        "scope",
        "terminal-cash-payment:sha256:v1",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Guid.Parse(command.OriginalCorrelationId),
        "NOT_STARTED_IN_THIS_SLICE");
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

internal sealed class ScriptedCentralPmsClient : ICentralPmsTerminalCashPaymentClient
{
    private readonly Queue<object> _submitResults = new();
    private readonly Queue<object> _readbackResults = new();

    public List<TerminalCashPaymentOutboxOperationType> Operations { get; } = [];

    public List<string> SubmittedIdempotencyKeys { get; } = [];

    public List<string> SubmittedCorrelationIds { get; } = [];

    public void EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse> result) =>
        _submitResults.Enqueue(result);

    public void EnqueueReadback(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse> result) =>
        _readbackResults.Enqueue(result);

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(
        Uri baseUri,
        TerminalCashPaymentRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(TerminalCashPaymentOutboxOperationType.Submit);
        SubmittedIdempotencyKeys.Add(idempotencyKey);
        SubmittedCorrelationIds.Add(correlationId);
        return Task.FromResult((CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>)_submitResults.Dequeue());
    }

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(TerminalCashPaymentOutboxOperationType.Readback);
        return Task.FromResult((CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>)_readbackResults.Dequeue());
    }
}
