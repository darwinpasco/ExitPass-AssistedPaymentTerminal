using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class CashCustodyMovementTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task SafeDrop_IsPendingUntilDistinctAcknowledgment_AndBlocksClose()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);

        var initiated = await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 200m));
        var close = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 800m));

        Assert.True(initiated.IsSuccess);
        Assert.Equal(CashCustodyMovementStatus.PendingAcknowledgment, initiated.Value!.Status);
        Assert.False(close.IsSuccess);
        Assert.Equal(CashJournalErrorCode.PendingCashCustodyMovement, close.Error!.Code);
    }

    [Theory]
    [InlineData(CashCustodyMovementType.SafeDrop)]
    [InlineData(CashCustodyMovementType.DirectRemittance)]
    [Trait("Category", "LocalOperations")]
    public async Task CompletedCashOut_AffectsClosingExactlyOnce(CashCustodyMovementType movementType)
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, movementType, 200m))).Value!;

        var first = await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(movement.Id));
        var replay = await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(movement.Id));
        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 800m));

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(CashCustodyMovementStatus.Completed, replay.Value!.Status);
        Assert.True(closed.IsSuccess);
        Assert.Equal(800m, closed.Value!.ExpectedClosingCashAmount);
        Assert.Equal(0m, closed.Value.VarianceAmount);
        Assert.Equal(1, closed.Value.CompletedMovementCountAtClose);
        Assert.Equal(20_000, closed.Value.CompletedCashOutMinorUnitsAtClose);
        Assert.Single(closed.Value.CompletedMovementReferencesAtClose!);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ReferencedPartialReturn_IncreasesExpectedCash_AndExcessReturnFailsClosed()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var outbound = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 300m))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(outbound.Id))).IsSuccess);

        var returned = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.CashReturn, 100m, outbound.Id, "returner-001", "return-001"))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(
            Acknowledge(returned.Id, "returner-001", "receiver-002", "ack-return-001"))).IsSuccess);

        var excess = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.CashReturn, 250m, outbound.Id, "returner-002", "return-excess"))).Value!;
        var excessResult = await service.AcknowledgeCashCustodyMovementAsync(
            Acknowledge(excess.Id, "returner-002", "receiver-003", "ack-return-excess"));
        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 800m));

        Assert.False(excessResult.IsSuccess);
        Assert.Equal(CashJournalErrorCode.CashReturnLimitExceeded, excessResult.Error!.Code);
        Assert.False(closed.IsSuccess);
        Assert.Equal(CashJournalErrorCode.PendingCashCustodyMovement, closed.Error!.Code);
        Assert.True((await service.ResolvePendingCashCustodyMovementAsync(Cancel(excess.Id))).IsSuccess);
        closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 800m));
        Assert.True(closed.IsSuccess);
        Assert.Equal(800m, closed.Value!.ExpectedClosingCashAmount);
        Assert.Equal(10_000, closed.Value.CompletedCashInMinorUnitsAtClose);
        Assert.Equal(30_000, closed.Value.CompletedCashOutMinorUnitsAtClose);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CancelledMovement_HasNoAccountingEffect()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 400m))).Value!;

        var cancelled = await service.ResolvePendingCashCustodyMovementAsync(Cancel(movement.Id));
        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 1_000m));

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(CashCustodyMovementStatus.Cancelled, cancelled.Value!.Status);
        Assert.True(closed.IsSuccess);
        Assert.Equal(1_000m, closed.Value!.ExpectedClosingCashAmount);
        Assert.Equal(0, closed.Value.CompletedMovementCountAtClose);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RejectedMovement_HasNoAccountingEffect()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.DirectRemittance, 400m))).Value!;

        var rejected = await service.ResolvePendingCashCustodyMovementAsync(Reject(movement.Id));
        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 1_000m));

        Assert.True(rejected.IsSuccess);
        Assert.Equal(CashCustodyMovementStatus.Rejected, rejected.Value!.Status);
        Assert.True(closed.IsSuccess);
        Assert.Equal(1_000m, closed.Value!.ExpectedClosingCashAmount);
        Assert.Equal(0, closed.Value.CompletedMovementCountAtClose);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InitiatingCashier_CannotRejectOwnPendingMovement()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m))).Value!;
        var request = Reject(movement.Id) with { AuthorizedActorId = "cashier-001" };

        var rejected = await service.ResolvePendingCashCustodyMovementAsync(request);

        Assert.False(rejected.IsSuccess);
        Assert.Equal(CashJournalErrorCode.DistinctPrincipalRequired, rejected.Error!.Code);
        var overview = await service.GetCashCustodyOverviewAsync(TestRequests.CashCustodyOverview());
        var readback = Assert.Single(overview.ActiveSession!.Movements!);
        Assert.Equal(CashCustodyMovementStatus.PendingAcknowledgment, readback.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentCashOutAcknowledgments_CannotMakeCustodyNegative()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var first = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 700m))).Value!;
        var second = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.DirectRemittance, 700m, idempotencyKey: "movement-002"))).Value!;

        var results = await Task.WhenAll(
            service.AcknowledgeCashCustodyMovementAsync(Acknowledge(first.Id)),
            service.AcknowledgeCashCustodyMovementAsync(Acknowledge(second.Id, idempotencyKey: "ack-002")));

        Assert.Single(results, result => result.IsSuccess);
        var failure = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.InsufficientCashCustody, failure.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentPartialReturns_CannotExceedOriginalOutboundAmount()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var outbound = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.SafeDrop, 300m))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(outbound.Id))).IsSuccess);
        var first = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.CashReturn, 200m, outbound.Id, "returner-001", "return-001"))).Value!;
        var second = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.CashReturn, 200m, outbound.Id, "returner-002", "return-002"))).Value!;

        var results = await Task.WhenAll(
            service.AcknowledgeCashCustodyMovementAsync(Acknowledge(first.Id, "returner-001", "receiver-002", "ack-return-001")),
            service.AcknowledgeCashCustodyMovementAsync(Acknowledge(second.Id, "returner-002", "receiver-003", "ack-return-002")));

        Assert.Single(results, result => result.IsSuccess);
        var failure = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.CashReturnLimitExceeded, failure.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task EqualTotalDifferentBreakdown_ConflictsWithInitiationIdempotency()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var request = Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m);
        var alternate = ReplaceBreakdown(request.Denominations, "PHP-100", 0, "PHP-20", 5);

        var first = await service.InitiateCashCustodyMovementAsync(request);
        var conflict = await service.InitiateCashCustodyMovementAsync(request with { Denominations = alternate });

        Assert.True(first.IsSuccess);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(CashJournalErrorCode.CashCustodyMovementConflict, conflict.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task AcknowledgmentIdempotencyKey_CannotBeReusedForAnotherMovement()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var first = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m))).Value!;
        var second = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m, idempotencyKey: "movement-002"))).Value!;

        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(first.Id))).IsSuccess);
        var conflict = await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(second.Id));

        Assert.False(conflict.IsSuccess);
        Assert.Equal(CashJournalErrorCode.CashCustodyMovementConflict, conflict.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CompletedMovementEvidence_IsImmutable_AndSurvivesRestart()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(movement.Id))).IsSuccess);

        await using (var context = database.CreateService().CreateDbContext())
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            {
                var entity = await context.CashCustodyMovements.SingleAsync(value => value.Id == movement.Id);
                entity.AmountMinorUnits = 1;
                await context.SaveChangesAsync();
            });
        }

        var overview = await database.CreateService().GetCashCustodyOverviewAsync(TestRequests.CashCustodyOverview());
        var persisted = Assert.Single(overview.ActiveSession!.Movements!);
        Assert.Equal(10_000, persisted.AmountMinorUnits);
        Assert.Equal(CashCustodyMovementStatus.Completed, persisted.Status);
        Assert.Equal(13, persisted.Denominations.Count);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task AppendOnlyCorrectionNote_HasNoAccountingPaymentOrFiscalEffect()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(
            Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(movement.Id))).IsSuccess);

        var noted = await service.AppendCashCustodyMovementNoteAsync(Note(movement.Id));
        var replay = await service.AppendCashCustodyMovementNoteAsync(Note(movement.Id));
        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(session.Id, 900m));
        await using var context = service.CreateDbContext();

        Assert.True(noted.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(3, noted.Value!.Events.Count);
        Assert.True(closed.IsSuccess);
        Assert.Equal(900m, closed.Value!.ExpectedClosingCashAmount);
        Assert.Empty(await context.CashTenders.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.TerminalCashPaymentOutboxCommands.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.TerminalCashFiscalOutboxCommands.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task Stage2AStyleClose_IsBlockedAfterCompletedMovement()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var movement = (await service.InitiateCashCustodyMovementAsync(Initiate(session.Id, CashCustodyMovementType.SafeDrop, 100m))).Value!;
        Assert.True((await service.AcknowledgeCashCustodyMovementAsync(Acknowledge(movement.Id))).IsSuccess);

        await using var context = database.CreateService().CreateDbContext();
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE cash_custody_sessions SET Status = 'Closed', ClosingCashAmount = 900 WHERE Id = {0}", session.Id));
        Assert.Contains("Stage 2B movement-aware closing is required", exception.Message);
    }

    private static InitiateCashCustodyMovementRequest Initiate(
        Guid sessionId,
        CashCustodyMovementType type,
        decimal amount,
        Guid? originalMovementId = null,
        string receiver = "receiver-001",
        string idempotencyKey = "movement-001") =>
        new(
            sessionId,
            type,
            originalMovementId,
            "cashier-001",
            "auth-session-001",
            "shift-001",
            "terminal-001",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-server-001",
            receiver,
            $"handover-{idempotencyKey}",
            idempotencyKey,
            $"correlation-{idempotencyKey}",
            TestRequests.CustodyDenominations(amount));

    private static AcknowledgeCashCustodyMovementRequest Acknowledge(
        Guid movementId,
        string receiverUsername = "receiver-001",
        string receiverId = "receiver-id-001",
        string idempotencyKey = "ack-001") =>
        new(
            movementId,
            "cashier-001",
            receiverId,
            receiverUsername,
            "receiver-session-001",
            "terminal-001",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-server-001",
            idempotencyKey,
            $"correlation-{idempotencyKey}");

    private static ResolvePendingCashCustodyMovementRequest Cancel(Guid movementId) =>
        new(
            movementId,
            CashCustodyMovementEventType.Cancelled,
            "cashier-001",
            "auth-session-001",
            "terminal-001",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-server-001",
            $"cancel-{movementId:D}",
            $"correlation-cancel-{movementId:D}",
            "HANDOVER_CANCELLED");

    private static ResolvePendingCashCustodyMovementRequest Reject(Guid movementId) =>
        new(
            movementId,
            CashCustodyMovementEventType.Rejected,
            "receiver-id-001",
            "receiver-session-001",
            "terminal-001",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-server-001",
            $"reject-{movementId:D}",
            $"correlation-reject-{movementId:D}",
            "HANDOVER_REJECTED");

    private static AppendCashCustodyMovementNoteRequest Note(Guid movementId) =>
        new(
            movementId,
            CashCustodyMovementEventType.CorrectionNoted,
            "supervisor-id-001",
            "supervisor-session-001",
            "terminal-001",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "pos-server-001",
            $"note-{movementId:D}",
            $"correlation-note-{movementId:D}",
            "REFERENCE_CLARIFICATION",
            "Counterparty spelling was clarified; financial evidence is unchanged.");

    private static IReadOnlyCollection<CashCustodyDenominationLine> ReplaceBreakdown(
        IReadOnlyCollection<CashCustodyDenominationLine> source,
        string firstCode,
        int firstQuantity,
        string secondCode,
        int secondQuantity) =>
        source.Select(line => line.DenominationCode switch
        {
            var code when code == firstCode => line with { Quantity = firstQuantity },
            var code when code == secondCode => line with { Quantity = secondQuantity },
            _ => line
        }).ToArray();
}
