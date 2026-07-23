using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashReceiptBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string BaseUrl = "http://127.0.0.1:18080";

    [Fact]
    public async Task UnsupportedReceiptBridgeCommandIsRejected()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient());

        using var response = await SendAsync(handler, "centralPmsCashReceipt.delete", "corr-unsupported", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported_command", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetStatusReturnsPersistedRetrievalRecordWithoutNetworkRetrieval()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus, "corr-status", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("corr-status", response.RootElement.GetProperty("correlationId").GetString());
        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal(receipt.Id, mapped.GetProperty("localReceiptRetrievalId").GetGuid());
        Assert.Equal("Pending", mapped.GetProperty("status").GetString());
        Assert.False(mapped.TryGetProperty("authoritativePresentationJson", out _));
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task RetrieveOrCheckInvokesReceiptServiceAndMapsAvailableMetadata()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(receipt),
            200,
            Guid.Parse(receipt.RetrievalCorrelationId)));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-retrieve", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        Assert.Equal([receipt.TerminalCashTenderId], client.Operations);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Available", mapped.GetProperty("status").GetString());
        Assert.Equal("SI-000001", mapped.GetProperty("fiscalDocumentNumber").GetString());
        Assert.Equal("recorded", mapped.GetProperty("fiscalDocumentStatus").GetString());
        Assert.Equal("digital-sales-invoice-presentation-json-v1", mapped.GetProperty("presentationVersion").GetString());
        Assert.Equal("digital-sales-invoice-json-v1", mapped.GetProperty("templateVersion").GetString());
        Assert.Equal("application/json", mapped.GetProperty("contentType").GetString());
        Assert.Equal("CONFIRMED", mapped.GetProperty("canonicalPaymentStatus").GetString());
        Assert.Equal("sha256:fiscal-semantic", mapped.GetProperty("semanticRequestHash").GetString());
        Assert.Equal("pos-server-semantic-hash:sha256:v1", mapped.GetProperty("semanticRequestHashVersion").GetString());
        Assert.Equal("MATCHED", mapped.GetProperty("semanticRequestHashStatus").GetString());
        Assert.False(mapped.GetProperty("lastRetryable").GetBoolean());
        Assert.Equal(receipt.RetrievalCorrelationId, mapped.GetProperty("lastCentralPmsCorrelationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(mapped.GetProperty("authoritativePayloadHash").GetString()));
        Assert.False(mapped.TryGetProperty("authoritativePresentationJson", out _));
    }

    [Fact]
    public async Task NotReadyResultMapsSafely()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady(409, "RECEIPT_PRESENTATION_NOT_READY"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-not-ready", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("NotReady", mapped.GetProperty("status").GetString());
        Assert.Equal("RECEIPT_PRESENTATION_NOT_READY", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RetryOrUnavailableNeverMapsToAvailable()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable(503, "CENTRAL_PMS_UNAVAILABLE"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-retry", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Unavailable", mapped.GetProperty("status").GetString());
        Assert.NotEqual("Available", mapped.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InconsistentResultMapsToBlockingSafeResponse()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Inconsistent(409, "TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-inconsistent", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Inconsistent", mapped.GetProperty("status").GetString());
        Assert.Equal("TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Fact]
    public async Task RejectionMapsSafeErrorDetails()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Rejected(400, "RECEIPT_PRESENTATION_REJECTED"));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-rejected", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Rejected", mapped.GetProperty("status").GetString());
        Assert.Equal("RECEIPT_PRESENTATION_REJECTED", mapped.GetProperty("lastSafeErrorCode").GetString());
    }

    [Theory]
    [InlineData("POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED", "Unsupported")]
    [InlineData("POS_SERVER_RECEIPT_PRESENTATION_MALFORMED", "Malformed")]
    public async Task TerminalPresentationFailuresMapToDistinctSafeStates(string safeCode, string expectedStatus)
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(safeCode.EndsWith("UNSUPPORTED", StringComparison.Ordinal)
            ? CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unsupported(409, safeCode, retryable: false)
            : CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Malformed(409, safeCode, retryable: false));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-terminal-failure", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal(expectedStatus, mapped.GetProperty("status").GetString());
        Assert.Equal(safeCode, mapped.GetProperty("lastSafeErrorCode").GetString());
        Assert.False(mapped.GetProperty("lastRetryable").GetBoolean());
        Assert.False(mapped.TryGetProperty("authoritativePresentationJson", out _));
    }

    [Fact]
    public async Task VoidedPostureMapsSafely()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt, voided: true), 200));
        var handler = database.CreateHandler(client);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-voided", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        var mapped = response.RootElement.GetProperty("payload").GetProperty("command");
        Assert.Equal("Voided", mapped.GetProperty("status").GetString());
        Assert.Equal("voided", mapped.GetProperty("voidStatus").GetString());
        Assert.Equal("operator_void", mapped.GetProperty("voidReasonCode").GetString());
    }

    [Fact]
    public async Task MalformedReceiptRequestFailsSafely()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-malformed", new
        {
            localCashTenderId = "not-a-guid"
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("malformed_payload", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoSecondReceiptRetrievalRecordIsCreated()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus, "corr-status", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, await database.CountReceiptCommandsAsync(receipt.TerminalCashTenderId));
    }

    [Fact]
    public async Task MissingRetrievalRecordForRecordedFiscalCanBeEnsuredOnce()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        await database.DeleteReceiptCommandAsync(receipt.TerminalCashTenderId);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus, "corr-ensure", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, await database.CountReceiptCommandsAsync(receipt.TerminalCashTenderId));
    }

    [Fact]
    public async Task MissingRetrievalRecordForNonRecordedFiscalIsRejected()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var fiscal = await database.CreateConfirmedPaymentWithPendingFiscalAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus, "corr-unrecorded", new
        {
            localCashTenderId = fiscal.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_retrieval_unavailable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DisabledReceiptCommandFailsWithoutNetworkAttempt()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        var handler = database.CreateHandler(client, receiptEnabled: false);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-disabled", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
        });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("feature_disabled", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task InvalidConfigurationFailsWithoutNetworkAttempt()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        var handler = database.CreateHandler(client, centralPmsBaseUrl: "https://central-pms.example.invalid");

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck, "corr-invalid-config", new
        {
            localCashTenderId = receipt.TerminalCashTenderId
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

    private static TerminalCashReceiptPresentationResponse Available(
        TerminalCashReceiptRetrievalCommand command,
        bool voided = false)
    {
        using var document = JsonDocument.Parse(
            voided
                ? """{"presentation":{"lines":[{"description":"Parking fee - cash"}],"tenders":[{"tenderType":"CASH"}]},"voidStatus":"voided"}"""
                : """{"presentation":{"lines":[{"description":"Parking fee - cash"}],"tenders":[{"tenderType":"CASH"}]}}""");

        return new TerminalCashReceiptPresentationResponse(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            "CONFIRMED",
            command.FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            command.PosFiscalDocumentId,
            "SI-000001",
            voided ? "voided" : "recorded",
            voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
            "digital-sales-invoice-presentation-json-v1",
            "digital-sales-invoice-json-v1",
            "sha256:fiscal-semantic",
            "pos-server-semantic-hash:sha256:v1",
            "MATCHED",
            "application/json",
            document.RootElement.Clone(),
            voided ? "voided" : null,
            voided ? "operator_void" : null,
            voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            Guid.Parse(command.RetrievalCorrelationId));
    }
}

internal sealed class ReceiptBridgeTestDatabase : IDisposable
{
    private ReceiptBridgeTestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "central-pms-receipt-bridge-test.db");
    }

    private string DirectoryPath { get; }

    private string DatabasePath { get; }

    internal LocalOperationsDatabaseOptions OptionsForPreviewTests =>
        new(
            DatabasePath,
            CentralPmsBaseUrl: CentralPmsCashReceiptBridgeHandlerTests.BaseUrl,
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true,
            EnableCentralPmsReceiptRetrieval: true);

    public static ReceiptBridgeTestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.Desktop.CentralPmsReceipt.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new ReceiptBridgeTestDatabase(directoryPath);
    }

    public LocalJournalBridgeHandler CreateHandler(
        ScriptedCentralPmsReceiptClient receiptClient,
        bool receiptEnabled = true,
        string centralPmsBaseUrl = CentralPmsCashReceiptBridgeHandlerTests.BaseUrl,
        bool receiptPreviewEnabled = false,
        string? receiptPaperWidthMm = null)
    {
        var options = new LocalOperationsDatabaseOptions(
            DatabasePath,
            CentralPmsBaseUrl: centralPmsBaseUrl,
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
            receiptPreviewEnabled: receiptPreviewEnabled,
            receiptPaperWidthMm: receiptPaperWidthMm,
            centralPmsBaseUrl: centralPmsBaseUrl,
            submissionService: new TerminalCashPaymentSubmissionService(new ScriptedCentralPmsClient(), options),
            fiscalService: new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), options),
            receiptService: new TerminalCashReceiptRetrievalService(receiptClient, options));
    }

    public async Task<TerminalCashReceiptRetrievalCommand> CreateRecordedFiscalWithReceiptCommandAsync()
    {
        var fiscal = await CreateConfirmedPaymentWithPendingFiscalAsync();
        await MarkFiscalRecordedAsync(fiscal);
        return await new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), OptionsForPreviewTests)
            .EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId);
    }

    public async Task<TerminalCashFiscalOutboxCommand> CreateConfirmedPaymentWithPendingFiscalAsync()
    {
        var service = new CashJournalService(OptionsForPreviewTests);
        var session = await service.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
            CashierId: $"cashier-{Guid.NewGuid():N}",
            AuthenticatedCashierSessionReference: $"auth-{Guid.NewGuid():N}",
            CashierShiftId: $"shift-{Guid.NewGuid():N}",
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
            CentralPmsTarget: CentralPmsCashReceiptBridgeHandlerTests.BaseUrl));
        Assert.True(received.IsSuccess);

        var paymentCommand = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Value!.Id)
            ?? throw new InvalidOperationException("Cash-payment outbox command was not created.");
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

        await new TerminalCashPaymentSubmissionService(cashClient, OptionsForPreviewTests).SubmitOrReadbackAsync(paymentCommand.Id);
        return await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), OptionsForPreviewTests)
            .GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId)
            ?? throw new InvalidOperationException("Fiscal command was not created.");
    }

    private async Task MarkFiscalRecordedAsync(TerminalCashFiscalOutboxCommand fiscal)
    {
        await using var dbContext = new CashJournalService(OptionsForPreviewTests).CreateDbContext();
        var command = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleAsync(value => value.Id == fiscal.Id);
        command.Status = TerminalCashFiscalCommandStatus.Recorded;
        command.FiscalIssuanceReferenceId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        command.FiscalIssuanceState = "FISCAL_ISSUANCE_RECORDED";
        command.PosFiscalDocumentId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        command.FiscalDocumentNumber = "SI-000001";
        command.FiscalNumberAssignedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");
        command.ResultClassification = "NEWLY_CREATED";
        command.RecordedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");
        command.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    public async Task DeleteReceiptCommandAsync(Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(OptionsForPreviewTests).CreateDbContext();
        var command = await dbContext.TerminalCashReceiptRetrievalCommands
            .SingleAsync(value => value.TerminalCashTenderId == terminalCashTenderId);
        dbContext.TerminalCashReceiptRetrievalCommands.Remove(command);
        await dbContext.SaveChangesAsync();
    }

    public async Task<int> CountReceiptCommandsAsync(Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(OptionsForPreviewTests).CreateDbContext();
        return await dbContext.TerminalCashReceiptRetrievalCommands
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

internal sealed class ScriptedCentralPmsReceiptClient : ICentralPmsTerminalCashReceiptClient
{
    private readonly Queue<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> _results = new();

    public List<Guid> Operations { get; } = [];

    public void Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse> result) =>
        _results.Enqueue(result);

    public Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(terminalCashTenderId);
        return Task.FromResult(_results.Dequeue());
    }
}
