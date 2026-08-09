using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class TerminalCashPaymentOutboxTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CashReceivedAtomicallyCreatesOneOutboxCommand()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var tender = await CreateReceivedTenderAsync(service);

        var command = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);

        Assert.NotNull(command);
        Assert.Equal(tender.Id, command.TerminalCashTenderId);
        Assert.Equal(TerminalCashPaymentCommandStatus.Pending, command.Status);
        Assert.Equal(tender.LocalIdempotencyIdentity, command.IdempotencyKey);
        Assert.Equal(tender.CorrelationId, command.OriginalCorrelationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FailureDuringOutboxCreationRollsBackCashReceived()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id) with { SimulateOutboxCreationFailure = true }));

        Assert.Equal(CashTenderState.TenderStarted, (await service.GetCashTenderAsync(tender.Id))!.CurrentLocalState);
        Assert.Null(await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task DuplicateOutboxLookupForOneTenderReturnsExistingCommand()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var tender = await CreateReceivedTenderAsync(service);

        var first = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);
        var second = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task StableIdentitiesPayloadAndHashSurviveDatabaseReopen()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var tender = await CreateReceivedTenderAsync(service);
        var first = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);

        var reopened = database.CreateService();
        var second = await reopened.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(first.OriginalCorrelationId, second.OriginalCorrelationId);
        Assert.Equal(first.RequestPayloadJson, second.RequestPayloadJson);
        Assert.Equal(first.RequestPayloadHash, second.RequestPayloadHash);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FirstSubmissionSendsPost()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(Success(command), 201));

        await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal([TerminalCashPaymentOutboxOperationType.Submit], client.Operations);
        Assert.Equal(command.IdempotencyKey, client.SubmittedIdempotencyKeys.Single());
        Assert.Equal(command.OriginalCorrelationId, client.SubmittedCorrelationIds.Single());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task TimeoutPersistsUncertainState()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());

        var result = await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(TerminalCashPaymentCommandStatus.ReadbackRequired, result.Status);
        Assert.Equal("UNCERTAIN", result.ResultClassification);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RestartedUncertainCommandPerformsReadbackBeforePost()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var firstClient = new ScriptedCentralPmsClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());
        await new TerminalCashPaymentSubmissionService(firstClient, database.Options).SubmitOrReadbackAsync(command.Id);

        var secondClient = new ScriptedCentralPmsClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.NotFound(404, "MISSING_TERMINAL_CASH_TENDER_RECORD"));
        secondClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(Success(command), 201));

        await new TerminalCashPaymentSubmissionService(secondClient, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(
            [TerminalCashPaymentOutboxOperationType.Readback, TerminalCashPaymentOutboxOperationType.Submit],
            secondClient.Operations);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Readback200ConfirmsWithoutAnotherPost()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var firstClient = new ScriptedCentralPmsClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());
        await new TerminalCashPaymentSubmissionService(firstClient, database.Options).SubmitOrReadbackAsync(command.Id);

        var secondClient = new ScriptedCentralPmsClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.Confirmed(Readback(command), 200));

        var result = await new TerminalCashPaymentSubmissionService(secondClient, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(TerminalCashPaymentCommandStatus.Confirmed, result.Status);
        Assert.Equal([TerminalCashPaymentOutboxOperationType.Readback], secondClient.Operations);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Http409PersistsConflict()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Conflict(409, "DUPLICATE_CASH_TENDER"));

        var result = await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(TerminalCashPaymentCommandStatus.Conflict, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ValidationFailurePersistsRejected()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Rejected(400, "INVALID_CASH_AMOUNTS"));

        var result = await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(TerminalCashPaymentCommandStatus.Rejected, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Http5xxPersistsRetryPending()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var client = new ScriptedCentralPmsClient();
        client.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Unavailable(503, "SERVICE_UNAVAILABLE"));

        var result = await new TerminalCashPaymentSubmissionService(client, database.Options).SubmitOrReadbackAsync(command.Id);

        Assert.Equal(TerminalCashPaymentCommandStatus.RetryPending, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task AttemptRecordsRemainAppendOnly()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);
        var firstClient = new ScriptedCentralPmsClient();
        firstClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout());
        await new TerminalCashPaymentSubmissionService(firstClient, database.Options).SubmitOrReadbackAsync(command.Id);

        var secondClient = new ScriptedCentralPmsClient();
        secondClient.EnqueueReadback(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.NotFound(404, "MISSING_TERMINAL_CASH_TENDER_RECORD"));
        secondClient.EnqueueSubmit(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Unavailable(503, "SERVICE_UNAVAILABLE"));
        await new TerminalCashPaymentSubmissionService(secondClient, database.Options).SubmitOrReadbackAsync(command.Id);

        var attempts = await service.GetTerminalCashPaymentAttemptsAsync(command.Id);
        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptSequence));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NoFiscalExitGateOrProviderBehaviorIsCreatedLocally()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var command = await CreateOutboxAsync(service);

        Assert.DoesNotContain("fiscal", command.RequestPayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate", command.RequestPayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", command.RequestPayloadJson, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<TerminalCashPaymentOutboxCommand> CreateOutboxAsync(CashJournalService service)
    {
        var tender = await CreateReceivedTenderAsync(service);
        var command = await service.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Id);
        Assert.NotNull(command);
        return command;
    }

    private static async Task<CashCustodySessionSnapshot> CreateSessionAsync(CashJournalService service)
    {
        return await TestRequests.OpenShiftAndCreateSessionAsync(service);
    }

    private static async Task<CashTenderSnapshot> StartTenderAsync(CashJournalService service, Guid cashCustodySessionId)
    {
        var result = await service.StartCashTenderAsync(TestRequests.StartTender(
            cashCustodySessionId,
            localIdempotencyIdentity: $"cash-{Guid.NewGuid():N}"));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static TerminalCashPaymentResponse Success(TerminalCashPaymentOutboxCommand command) =>
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

    private static TerminalCashPaymentReadbackResponse Readback(TerminalCashPaymentOutboxCommand command)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<TerminalCashPaymentRequest>(
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
