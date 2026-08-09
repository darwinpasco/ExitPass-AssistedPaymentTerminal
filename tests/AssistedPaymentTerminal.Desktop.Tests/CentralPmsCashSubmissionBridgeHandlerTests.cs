using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashSubmissionBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string BaseUrl = "http://127.0.0.1:18080";

    [Fact]
    public async Task UnsupportedSubmissionCommandIsRejected()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsClient());

        using var response = await SendAsync(handler, "centralPmsCashSubmission.delete", "corr-unsupported", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported_command", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetStatusReturnsPersistedOutboxStatus()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus, "corr-status", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("corr-status", response.RootElement.GetProperty("correlationId").GetString());
        var payload = response.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("enabled").GetBoolean());
        Assert.True(payload.GetProperty("configurationValid").GetBoolean());
        Assert.Equal("Pending", payload.GetProperty("command").GetProperty("status").GetString());
        Assert.Equal(command.Id, payload.GetProperty("command").GetProperty("localCommandId").GetGuid());
    }

    [Fact]
    public async Task SubmitOrReadbackCallsExistingSubmissionServiceAndMapsConfirmedIds()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        var attemptId = Guid.NewGuid();
        var confirmationId = Guid.NewGuid();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(
            Success(command, attemptId, confirmationId, "CREATED"),
            201));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-submit", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.Equal([TerminalCashPaymentOutboxOperationType.Submit], client.Operations);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("corr-submit", response.RootElement.GetProperty("correlationId").GetString());
        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Confirmed", mapped.GetProperty("status").GetString());
        Assert.Equal(attemptId, mapped.GetProperty("canonicalPaymentAttemptId").GetGuid());
        Assert.Equal(confirmationId, mapped.GetProperty("canonicalPaymentConfirmationId").GetGuid());
    }

    [Fact]
    public async Task ConflictMapsSafely()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Conflict(409, "DUPLICATE_CASH_TENDER"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-conflict", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Conflict", mapped.GetProperty("status").GetString());
        Assert.Equal("DUPLICATE_CASH_TENDER", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RejectedMapsSafely()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Rejected(400, "INVALID_CASH_AMOUNTS"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-rejected", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Rejected", mapped.GetProperty("status").GetString());
        Assert.Equal("INVALID_CASH_AMOUNTS", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RetryPendingMapsSafely()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Unavailable(503, "SERVICE_UNAVAILABLE"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-retry", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("RetryPending", mapped.GetProperty("status").GetString());
        Assert.Equal("SERVICE_UNAVAILABLE", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task MalformedSubmissionRequestFailsSafely()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-malformed", new
        {
            localCashTenderId = "not-a-guid"
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("malformed_payload", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetStatusDoesNotCreateAnotherOutboxCommand()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus, "corr-status", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, await database.CountOutboxCommandsAsync(command.TerminalCashTenderId));
    }

    [Fact]
    public async Task DisabledSubmissionCommandFailsWithoutNetworkAttempt()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        var handler = database.CreateHandler(client, submissionEnabled: false);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-disabled", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("feature_disabled", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task InvalidConfigurationFailsWithoutNetworkAttempt()
    {
        using var database = SubmissionBridgeTestDatabase.Create();
        var command = await database.CreateReceivedTenderWithOutboxAsync();
        var client = new ScriptedCentralPmsClient();
        var handler = database.CreateHandler(client, centralPmsBaseUrl: "https://central-pms.example.invalid");

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback, "corr-invalid-config", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("central_pms_configuration_invalid", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(client.Operations);
    }

    private static TerminalCashPaymentResponse Success(
        TerminalCashPaymentOutboxCommand command,
        Guid attemptId,
        Guid confirmationId,
        string classification) =>
        new(
            command.TerminalCashTenderId,
            attemptId,
            confirmationId,
            "CONFIRMED",
            classification,
            "scope",
            "terminal-cash-payment:sha256:v1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Guid.Parse(command.OriginalCorrelationId),
            "NOT_STARTED_IN_THIS_SLICE");

    private static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
    {
        var request = JsonSerializer.Serialize(
            new
            {
                source = LocalJournalBridgeCommand.Source,
                command,
                correlationId,
                payload
            },
            JsonOptions);

        var response = await handler.HandleWebMessageAsync(request);
        Assert.NotNull(response);
        return JsonDocument.Parse(response!);
    }
}

internal sealed class SubmissionBridgeTestDatabase : IDisposable
{
    private SubmissionBridgeTestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "central-pms-bridge-test.db");
    }

    private string DirectoryPath { get; }

    private string DatabasePath { get; }

    private LocalOperationsDatabaseOptions Options =>
        new(DatabasePath, CentralPmsBaseUrl: CentralPmsCashSubmissionBridgeHandlerTests.BaseUrl, EnableCentralPmsCashSubmission: true);

    public static SubmissionBridgeTestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.Desktop.CentralPms.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new SubmissionBridgeTestDatabase(directoryPath);
    }

    public LocalJournalBridgeHandler CreateHandler(
        ScriptedCentralPmsClient client,
        bool submissionEnabled = true,
        string centralPmsBaseUrl = CentralPmsCashSubmissionBridgeHandlerTests.BaseUrl)
    {
        var options = new LocalOperationsDatabaseOptions(
            DatabasePath,
            CentralPmsBaseUrl: centralPmsBaseUrl,
            EnableCentralPmsCashSubmission: submissionEnabled);
        var journal = new CashJournalService(options);
        return new LocalJournalBridgeHandler(
            journal,
            enabled: true,
            centralPmsCashSubmissionEnabled: submissionEnabled,
            centralPmsBaseUrl: centralPmsBaseUrl,
            submissionService: new TerminalCashPaymentSubmissionService(client, options));
    }

    public async Task<TerminalCashPaymentOutboxCommand> CreateReceivedTenderWithOutboxAsync()
    {
        var service = new CashJournalService(Options);
        var shift = await service.OpenCashierShiftAsync(new OpenCashierShiftRequest(
            CashierShiftId: "shift-bridge",
            CashierId: "cashier-bridge",
            AuthenticatedCashierSessionReference: "auth-bridge",
            TerminalId: "terminal-bridge",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-bridge"));
        Assert.True(shift.IsSuccess);
        var session = await service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
            CashierId: "cashier-bridge",
            AuthenticatedCashierSessionReference: "auth-bridge",
            CashierShiftId: "shift-bridge",
            TerminalId: "terminal-bridge",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-bridge",
            OpeningCashAmount: 500m));
        Assert.True(session.IsSuccess);

        var tender = await service.StartCashTenderAsync(new StartCashTenderRequest(
            CashCustodySessionId: session.Value!.Id,
            ParkingSessionId: Guid.NewGuid().ToString("D"),
            TariffSnapshotId: Guid.NewGuid().ToString("D"),
            Currency: "PHP",
            AmountDue: 125m,
            AmountTendered: 150m,
            CorrelationId: Guid.NewGuid().ToString("D"),
            LocalIdempotencyIdentity: $"idem-{Guid.NewGuid():N}"));
        Assert.True(tender.IsSuccess);

        var received = await service.CommitCashReceivedAsync(new CommitCashReceivedRequest(
            tender.Value!.Id,
            CashierAttested: true,
            Denominations: [],
            CentralPmsTarget: CentralPmsCashSubmissionBridgeHandlerTests.BaseUrl));
        Assert.True(received.IsSuccess);

        return await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Value!.Id)
            ?? throw new InvalidOperationException("Outbox command was not created.");
    }

    public async Task<int> CountOutboxCommandsAsync(Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(Options).CreateDbContext();
        return await dbContext.TerminalCashPaymentOutboxCommands
            .CountAsync(command => command.TerminalCashTenderId == terminalCashTenderId);
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

internal sealed class ScriptedCentralPmsClient : ICentralPmsTerminalCashPaymentClient
{
    private readonly Queue<object> _submitResults = new();
    private readonly Queue<object> _readbackResults = new();

    public List<TerminalCashPaymentOutboxOperationType> Operations { get; } = [];

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
