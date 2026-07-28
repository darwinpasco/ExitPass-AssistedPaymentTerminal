using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class LocalJournalBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ParkingSessionStartId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";
    private const string ParkingSessionCashReceivedId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2";
    private const string ParkingSessionReadbackId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa3";
    private const string TariffSnapshotId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string SiteId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string SiteGroupId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    [Fact]
    public async Task UnsupportedBridgeCommandIsRejected()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);

        var response = await SendAsync(handler, "localJournal.deleteEverything", "corr-unsupported", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported_command", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HealthReadinessCommandSucceeds()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);

        var response = await SendAsync(handler, LocalJournalBridgeCommand.Health, "corr-health", new { });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(response.RootElement.GetProperty("payload").GetProperty("healthy").GetBoolean());
        Assert.True(response.RootElement.GetProperty("payload").GetProperty("enabled").GetBoolean());
        Assert.False(response.RootElement.GetProperty("payload").GetProperty("cashDrawerEnabled").GetBoolean());
    }

    [Fact]
    public async Task StartTenderCommandMapsToLocalJournal()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);
        var sessionId = await CreateSessionAsync(handler);

        var response = await StartTenderAsync(handler, sessionId, ParkingSessionStartId);

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal(ParkingSessionStartId, payload.GetProperty("parkingSessionId").GetString());
        Assert.Equal(nameof(CashTenderState.TenderStarted), payload.GetProperty("currentLocalState").GetString());
    }

    [Fact]
    public async Task StartTenderCommandAcceptsOptionalDevelopmentFixtureTenderId()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);
        var sessionId = await CreateSessionAsync(handler);
        var fixtureTenderId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeee2001");

        var response = await SendAsync(
            handler,
            LocalJournalBridgeCommand.StartTender,
            "corr-start-fixture",
            new
            {
                localCashTenderId = fixtureTenderId,
                cashCustodySessionId = sessionId,
                parkingSessionId = ParkingSessionStartId,
                tariffSnapshotId = TariffSnapshotId,
                currency = "PHP",
                amountDue = 125m,
                amountTendered = 150m,
                localIdempotencyIdentity = $"idem-fixture-{ParkingSessionStartId}"
            });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal(fixtureTenderId, payload.GetProperty("id").GetGuid());
        Assert.Equal(nameof(CashTenderState.TenderStarted), payload.GetProperty("currentLocalState").GetString());
    }

    [Fact]
    public async Task CashReceivedCommandMapsToLocalJournalAndReturnsPersistedState()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);
        var sessionId = await CreateSessionAsync(handler);
        var tenderId = ReadTenderId(await StartTenderAsync(handler, sessionId, ParkingSessionCashReceivedId));

        var response = await SendAsync(
            handler,
            LocalJournalBridgeCommand.RecordCashReceived,
            "corr-received",
            new
            {
                localCashTenderId = tenderId,
                cashierAttested = true,
                statutoryTenderEvidence = new
                {
                    statutoryDiscountDecisionCommandId = "77777777-7777-4777-8777-777777770777",
                    statutoryDiscountPayableBasisApplicationCommandId = "88888888-8888-4888-8888-888888880001",
                    statutoryDiscountValidationId = "66666666-6666-4666-8666-666666660001",
                    originalTariffSnapshotId = "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
                    appliedTariffSnapshotId = TariffSnapshotId,
                    originalAmountMinorUnits = 12500,
                    finalAmountMinorUnits = 10000,
                    currency = "PHP",
                    amountAcknowledged = true,
                    amountAcknowledgedAt = DateTimeOffset.Parse("2026-07-15T00:01:30Z"),
                    immediateRevalidationOutcome = "PASSED_UNCHANGED",
                    immediateRevalidatedAt = DateTimeOffset.Parse("2026-07-15T00:01:45Z"),
                    centralPmsCorrelationId = "central-statutory-corr",
                    readinessStatus = "APPLIED",
                    readinessAction = (string?)null
                },
                denominations = new[]
                {
                    new { denominationCode = "PHP-100", denominationValue = 100m, quantity = 2 }
                }
            });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal(nameof(CashTenderState.CashReceived), payload.GetProperty("currentLocalState").GetString());
        Assert.Equal("77777777-7777-4777-8777-777777770777", payload.GetProperty("statutoryDiscountDecisionCommandId").GetString());
        Assert.Equal(TariffSnapshotId, payload.GetProperty("statutoryAppliedTariffSnapshotId").GetString());
        Assert.Equal(10000, payload.GetProperty("statutoryFinalAmountMinorUnits").GetInt64());
    }

    [Fact]
    public async Task ReadbackReturnsPersistedTenderAndEventHistory()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);
        var sessionId = await CreateSessionAsync(handler);
        var tenderId = ReadTenderId(await StartTenderAsync(handler, sessionId, ParkingSessionReadbackId));

        await SendAsync(
            handler,
            LocalJournalBridgeCommand.RecordCashReceived,
            "corr-received",
            new { localCashTenderId = tenderId, cashierAttested = true, denominations = Array.Empty<object>() });

        var readback = await SendAsync(
            handler,
            LocalJournalBridgeCommand.ReadTenderByParkingSession,
            "corr-readback",
            new { parkingSessionId = ParkingSessionReadbackId });

        Assert.True(readback.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(nameof(CashTenderState.CashReceived), readback.RootElement.GetProperty("payload").GetProperty("tender").GetProperty("currentLocalState").GetString());
        Assert.Equal(2, readback.RootElement.GetProperty("payload").GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task CorrelationIdIsPreserved()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);

        var response = await SendAsync(handler, LocalJournalBridgeCommand.Health, "corr-preserved", new { });

        Assert.Equal("corr-preserved", response.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MalformedBridgeRequestsFailSafely()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);

        var responseText = await handler.HandleWebMessageAsync("{ not-json");

        Assert.NotNull(responseText);
        using var response = JsonDocument.Parse(responseText!);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("malformed_request", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }


    [Fact]
    public async Task PayableBasisStateBridgeCommandsPersistAndRestorePreCashEvidence()
    {
        using var database = DesktopTestDatabase.Create();
        var handler = database.CreateHandler(enabled: true);

        var saved = await SendAsync(
            handler,
            LocalJournalBridgeCommand.PayableBasisStateSave,
            "corr-payable-save",
            new
            {
                localWorkflowId = $"{SiteId}:terminal-bridge:{ParkingSessionStartId}",
                lookupReferenceType = "ticket",
                lookupReferenceValue = "APT-ACTIVE-1001",
                parkingSessionId = ParkingSessionStartId,
                tariffSnapshotId = TariffSnapshotId,
                siteId = SiteId,
                siteGroupId = SiteGroupId,
                sitePosServerId = "pos-bridge",
                terminalId = "terminal-bridge",
                authoritativeAmountMinorUnits = 12500,
                currency = "PHP",
                tariffCalculatedAt = DateTimeOffset.UtcNow,
                tariffValidUntil = DateTimeOffset.UtcNow.AddMinutes(15),
                feeValidUntil = DateTimeOffset.UtcNow.AddMinutes(15),
                parkingStatus = "Active",
                paymentStatus = "Unpaid",
                sessionReadiness = "RESOLVED_PAYABLE",
                tariffReadiness = "CURRENT",
                paymentEligibility = "ELIGIBLE",
                terminalCashAvailability = "AVAILABLE",
                fiscalReadiness = "READY",
                salesInvoiceConfigurationReadiness = "READY",
                cashAcceptanceReadiness = "READY",
                readyForCashAcceptance = true,
                blockingReasonCodes = Array.Empty<string>(),
                retryable = false,
                safeUserFacingClassification = "READY_FOR_CASH_ACCEPTANCE",
                centralPmsCorrelationId = "central-corr-payable",
                revalidationOutcome = (string?)null,
                cashierAcknowledgementRequired = false,
                amountChanged = false,
                priorDisplayedAmountMinorUnits = (long?)null,
                statutoryDiscountStateJson = "{\"status\":\"awaiting_review\",\"statutoryDiscountDecisionCommandId\":\"77777777-7777-4777-8777-777777770777\"}"
            });

        Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());

        var restored = await SendAsync(
            handler,
            LocalJournalBridgeCommand.PayableBasisStateGetLatest,
            "corr-payable-get",
            new { terminalId = "terminal-bridge", siteId = SiteId });

        Assert.True(restored.RootElement.GetProperty("ok").GetBoolean());
        var payload = restored.RootElement.GetProperty("payload");
        Assert.Equal("APT-ACTIVE-1001", payload.GetProperty("lookupReferenceValue").GetString());
        Assert.True(payload.GetProperty("readyForCashAcceptance").GetBoolean());
        Assert.Equal("central-corr-payable", payload.GetProperty("centralPmsCorrelationId").GetString());
        Assert.Contains("awaiting_review", payload.GetProperty("statutoryDiscountStateJson").GetString(), StringComparison.Ordinal);
    }
    private static async Task<Guid> CreateSessionAsync(LocalJournalBridgeHandler handler)
    {
        using var response = await SendAsync(
            handler,
            LocalJournalBridgeCommand.CreateOrGetDevelopmentSession,
            "corr-session",
            new
            {
                cashierId = "cashier-bridge",
                authenticatedCashierSessionReference = "auth-bridge",
                cashierShiftId = "shift-bridge",
                terminalId = "terminal-bridge",
                siteId = SiteId,
                siteGroupId = SiteGroupId,
                posServerId = "pos-bridge",
                openingCashAmount = 500m
            });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        return response.RootElement.GetProperty("payload").GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> StartTenderAsync(LocalJournalBridgeHandler handler, Guid sessionId, string parkingSessionId) =>
        await SendAsync(
            handler,
            LocalJournalBridgeCommand.StartTender,
            "corr-start",
            new
            {
                cashCustodySessionId = sessionId,
                parkingSessionId,
                tariffSnapshotId = TariffSnapshotId,
                currency = "PHP",
                amountDue = 125m,
                amountTendered = 150m,
                localIdempotencyIdentity = $"idem-{parkingSessionId}"
            });

    private static Guid ReadTenderId(JsonDocument response)
    {
        using (response)
        {
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            return response.RootElement.GetProperty("payload").GetProperty("id").GetGuid();
        }
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
}

internal sealed class DesktopTestDatabase : IDisposable
{
    private DesktopTestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "cash-journal-bridge-test.db");
    }

    private string DirectoryPath { get; }

    private string DatabasePath { get; }

    public static DesktopTestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.Desktop.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new DesktopTestDatabase(directoryPath);
    }

    public LocalJournalBridgeHandler CreateHandler(bool enabled) =>
        new(new CashJournalService(new LocalOperationsDatabaseOptions(DatabasePath)), enabled);

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
