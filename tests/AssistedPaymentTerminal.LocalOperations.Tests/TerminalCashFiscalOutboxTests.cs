using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class TerminalCashFiscalOutboxTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task UnconfirmedLocalPaymentCannotCreateFiscalCommand()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var tender = await CreateReceivedTenderAsync(service);

        var fiscalService = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fiscalService.EnsureForConfirmedPaymentAsync(tender.Id));
        Assert.Null(await fiscalService.GetFiscalCommandByTenderAsync(tender.Id));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConfirmedCanonicalPaymentAtomicallyCreatesOneFiscalCommand()
    {
        using var database = TestDatabase.Create();
        var paymentCommand = await CreateConfirmedPaymentAsync(database);

        var fiscalCommand = await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options)
            .GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId);

        Assert.NotNull(fiscalCommand);
        Assert.Equal(paymentCommand.TerminalCashTenderId, fiscalCommand.TerminalCashTenderId);
        Assert.Equal(paymentCommand.Id, fiscalCommand.RelatedCashPaymentOutboxCommandId);
        Assert.Equal(paymentCommand.CanonicalPaymentAttemptId, fiscalCommand.CanonicalPaymentAttemptId);
        Assert.Equal(paymentCommand.CanonicalPaymentConfirmationId, fiscalCommand.CanonicalPaymentConfirmationId);
        Assert.Equal(TerminalCashFiscalCommandStatus.Pending, fiscalCommand.Status);
        Assert.Equal("{}", fiscalCommand.RequestRepresentationJson);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task EnsureForConfirmedPaymentCreatesOneCommandWhenMissing()
    {
        using var database = TestDatabase.Create();
        var paymentCommand = await CreateConfirmedPaymentAsync(database);
        await DeleteFiscalCommandAsync(database, paymentCommand.TerminalCashTenderId);

        var fiscalService = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);
        var fiscalCommand = await fiscalService.EnsureForConfirmedPaymentAsync(paymentCommand.TerminalCashTenderId);

        Assert.Equal(paymentCommand.TerminalCashTenderId, fiscalCommand.TerminalCashTenderId);
        Assert.Equal(paymentCommand.CanonicalPaymentConfirmationId, fiscalCommand.CanonicalPaymentConfirmationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RepeatedEnsureReturnsExistingCommand()
    {
        using var database = TestDatabase.Create();
        var paymentCommand = await CreateConfirmedPaymentAsync(database);
        await DeleteFiscalCommandAsync(database, paymentCommand.TerminalCashTenderId);
        var fiscalService = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);

        var first = await fiscalService.EnsureForConfirmedPaymentAsync(paymentCommand.TerminalCashTenderId);
        var second = await fiscalService.EnsureForConfirmedPaymentAsync(paymentCommand.TerminalCashTenderId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FiscalIdempotencyKey, second.FiscalIdempotencyKey);
        Assert.Equal(first.FiscalCorrelationId, second.FiscalCorrelationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentEnsureAttemptsCreateOnlyOneCommand()
    {
        using var database = TestDatabase.Create();
        var paymentCommand = await CreateConfirmedPaymentAsync(database);
        await DeleteFiscalCommandAsync(database, paymentCommand.TerminalCashTenderId);

        var first = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);
        var second = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);

        var results = await Task.WhenAll(
            first.EnsureForConfirmedPaymentAsync(paymentCommand.TerminalCashTenderId),
            second.EnsureForConfirmedPaymentAsync(paymentCommand.TerminalCashTenderId));

        Assert.Single(results.Select(command => command.Id).Distinct());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task StableFiscalIdentitiesPayloadHashAndCanonicalIdsSurviveDatabaseReopen()
    {
        using var database = TestDatabase.Create();
        var paymentCommand = await CreateConfirmedPaymentAsync(database);
        var first = await GetFiscalCommandAsync(database, paymentCommand.TerminalCashTenderId);

        var second = await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options)
            .GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId);

        Assert.NotNull(second);
        Assert.Equal(first.FiscalIdempotencyKey, second.FiscalIdempotencyKey);
        Assert.Equal(first.FiscalCorrelationId, second.FiscalCorrelationId);
        Assert.Equal(first.RequestRepresentationJson, second.RequestRepresentationJson);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(first.CanonicalPaymentAttemptId, second.CanonicalPaymentAttemptId);
        Assert.Equal(first.CanonicalPaymentConfirmationId, second.CanonicalPaymentConfirmationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FirstFiscalExecutionSendsPost()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(command), 200));

        await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal([TerminalCashFiscalOperationType.Submit], client.Operations);
        Assert.Equal(command.FiscalIdempotencyKey, client.SubmittedIdempotencyKeys.Single());
        Assert.Equal(command.FiscalCorrelationId, client.SubmittedCorrelationIds.Single());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task TimeoutPersistsReadbackRequiredState()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());

        var result = await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.ReadbackRequired, result.Status);
        Assert.Equal("UNCERTAIN", result.ResultClassification);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RestartedUncertainFiscalCommandPerformsGetBeforePost()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var firstClient = new ScriptedCentralPmsFiscalClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());
        await new TerminalCashFiscalSubmissionService(firstClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        var secondClient = new ScriptedCentralPmsFiscalClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.NotFound(404, "TERMINAL_CASH_FISCAL_ISSUANCE_NOT_FOUND"));
        secondClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(command), 200));

        await new TerminalCashFiscalSubmissionService(secondClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal([TerminalCashFiscalOperationType.Readback, TerminalCashFiscalOperationType.Submit], secondClient.Operations);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Readback200ClosesCommandWithoutAnotherPost()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var firstClient = new ScriptedCentralPmsFiscalClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());
        await new TerminalCashFiscalSubmissionService(firstClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        var secondClient = new ScriptedCentralPmsFiscalClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(Recorded(command), 200));

        var result = await new TerminalCashFiscalSubmissionService(secondClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.Recorded, result.Status);
        Assert.Equal([TerminalCashFiscalOperationType.Readback], secondClient.Operations);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task IdempotentReplayPreservesFiscalReferenceAndDocumentIds()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var response = Recorded(command, "IDEMPOTENT_REPLAY");
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(response, 200));

        var result = await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.Recorded, result.Status);
        Assert.Equal(response.FiscalIssuanceReferenceId, result.FiscalIssuanceReferenceId);
        Assert.Equal(response.PosFiscalDocumentId, result.PosFiscalDocumentId);
        Assert.Equal(response.FiscalDocumentNumber, result.FiscalDocumentNumber);
        Assert.Equal("IDEMPOTENT_REPLAY", result.ResultClassification);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Http409PersistsConflict()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Conflict(409, "TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT"));

        var result = await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.Conflict, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task DeterministicRejectionPersistsRejected()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Rejected(400, "CORRELATION_ID_REQUIRED"));

        var result = await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.Rejected, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Http5xxPersistsRetryPending()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var client = new ScriptedCentralPmsFiscalClient();
        client.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unavailable(503, "POS_SERVER_UNAVAILABLE"));

        var result = await new TerminalCashFiscalSubmissionService(client, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        Assert.Equal(TerminalCashFiscalCommandStatus.RetryPending, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FiscalAttemptRecordsRemainAppendOnly()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);
        var firstClient = new ScriptedCentralPmsFiscalClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout());
        await new TerminalCashFiscalSubmissionService(firstClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        var secondClient = new ScriptedCentralPmsFiscalClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.NotFound(404, "TERMINAL_CASH_FISCAL_ISSUANCE_NOT_FOUND"));
        secondClient.EnqueueSubmit(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unavailable(503, "POS_SERVER_UNAVAILABLE"));
        await new TerminalCashFiscalSubmissionService(secondClient, database.Options).SubmitOrReadbackFiscalAsync(command.Id);

        var attempts = await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options)
            .GetFiscalAttemptsAsync(command.Id);

        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptSequence));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NoFiscalCommandIsCreatedForRejectedConflictOrUncertainPayment()
    {
        using var database = TestDatabase.Create();
        var rejected = await CreatePaymentWithOutcomeAsync(database, CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Rejected(400, "INVALID_CASH_AMOUNTS"));
        var conflict = await CreatePaymentWithOutcomeAsync(database, CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Conflict(409, "DUPLICATE_CASH_TENDER"));
        var uncertain = await CreatePaymentWithOutcomeAsync(database, CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());
        var fiscalService = new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options);

        Assert.Null(await fiscalService.GetFiscalCommandByTenderAsync(rejected.TerminalCashTenderId));
        Assert.Null(await fiscalService.GetFiscalCommandByTenderAsync(conflict.TerminalCashTenderId));
        Assert.Null(await fiscalService.GetFiscalCommandByTenderAsync(uncertain.TerminalCashTenderId));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NoDirectPosReceiptExitGateOrProviderBehaviorIsIntroduced()
    {
        using var database = TestDatabase.Create();
        var command = await CreateFiscalCommandAsync(database);

        Assert.Equal("{}", command.RequestRepresentationJson);
        Assert.DoesNotContain("receipt", command.RequestRepresentationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exit", command.RequestRepresentationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate", command.RequestRepresentationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", command.RequestRepresentationJson, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CashTenderSnapshot> CreateReceivedTenderAsync(CashJournalService service)
    {
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);
        var result = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id) with
        {
            CentralPmsTarget = "https://central-pms.example.invalid"
        });
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<TerminalCashPaymentOutboxCommand> CreateConfirmedPaymentAsync(TestDatabase database)
    {
        var command = await CreateOutboxAsync(database.CreateService());
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(PaymentSuccess(command), 201));

        return await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);
    }

    private static async Task<TerminalCashPaymentOutboxCommand> CreatePaymentWithOutcomeAsync(
        TestDatabase database,
        CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse> outcome)
    {
        var command = await CreateOutboxAsync(database.CreateService());
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(outcome);
        return await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);
    }

    private static async Task<TerminalCashFiscalOutboxCommand> CreateFiscalCommandAsync(TestDatabase database)
    {
        var paymentCommand = await CreateConfirmedPaymentAsync(database);
        return await GetFiscalCommandAsync(database, paymentCommand.TerminalCashTenderId);
    }

    private static async Task<TerminalCashFiscalOutboxCommand> GetFiscalCommandAsync(TestDatabase database, Guid terminalCashTenderId)
    {
        var command = await new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), database.Options)
            .GetFiscalCommandByTenderAsync(terminalCashTenderId);
        Assert.NotNull(command);
        return command;
    }

    private static async Task DeleteFiscalCommandAsync(TestDatabase database, Guid terminalCashTenderId)
    {
        await using var dbContext = database.CreateService().CreateDbContext();
        var command = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleAsync(value => value.TerminalCashTenderId == terminalCashTenderId);
        dbContext.TerminalCashFiscalOutboxCommands.Remove(command);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<TerminalCashPaymentOutboxCommand> CreateOutboxAsync(CashJournalService service)
    {
        var tender = await CreateReceivedTenderAsync(service);
        var command = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);
        Assert.NotNull(command);
        return command;
    }

    private static async Task<CashCustodySessionSnapshot> CreateSessionAsync(CashJournalService service)
    {
        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession());
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<CashTenderSnapshot> StartTenderAsync(CashJournalService service, Guid cashCustodySessionId)
    {
        var result = await service.StartCashTenderAsync(TestRequests.StartTender(
            cashCustodySessionId,
            parkingSessionId: Guid.NewGuid().ToString("D"),
            localIdempotencyIdentity: $"cash-{Guid.NewGuid():N}"));
        Assert.True(result.IsSuccess);
        return result.Value!;
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

internal sealed class ScriptedCentralPmsFiscalClient : ICentralPmsTerminalCashFiscalClient
{
    private readonly Queue<object> _submitResults = new();
    private readonly Queue<object> _readbackResults = new();

    public List<TerminalCashFiscalOperationType> Operations { get; } = [];

    public List<string> SubmittedIdempotencyKeys { get; } = [];

    public List<string> SubmittedCorrelationIds { get; } = [];

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
        SubmittedIdempotencyKeys.Add(idempotencyKey);
        SubmittedCorrelationIds.Add(correlationId);
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
