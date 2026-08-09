using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class TerminalCashReceiptRetrievalTests
{
    private static readonly Guid FiscalIssuanceReferenceId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Guid PosFiscalDocumentId = Guid.Parse("88888888-8888-4888-8888-888888888888");

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NonRecordedFiscalStateCannotCreateReceiptRetrieval()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateFiscalCommandAsync(database);

        var receiptService = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiptService.EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RecordedFiscalResultCreatesOneRetrievalRecord()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);

        var receipt = await new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options)
            .GetReceiptRetrievalByTenderAsync(fiscal.TerminalCashTenderId);

        Assert.NotNull(receipt);
        Assert.Equal(fiscal.TerminalCashTenderId, receipt.TerminalCashTenderId);
        Assert.Equal(fiscal.Id, receipt.RelatedFiscalCommandId);
        Assert.Equal(fiscal.FiscalIssuanceReferenceId, receipt.FiscalIssuanceReferenceId);
        Assert.Equal(fiscal.PosFiscalDocumentId, receipt.PosFiscalDocumentId);
        Assert.Equal(TerminalCashReceiptRetrievalStatus.Pending, receipt.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task MissingPosFiscalDocumentIdRejectsRetrievalCreation()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        await using var dbContext = database.CreateService().CreateDbContext();
        var stored = await dbContext.TerminalCashFiscalOutboxCommands.SingleAsync(command => command.Id == fiscal.Id);
        stored.PosFiscalDocumentId = null;
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options)
                .EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RepeatedEnsureReturnsExistingRecordAndPreservesCorrelation()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        await DeleteReceiptRetrievalAsync(database, fiscal.TerminalCashTenderId);
        var service = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options);

        var first = await service.EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId);
        var second = await service.EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.RetrievalCorrelationId, second.RetrievalCorrelationId);
        Assert.Equal(first.PosFiscalDocumentId, second.PosFiscalDocumentId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentEnsureCreatesOnlyOneRecord()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        await DeleteReceiptRetrievalAsync(database, fiscal.TerminalCashTenderId);

        var first = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options);
        var second = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options);

        var results = await Task.WhenAll(
            first.EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId),
            second.EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId));

        Assert.Single(results.Select(command => command.Id).Distinct());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CorrelationFiscalAndPosReferencesSurviveDatabaseReopen()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        var first = await GetReceiptRetrievalAsync(database, fiscal.TerminalCashTenderId);

        var second = await new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options)
            .GetReceiptRetrievalByTenderAsync(fiscal.TerminalCashTenderId);

        Assert.NotNull(second);
        Assert.Equal(first.RetrievalCorrelationId, second.RetrievalCorrelationId);
        Assert.Equal(first.FiscalIssuanceReferenceId, second.FiscalIssuanceReferenceId);
        Assert.Equal(first.PosFiscalDocumentId, second.PosFiscalDocumentId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FirstRetrievalSendsCentralPmsGet()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(receipt),
            200,
            Guid.Parse(receipt.RetrievalCorrelationId)));

        await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Single(client.RequestedTenderIds);
        Assert.Equal(receipt.TerminalCashTenderId, client.RequestedTenderIds.Single());
        Assert.Equal(receipt.RetrievalCorrelationId, client.RequestedCorrelationIds.Single());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task AvailableResponsePersistsAuthoritativePayloadHashAndMetadata()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(receipt),
            200,
            Guid.Parse(receipt.RetrievalCorrelationId)));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Available, result.Status);
        Assert.NotNull(result.AuthoritativePresentationJson);
        Assert.Equal(TerminalCashReceiptPayloadFactory.ComputeHash(result.AuthoritativePresentationJson!), result.AuthoritativePayloadHash);
        Assert.Equal("SI-000001", result.FiscalDocumentNumber);
        Assert.Equal("digital-sales-invoice-presentation-json-v1", result.PresentationVersion);
        Assert.Equal("digital-sales-invoice-json-v1", result.TemplateVersion);
        Assert.Equal("CONFIRMED", result.CanonicalPaymentStatus);
        Assert.Equal("sha256:fiscal-semantic", result.SemanticRequestHash);
        Assert.Equal("pos-server-semantic-hash:sha256:v1", result.SemanticRequestHashVersion);
        Assert.Equal("MATCHED", result.SemanticRequestHashStatus);
        Assert.Equal("application/json", result.ContentType);
        Assert.False(result.LastRetryable);
        Assert.Equal(result.RetrievalCorrelationId, result.LastCentralPmsCorrelationId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T00:05:00Z"), result.LastUpdatedFromCentralPms);
        Assert.Contains("\"tenderType\":\"CASH\"", result.AuthoritativePresentationJson);
        Assert.Contains("\"totalType\":\"grand_total\"", result.AuthoritativePresentationJson);
        Assert.Contains("\"taxType\":\"VAT\"", result.AuthoritativePresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task PayloadHashRemainsStableAfterReopen()
    {
        using var database = TestDatabase.Create();
        var result = await RetrieveAvailableAsync(database);

        var reopened = await GetReceiptRetrievalAsync(database, result.TerminalCashTenderId);

        Assert.Equal(result.AuthoritativePayloadHash, reopened.AuthoritativePayloadHash);
        Assert.Equal(result.AuthoritativePresentationJson, reopened.AuthoritativePresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NotReadyPersistsNoFabricatedContent()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady(
            409,
            "TERMINAL_CASH_RECEIPT_PRESENTATION_NOT_READY"));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.NotReady, result.Status);
        Assert.Null(result.AuthoritativePresentationJson);
        Assert.Null(result.AuthoritativePayloadHash);
        Assert.True(result.LastRetryable);
        Assert.NotNull(result.NextRetryAt);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task UnsupportedPresentationPersistsTerminalStateWithoutFallback()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unsupported(
            409,
            "POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED",
            retryable: false,
            correlationId: Guid.Parse(receipt.RetrievalCorrelationId)));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Unsupported, result.Status);
        Assert.Equal("UNSUPPORTED", result.ResultClassification);
        Assert.False(result.LastRetryable);
        Assert.Null(result.NextRetryAt);
        Assert.Null(result.AuthoritativePresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task MalformedPresentationPersistsTerminalStateWithoutFallback()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Malformed(
            409,
            "POS_SERVER_RECEIPT_PRESENTATION_MALFORMED",
            retryable: false,
            correlationId: Guid.Parse(receipt.RetrievalCorrelationId)));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Malformed, result.Status);
        Assert.Equal("MALFORMED", result.ResultClassification);
        Assert.False(result.LastRetryable);
        Assert.Null(result.NextRetryAt);
        Assert.Null(result.AuthoritativePresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task DifferentPresentationForSameFiscalIdentityIsBlocked()
    {
        using var database = TestDatabase.Create();
        var available = await RetrieveAvailableAsync(database);
        var originalPayload = available.AuthoritativePresentationJson;
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(available, voided: false, alternatePayload: true),
            200,
            Guid.Parse(available.RetrievalCorrelationId)));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(available.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Inconsistent, result.Status);
        Assert.Equal("PRESENTATION_INTEGRITY_MISMATCH", result.ResultClassification);
        Assert.Equal(originalPayload, result.AuthoritativePresentationJson);
        Assert.False(result.LastRetryable);
        Assert.Null(result.NextRetryAt);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task UnavailablePreservesExistingAuthoritativeSnapshot()
    {
        using var database = TestDatabase.Create();
        var available = await RetrieveAvailableAsync(database);
        var originalPayload = available.AuthoritativePresentationJson;
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable(
            503,
            "POS_SERVER_RECEIPT_PRESENTATION_UNAVAILABLE"));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(available.Id);

        Assert.Equal(originalPayload, result.AuthoritativePresentationJson);
        Assert.Equal("UNAVAILABLE", result.ResultClassification);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ReferenceMismatchPersistsInconsistent()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(receipt) with { PosFiscalDocumentId = Guid.NewGuid() },
            200));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Inconsistent, result.Status);
        Assert.Equal("REFERENCE_MISMATCH", result.ResultClassification);
        Assert.Null(result.AuthoritativePresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task VoidedPostureIsPreserved()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            Available(receipt, voided: true),
            200));

        var result = await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);

        Assert.Equal(TerminalCashReceiptRetrievalStatus.Voided, result.Status);
        Assert.Equal("voided", result.VoidStatus);
        Assert.Equal("operator_void", result.VoidReasonCode);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RepeatedRetrievalCreatesNoSecondRetrievalRecord()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt), 200));
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt), 200));
        var service = new TerminalCashReceiptRetrievalService(client, database.Options);

        await service.RetrieveReceiptAsync(receipt.Id);
        await service.RetrieveReceiptAsync(receipt.Id);

        await using var dbContext = database.CreateService().CreateDbContext();
        Assert.Equal(1, await dbContext.TerminalCashReceiptRetrievalCommands.CountAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RetrievalAttemptsRemainAppendOnly()
    {
        using var database = TestDatabase.Create();
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady(409, "NOT_READY"));
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable(503, "UNAVAILABLE"));
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt), 200));
        var service = new TerminalCashReceiptRetrievalService(client, database.Options);

        await service.RetrieveReceiptAsync(receipt.Id);
        await service.RetrieveReceiptAsync(receipt.Id);
        await service.RetrieveReceiptAsync(receipt.Id);
        var attempts = await service.GetReceiptAttemptsAsync(receipt.Id);

        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptSequence));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ExistingRecordedFiscalRecoveryCreatesOneMissingRecord()
    {
        using var database = TestDatabase.Create();
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        await DeleteReceiptRetrievalAsync(database, fiscal.TerminalCashTenderId);

        var receipt = await new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options)
            .EnsureForRecordedFiscalAsync(fiscal.TerminalCashTenderId);

        Assert.Equal(fiscal.TerminalCashTenderId, receipt.TerminalCashTenderId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NoRecordIsCreatedForFiscalConflictOrRejection()
    {
        using var database = TestDatabase.Create();
        var conflict = await CreateRecordedFiscalCommandAsync(database, "FISCAL_ISSUANCE_CONFLICT", TerminalCashFiscalCommandStatus.Conflict);
        var rejected = await CreateRecordedFiscalCommandAsync(database, "FISCAL_ISSUANCE_FAILED_REQUEST", TerminalCashFiscalCommandStatus.Rejected);
        var service = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureForRecordedFiscalAsync(conflict.TerminalCashTenderId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureForRecordedFiscalAsync(rejected.TerminalCashTenderId));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void NoDirectPosRenderPrintExitGateOrProviderBehaviorIsIntroduced()
    {
        var serviceDependencies = typeof(TerminalCashReceiptRetrievalService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name);

        Assert.DoesNotContain(serviceDependencies, name => name.Contains("PosServer", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceDependencies, name => name.Contains("PaymentOrchestrator", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceDependencies, name => name.Contains("Gate", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceDependencies, name => name.Contains("Print", StringComparison.Ordinal));
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> CreateReceiptRetrievalAsync(TestDatabase database)
    {
        var fiscal = await CreateRecordedFiscalCommandAsync(database);
        return await GetReceiptRetrievalAsync(database, fiscal.TerminalCashTenderId);
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> RetrieveAvailableAsync(TestDatabase database)
    {
        var receipt = await CreateReceiptRetrievalAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt), 200));
        return await new TerminalCashReceiptRetrievalService(client, database.Options).RetrieveReceiptAsync(receipt.Id);
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> GetReceiptRetrievalAsync(TestDatabase database, Guid terminalCashTenderId)
    {
        var command = await new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), database.Options)
            .GetReceiptRetrievalByTenderAsync(terminalCashTenderId);
        Assert.NotNull(command);
        return command;
    }

    private static async Task<TerminalCashFiscalOutboxCommand> CreateRecordedFiscalCommandAsync(
        TestDatabase database,
        string fiscalState = "FISCAL_ISSUANCE_RECORDED",
        TerminalCashFiscalCommandStatus status = TerminalCashFiscalCommandStatus.Recorded)
    {
        var fiscal = await CreateFiscalCommandAsync(database);

        if (status == TerminalCashFiscalCommandStatus.Recorded)
        {
            var client = new ScriptedCentralPmsFiscalClient();
            client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(
                RecordedFiscal(fiscal),
                200));
            return await new TerminalCashFiscalSubmissionService(client, database.Options)
                .SubmitOrReadbackFiscalAsync(fiscal.Id);
        }

        await using var dbContext = database.CreateService().CreateDbContext();
        var stored = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleAsync(command => command.Id == fiscal.Id);
        stored.Status = status;
        stored.FiscalIssuanceReferenceId = FiscalIssuanceReferenceId;
        stored.FiscalIssuanceState = fiscalState;
        stored.PosFiscalDocumentId = PosFiscalDocumentId;
        stored.FiscalDocumentNumber = "SI-000001";
        stored.FiscalNumberAssignedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");
        stored.RecordedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");
        await dbContext.SaveChangesAsync();
        return stored;
    }

    private static async Task<TerminalCashFiscalOutboxCommand> CreateFiscalCommandAsync(TestDatabase database)
    {
        var payment = await CreateConfirmedPaymentAsync(database);
        var fiscal = await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options)
            .GetFiscalCommandByTenderAsync(payment.TerminalCashTenderId);
        Assert.NotNull(fiscal);
        return fiscal;
    }

    private static async Task DeleteReceiptRetrievalAsync(TestDatabase database, Guid terminalCashTenderId)
    {
        await using var dbContext = database.CreateService().CreateDbContext();
        var command = await dbContext.TerminalCashReceiptRetrievalCommands
            .SingleOrDefaultAsync(value => value.TerminalCashTenderId == terminalCashTenderId);
        if (command is not null)
        {
            dbContext.TerminalCashReceiptRetrievalCommands.Remove(command);
            await dbContext.SaveChangesAsync();
        }
    }

    private static async Task<TerminalCashPaymentOutboxCommand> CreateConfirmedPaymentAsync(TestDatabase database)
    {
        var command = await CreateOutboxAsync(database.CreateService());
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(PaymentSuccess(command), 201));
        return await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);
    }

    private static async Task<TerminalCashPaymentOutboxCommand> CreateOutboxAsync(CashJournalService service)
    {
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var tender = await service.StartCashTenderAsync(TestRequests.StartTender(
            session.Id,
            parkingSessionId: Guid.NewGuid().ToString("D"),
            localIdempotencyIdentity: $"cash-{Guid.NewGuid():N}"));
        Assert.True(tender.IsSuccess);
        var received = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Value!.Id) with
        {
            CentralPmsTarget = "https://central-pms.example.invalid"
        });
        Assert.True(received.IsSuccess);
        var command = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Value.Id);
        Assert.NotNull(command);
        return command;
    }

    private static TerminalCashPaymentResponse PaymentSuccess(TerminalCashPaymentOutboxCommand command) =>
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

    private static TerminalCashFiscalIssuanceResponse RecordedFiscal(TerminalCashFiscalOutboxCommand command) =>
        new(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            "NEWLY_CREATED",
            PosFiscalDocumentId,
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

    private static TerminalCashReceiptPresentationResponse Available(
        TerminalCashReceiptRetrievalCommand command,
        bool voided = false)
    {
        var presentation = AuthoritativePresentation(voided, alternatePayload: false);
        return new TerminalCashReceiptPresentationResponse(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            "CONFIRMED",
            command.FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            command.PosFiscalDocumentId,
            "SI-000001",
            "recorded",
            voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
            "digital-sales-invoice-presentation-json-v1",
            "digital-sales-invoice-json-v1",
            "sha256:fiscal-semantic",
            "pos-server-semantic-hash:sha256:v1",
            "MATCHED",
            "application/json",
            presentation,
            voided ? "voided" : null,
            voided ? "operator_void" : null,
            voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            Guid.Parse(command.RetrievalCorrelationId));
    }

    private static TerminalCashReceiptPresentationResponse Available(
        TerminalCashReceiptRetrievalCommand command,
        bool voided,
        bool alternatePayload)
    {
        var presentation = AuthoritativePresentation(voided, alternatePayload);
        return new TerminalCashReceiptPresentationResponse(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            "CONFIRMED",
            command.FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            command.PosFiscalDocumentId,
            "SI-000001",
            "recorded",
            voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
            "digital-sales-invoice-presentation-json-v1",
            "digital-sales-invoice-json-v1",
            "sha256:fiscal-semantic",
            "pos-server-semantic-hash:sha256:v1",
            "MATCHED",
            "application/json",
            presentation,
            voided ? "voided" : null,
            voided ? "operator_void" : null,
            voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            Guid.Parse(command.RetrievalCorrelationId));
    }

    private static JsonElement AuthoritativePresentation(bool voided, bool alternatePayload)
    {
        var voidJson = voided
            ? "\"voidStatus\":\"voided\",\"voidReasonCode\":\"operator_void\",\"voidedAt\":\"2026-07-15T00:06:00Z\""
            : "\"voidStatus\":null,\"voidReasonCode\":null,\"voidedAt\":null";
        var description = alternatePayload ? "Altered parking fee - cash" : "Parking fee - cash";
        var json = $$"""
        {
          "succeeded": true,
          "code": "presented",
          "message": "Digital Sales Invoice presentation returned.",
          "presentation": {
            "presentationVersion": "digital-sales-invoice-presentation-json-v1",
            "lines": [
              { "description": "{{description}}", "amountMinorUnits": 10000 }
            ],
            "taxes": [
              { "taxType": "VAT", "amountMinorUnits": 0 }
            ],
            "totals": [
              { "totalType": "grand_total", "amountMinorUnits": 10000 }
            ],
            "tenders": [
              { "tenderType": "CASH", "amountMinorUnits": 10000 }
            ]
          },
          "fiscalDocumentId": "{{PosFiscalDocumentId:D}}",
          "fiscalDocumentNumber": "SI-000001",
          "presentationVersion": "digital-sales-invoice-presentation-json-v1",
          "templateVersion": "digital-sales-invoice-json-v1",
          "contentType": "application/json",
          {{voidJson}}
        }
        """;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal sealed class ScriptedCentralPmsReceiptClient : ICentralPmsTerminalCashReceiptClient
{
    private readonly Queue<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> _results = new();

    public List<Guid> RequestedTenderIds { get; } = [];

    public List<string> RequestedCorrelationIds { get; } = [];

    public void Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse> result) =>
        _results.Enqueue(result);

    public Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        RequestedTenderIds.Add(terminalCashTenderId);
        RequestedCorrelationIds.Add(correlationId);
        return Task.FromResult(_results.Dequeue());
    }
}
