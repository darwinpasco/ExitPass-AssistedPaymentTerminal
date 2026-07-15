using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class CashJournalServiceTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CreatesCashCustodySession()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();

        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession());

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value!.Id);
        Assert.Equal("cashier-001", result.Value.CashierId);
        Assert.Equal(CashCustodySessionStatus.Open, result.Value.Status);
        Assert.True(File.Exists(database.DatabasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CreatesTenderStarted()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);

        var result = await service.StartCashTenderAsync(TestRequests.StartTender(session.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(CashTenderState.TenderStarted, result.Value!.CurrentLocalState);

        var events = await service.GetCashTenderEventsAsync(result.Value.Id);
        Assert.Single(events);
        Assert.Equal(CashTenderEventType.TenderStarted, events[0].EventType);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RejectsAmountTenderedBelowAmountDue()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id, amountDue: 120m, amountTendered: 100m);

        var result = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.AmountTenderedBelowAmountDue, result.Error!.Code);
        Assert.Equal(CashTenderState.TenderStarted, (await service.GetCashTenderAsync(tender.Id))!.CurrentLocalState);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RequiresCashierAttestationForCashReceived()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);

        var result = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id, cashierAttested: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.CashierAttestationRequired, result.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CommitsCashReceivedAndEventAtomically()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id, amountDue: 100m, amountTendered: 150m);

        var result = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(
            tender.Id,
            denominations:
            [
                new CashDenominationLine("PHP-100", 100m, 1),
                new CashDenominationLine("PHP-50", 50m, 1)
            ]));

        Assert.True(result.IsSuccess);
        Assert.Equal(CashTenderState.CashReceived, result.Value!.CurrentLocalState);
        Assert.Equal(50m, result.Value.ChangeDue);

        var events = await service.GetCashTenderEventsAsync(tender.Id);
        Assert.Equal([CashTenderEventType.TenderStarted, CashTenderEventType.CashReceived], events.Select(value => value.EventType));
        Assert.Equal(2, events.Single(value => value.EventType == CashTenderEventType.CashReceived).DenominationEntries.Count);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task PersistsCashReceivedAcrossDatabaseReopen()
    {
        using var database = TestDatabase.Create();
        var firstService = database.CreateService();
        var session = await CreateSessionAsync(firstService);
        var tender = await StartTenderAsync(firstService, session.Id);
        var committed = await firstService.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id));
        Assert.True(committed.IsSuccess);

        var reopenedService = database.CreateService();
        var readback = await reopenedService.GetCashTenderAsync(tender.Id);

        Assert.NotNull(readback);
        Assert.Equal(CashTenderState.CashReceived, readback.CurrentLocalState);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task PreservesAppendOnlyEventHistory()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);
        await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id));

        var events = await service.GetCashTenderEventsAsync(tender.Id);

        Assert.Equal(2, events.Count);
        Assert.Equal(CashTenderEventType.TenderStarted, events[0].EventType);
        Assert.Equal(CashTenderEventType.CashReceived, events[1].EventType);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RejectsInvalidBackwardTransition()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);
        await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id));

        var result = await service.TryReturnToTenderStartedAsync(tender.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.InvalidStateTransition, result.Error!.Code);
        Assert.Equal(CashTenderState.CashReceived, (await service.GetCashTenderAsync(tender.Id))!.CurrentLocalState);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RejectsDuplicateUnresolvedTenderForSameParkingSession()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var firstTender = await StartTenderAsync(service, session.Id, parkingSessionId: "55555555-5555-4555-8555-555555555555");

        var duplicate = await service.StartCashTenderAsync(TestRequests.StartTender(
            session.Id,
            parkingSessionId: "55555555-5555-4555-8555-555555555555",
            localIdempotencyIdentity: "idem-duplicate"));

        Assert.False(duplicate.IsSuccess);
        Assert.Equal(CashJournalErrorCode.DuplicateUnresolvedTender, duplicate.Error!.Code);
        Assert.Equal(firstTender.Id, duplicate.Error.ExistingCashTenderId);
        Assert.Equal(CashTenderState.TenderStarted, duplicate.Error.ExistingCashTenderState);
        Assert.Single(await service.GetCashTenderEventsAsync(firstTender.Id));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task SupportsCashTenderWithoutCashDrawerCapability()
    {
        using var database = TestDatabase.Create();
        Assert.False(new LocalOperationsDatabaseOptions().CashDrawerEnabled);

        var service = database.CreateService();
        var session = await CreateSessionAsync(service);
        var tender = await StartTenderAsync(service, session.Id);

        var committed = await service.CommitCashReceivedAsync(TestRequests.CommitCashReceived(tender.Id));

        Assert.True(committed.IsSuccess);
        Assert.Equal(CashTenderState.CashReceived, committed.Value!.CurrentLocalState);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task UsesCallerSuppliedDevelopmentDatabasePath()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();

        await service.InitializeAsync();

        Assert.Equal(Path.GetFullPath(database.DatabasePath), service.DatabasePath);
        Assert.True(File.Exists(database.DatabasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void DefaultDatabasePathIsOutsideRepository()
    {
        var defaultPath = LocalOperationsDatabasePath.Resolve();
        var repositoryPath = RepositoryPath.Find();

        Assert.False(
            Path.GetFullPath(defaultPath).StartsWith(repositoryPath, StringComparison.OrdinalIgnoreCase),
            $"Default database path '{defaultPath}' must not be inside repository '{repositoryPath}'.");
    }

    private static async Task<CashCustodySessionSnapshot> CreateSessionAsync(CashJournalService service)
    {
        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession());

        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<CashTenderSnapshot> StartTenderAsync(
        CashJournalService service,
        Guid cashCustodySessionId,
        string parkingSessionId = "33333333-3333-4333-8333-333333333333",
        decimal amountDue = 100m,
        decimal amountTendered = 100m)
    {
        var result = await service.StartCashTenderAsync(TestRequests.StartTender(
            cashCustodySessionId,
            parkingSessionId: parkingSessionId,
            amountDue: amountDue,
            amountTendered: amountTendered));

        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}
