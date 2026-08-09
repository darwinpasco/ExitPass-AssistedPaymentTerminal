using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashFiscalBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string BaseUrl = "http://127.0.0.1:18080";

    [Fact]
    public async Task UnsupportedFiscalBridgeCommandIsRejected()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsFiscalClient());

        using var response = await SendAsync(handler, "centralPmsCashFiscal.delete", "corr-unsupported", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported_command", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetStatusReturnsPersistedFiscalCommandWithoutNetworkSubmission()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus, "corr-status", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("corr-status", response.RootElement.GetProperty("correlationId").GetString());
        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal(command.Id, mapped.GetProperty("localFiscalCommandId").GetGuid());
        Assert.Equal("Pending", mapped.GetProperty("status").GetString());
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task SubmitOrReadbackInvokesFiscalServiceAndMapsRecordedIdentifiers()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(command), 200));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-submit", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.Equal([TerminalCashFiscalOperationType.Submit], client.Operations);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("corr-submit", response.RootElement.GetProperty("correlationId").GetString());
        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Recorded", mapped.GetProperty("status").GetString());
        Assert.Equal(Recorded(command).FiscalIssuanceReferenceId, mapped.GetProperty("fiscalIssuanceReferenceId").GetGuid());
        Assert.Equal(Recorded(command).PosFiscalDocumentId, mapped.GetProperty("posFiscalDocumentId").GetGuid());
        Assert.Equal("SI-000001", mapped.GetProperty("fiscalDocumentNumber").GetString());
    }

    [Fact]
    public async Task ReplayMapsSafelyWithoutDuplicateDocumentWording()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(
            Recorded(command, "IDEMPOTENT_REPLAY"),
            200));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-replay", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Recorded", mapped.GetProperty("status").GetString());
        Assert.Equal("IDEMPOTENT_REPLAY", mapped.GetProperty("resultClassification").GetString());
    }

    [Fact]
    public async Task PendingResultMapsSafely()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(
            Recorded(command) with { FiscalIssuanceState = "FISCAL_ISSUANCE_REQUESTED", FiscalDocumentNumber = null, PosFiscalDocumentId = null },
            200));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-pending", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("ReadbackRequired", mapped.GetProperty("status").GetString());
        Assert.Equal("FISCAL_ISSUANCE_REQUESTED", mapped.GetProperty("fiscalIssuanceState").GetString());
    }

    [Fact]
    public async Task ConflictMapsToBlockingSafeResponse()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Conflict(409, "TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-conflict", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Conflict", mapped.GetProperty("status").GetString());
        Assert.Equal("TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RejectionMapsSafeErrorDetails()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Rejected(400, "CORRELATION_ID_REQUIRED"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-rejected", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Rejected", mapped.GetProperty("status").GetString());
        Assert.Equal("CORRELATION_ID_REQUIRED", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RetryOrUncertainStateNeverMapsToRecorded()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-retry", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("ReadbackRequired", mapped.GetProperty("status").GetString());
        Assert.NotEqual("Recorded", mapped.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MalformedFiscalRequestFailsSafely()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsFiscalClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-malformed", new
        {
            localCashTenderId = "not-a-guid"
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("malformed_payload", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoSecondFiscalCommandIsCreated()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsFiscalClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus, "corr-status", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, await database.CountFiscalCommandsAsync(command.TerminalCashTenderId));
    }

    [Fact]
    public async Task MissingCommandForConfirmedPaymentCanBeEnsuredOnce()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        await database.DeleteFiscalCommandAsync(command.TerminalCashTenderId);
        var handler = database.CreateHandler(new ScriptedCentralPmsFiscalClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus, "corr-ensure", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, await database.CountFiscalCommandsAsync(command.TerminalCashTenderId));
    }

    [Fact]
    public async Task MissingCommandForUnconfirmedPaymentIsRejected()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var paymentCommand = await database.CreateUnconfirmedPaymentOutboxAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsFiscalClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus, "corr-unconfirmed", new
        {
            localCashTenderId = paymentCommand.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("fiscal_command_unavailable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DisabledFiscalCommandFailsWithoutNetworkAttempt()
    {
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        var handler = database.CreateHandler(client, fiscalEnabled: false);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-disabled", new
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
        using var database = FiscalBridgeTestDatabase.Create();
        var command = await database.CreateConfirmedPaymentWithFiscalCommandAsync();
        var client = new ScriptedCentralPmsFiscalClient();
        var handler = database.CreateHandler(client, centralPmsBaseUrl: "https://central-pms.example.invalid");

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback, "corr-invalid-config", new
        {
            localCashTenderId = command.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("central_pms_configuration_invalid", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(client.Operations);
    }

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

    private static TerminalCashFiscalIssuanceResponse Recorded(
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
}

internal sealed class FiscalBridgeTestDatabase : IDisposable
{
    private FiscalBridgeTestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "central-pms-fiscal-bridge-test.db");
    }

    private string DirectoryPath { get; }

    private string DatabasePath { get; }

    private LocalOperationsDatabaseOptions Options =>
        new(
            DatabasePath,
            CentralPmsBaseUrl: CentralPmsCashFiscalBridgeHandlerTests.BaseUrl,
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true);

    public static FiscalBridgeTestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.Desktop.CentralPmsFiscal.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new FiscalBridgeTestDatabase(directoryPath);
    }

    public LocalJournalBridgeHandler CreateHandler(
        ScriptedCentralPmsFiscalClient fiscalClient,
        bool fiscalEnabled = true,
        string centralPmsBaseUrl = CentralPmsCashFiscalBridgeHandlerTests.BaseUrl)
    {
        var options = new LocalOperationsDatabaseOptions(
            DatabasePath,
            CentralPmsBaseUrl: centralPmsBaseUrl,
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: fiscalEnabled);
        var journal = new CashJournalService(options);
        return new LocalJournalBridgeHandler(
            journal,
            enabled: true,
            centralPmsCashSubmissionEnabled: true,
            centralPmsFiscalIssuanceEnabled: fiscalEnabled,
            centralPmsBaseUrl: centralPmsBaseUrl,
            submissionService: new TerminalCashPaymentSubmissionService(new ScriptedCentralPmsClient(), options),
            fiscalService: new TerminalCashFiscalSubmissionService(fiscalClient, options));
    }

    public async Task<TerminalCashFiscalOutboxCommand> CreateConfirmedPaymentWithFiscalCommandAsync()
    {
        var paymentCommand = await CreateUnconfirmedPaymentOutboxAsync();
        var cashClient = new ScriptedCentralPmsClient();
        cashClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(
            new TerminalCashPaymentResponse(
                paymentCommand.TerminalCashTenderId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "CONFIRMED",
                "CREATED",
                "scope",
                "terminal-cash-payment:sha256:v1",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Guid.Parse(paymentCommand.OriginalCorrelationId),
                "NOT_STARTED_IN_THIS_SLICE"),
            201));

        var confirmed = await new TerminalCashPaymentSubmissionService(cashClient, Options)
            .SubmitOrReadbackAsync(paymentCommand.Id);
        Assert.Equal(TerminalCashPaymentCommandStatus.Confirmed, confirmed.Status);

        return await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), Options)
            .GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId)
            ?? throw new InvalidOperationException("Fiscal command was not created.");
    }

    public async Task<TerminalCashPaymentOutboxCommand> CreateUnconfirmedPaymentOutboxAsync()
    {
        var service = new CashJournalService(Options);
        var cashierId = $"cashier-{Guid.NewGuid():N}";
        var authenticationReference = $"auth-{Guid.NewGuid():N}";
        var shiftId = $"shift-{Guid.NewGuid():N}";
        var shift = await service.OpenCashierShiftAsync(new OpenCashierShiftRequest(
            shiftId,
            cashierId,
            authenticationReference,
            "terminal-bridge",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-bridge"));
        Assert.True(shift.IsSuccess);
        var session = await service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
            CashierId: cashierId,
            AuthenticatedCashierSessionReference: authenticationReference,
            CashierShiftId: shiftId,
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
            CentralPmsTarget: CentralPmsCashFiscalBridgeHandlerTests.BaseUrl));
        Assert.True(received.IsSuccess);

        return await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Value!.Id)
            ?? throw new InvalidOperationException("Cash-payment outbox command was not created.");
    }

    public async Task DeleteFiscalCommandAsync(Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(Options).CreateDbContext();
        var command = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleAsync(value => value.TerminalCashTenderId == terminalCashTenderId);
        dbContext.TerminalCashFiscalOutboxCommands.Remove(command);
        await dbContext.SaveChangesAsync();
    }

    public async Task<int> CountFiscalCommandsAsync(Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(Options).CreateDbContext();
        return await dbContext.TerminalCashFiscalOutboxCommands
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

internal sealed class ScriptedCentralPmsFiscalClient : ICentralPmsTerminalCashFiscalClient
{
    private readonly Queue<object> _submitResults = new();
    private readonly Queue<object> _readbackResults = new();

    public List<TerminalCashFiscalOperationType> Operations { get; } = [];

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
        Operations.Add(TerminalCashFiscalOperationType.Submit);
        return Task.FromResult((CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>)_submitResults.Dequeue());
    }

    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(TerminalCashFiscalOperationType.Readback);
        return Task.FromResult((CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>)_readbackResults.Dequeue());
    }
}
