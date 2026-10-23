using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class CashCustodyDenominationTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public void CatalogueContainsTheCompleteAcceptedPhilippineDenominations()
    {
        Assert.Equal(
            [100_000, 50_000, 20_000, 10_000, 5_000, 2_000, 1_000, 500, 100, 25, 10, 5, 1],
            CashCustodyDenominationCatalog.Supported.Select(value => value.ValueMinorUnits));
        Assert.Contains(CashCustodyDenominationCatalog.Supported, value => value.Code == "PHP-200");
        Assert.Contains(CashCustodyDenominationCatalog.Supported, value => value.Code == "PHP-0.01");
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ZeroOpeningPersistsOneImmutableRowForEveryDenomination()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);

        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession(0m));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value!.OpeningCashAmount);
        Assert.Equal(13, result.Value.OpeningDenominations!.Count);
        Assert.All(result.Value.OpeningDenominations, entry =>
        {
            Assert.Equal(0, entry.Quantity);
            Assert.Equal(0, entry.CalculatedAmountMinorUnits);
        });

        await using var context = service.CreateDbContext();
        Assert.Equal(13, await context.CashCustodyDenominationEntries.CountAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task OpeningUsesIntegerMinorUnitsForBillsAndFractionalPesoCoins()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var lines = CashCustodyDenominationCatalog.CreateCount(new Dictionary<string, int>
        {
            ["PHP-200"] = 1,
            ["PHP-0.25"] = 1,
            ["PHP-0.10"] = 1,
            ["PHP-0.05"] = 1,
            ["PHP-0.01"] = 1
        });

        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession() with { Denominations = lines });

        Assert.True(result.IsSuccess);
        Assert.Equal(200.41m, result.Value!.OpeningCashAmount);
        Assert.Equal(20_041, result.Value.OpeningDenominations!.Sum(value => value.CalculatedAmountMinorUnits));
    }

    [Theory]
    [MemberData(nameof(InvalidBreakdowns))]
    [Trait("Category", "LocalOperations")]
    public async Task OpeningRejectsIncompleteDuplicateUnsupportedOrInvalidEvidence(
        IReadOnlyCollection<CashCustodyDenominationLine> lines)
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);

        var result = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession() with { Denominations = lines });

        Assert.False(result.IsSuccess);
        Assert.Equal(CashJournalErrorCode.InvalidDenominationEvidence, result.Error!.Code);
        await using var context = service.CreateDbContext();
        Assert.Equal(0, await context.CashCustodySessions.CountAsync());
        Assert.Equal(0, await context.CashCustodyDenominationEntries.CountAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task OpeningReplayRequiresTheExactImmutableBreakdown()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var id = Guid.NewGuid();
        var oneFiveHundred = WithQuantities(("PHP-500", 1));
        var fiveHundreds = WithQuantities(("PHP-100", 5));
        var request = TestRequests.CreateSession() with { CashCustodySessionId = id, Denominations = oneFiveHundred };

        var opened = await service.CreateCashCustodySessionAsync(request);
        var replay = await service.CreateCashCustodySessionAsync(request);
        var conflict = await service.CreateCashCustodySessionAsync(request with { Denominations = fiveHundreds });

        Assert.True(opened.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(id, replay.Value!.Id);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(CashJournalErrorCode.ConflictingDenominationEvidence, conflict.Error!.Code);
        await using var context = service.CreateDbContext();
        Assert.Equal(1, await context.CashCustodySessions.CountAsync());
        Assert.Equal(13, await context.CashCustodyDenominationEntries.CountAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentCreateOrGetRequestsCommitOneOpeningCount()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var request = TestRequests.CreateSession();

        var results = await Task.WhenAll(
            service.CreateOrGetCashCustodySessionAsync(request),
            service.CreateOrGetCashCustodySessionAsync(request));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Value!.Id, results[1].Value!.Id);
        await using var context = service.CreateDbContext();
        Assert.Equal(1, await context.CashCustodySessions.CountAsync());
        Assert.Equal(13, await context.CashCustodyDenominationEntries.CountAsync());
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ClosePersistsExactEvidenceAndRejectsAnEqualTotalDifferentBreakdown()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var oneFiveHundred = WithQuantities(("PHP-500", 1));
        var fiveHundreds = WithQuantities(("PHP-100", 5));
        var request = TestRequests.CloseSession(session.Id) with { Denominations = oneFiveHundred };

        var closed = await service.CloseCashCustodySessionAsync(request);
        var replay = await database.CreateService().CloseCashCustodySessionAsync(request);
        var conflict = await service.CloseCashCustodySessionAsync(request with { Denominations = fiveHundreds });

        Assert.True(closed.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(500m, closed.Value!.ClosingCashAmount);
        Assert.Equal(13, closed.Value.ClosingDenominations!.Count);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(CashJournalErrorCode.ConflictingDenominationEvidence, conflict.Error!.Code);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ConcurrentIdenticalCloseRequestsCommitOneClosingCount()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        var request = TestRequests.CloseSession(session.Id, 1_000.01m);

        var results = await Task.WhenAll(
            service.CloseCashCustodySessionAsync(request),
            service.CloseCashCustodySessionAsync(request));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        await using var context = service.CreateDbContext();
        Assert.Equal(
            13,
            await context.CashCustodyDenominationEntries.CountAsync(entry =>
                entry.CashCustodySessionId == session.Id
                && entry.CaptureType == CashCustodyDenominationCaptureType.Closing));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ExistingStageOneOpenCustodyCanCloseWithoutInventingOpeningEvidence()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var legacy = CreateLegacySession(CashCustodySessionStatus.Open);
        await using (var context = service.CreateDbContext())
        {
            context.CashCustodySessions.Add(legacy);
            await context.SaveChangesAsync();
        }

        var closed = await service.CloseCashCustodySessionAsync(TestRequests.CloseSession(legacy.Id, 1_000m));

        Assert.True(closed.IsSuccess);
        Assert.Empty(closed.Value!.OpeningDenominations!);
        Assert.Equal(13, closed.Value.ClosingDenominations!.Count);
        Assert.Equal(1_000m, closed.Value.OpeningCashAmount);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task HistoricalClosedCustodyWithoutDenominationsRemainsReadable()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var legacy = CreateLegacySession(CashCustodySessionStatus.Closed);
        legacy.ExpectedClosingCashAmount = 1_000m;
        legacy.ClosingCashAmount = 1_000m;
        legacy.VarianceAmount = 0m;
        legacy.ClosedAt = DateTimeOffset.Parse("2026-07-15T08:00:00Z");
        legacy.ClosedByAuthenticatedCashierSessionReference = "auth-session-close-001";
        await using (var context = service.CreateDbContext())
        {
            context.CashCustodySessions.Add(legacy);
            await context.SaveChangesAsync();
        }

        var overview = await service.GetCashCustodyOverviewAsync(TestRequests.CashCustodyOverview());

        var historical = Assert.Single(overview.ClosedSessions);
        Assert.Empty(historical.Session.OpeningDenominations!);
        Assert.Empty(historical.Session.ClosingDenominations!);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CommittedDenominationRowsRejectUpdateAndDelete()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        var session = await TestRequests.OpenShiftAndCreateSessionAsync(service);
        await using var context = service.CreateDbContext();

        var update = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE cash_custody_denomination_entries SET Quantity = Quantity + 1 WHERE CashCustodySessionId = {0};",
            session.Id));
        var delete = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "DELETE FROM cash_custody_denomination_entries WHERE CashCustodySessionId = {0};",
            session.Id));

        Assert.Contains("immutable", update.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("immutable", delete.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task EncryptedStartupReconcilesTheAdditiveTableWithoutChangingLegacyCustody()
    {
        using var database = TestDatabase.Create();
        var service = database.CreateService();
        Assert.True((await service.OpenCashierShiftAsync(TestRequests.OpenShift())).IsSuccess);
        var legacy = CreateLegacySession(CashCustodySessionStatus.Open);
        await using (var context = service.CreateDbContext())
        {
            context.CashCustodySessions.Add(legacy);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("DROP TABLE cash_custody_denomination_entries;");
        }

        var restarted = database.CreateService();
        await restarted.InitializeAsync();
        var state = await restarted.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());
        await using var reconciled = restarted.CreateDbContext();

        Assert.Equal(legacy.Id, state.ActiveCashCustodySession!.Id);
        Assert.Equal(1_000m, state.ActiveCashCustodySession.OpeningCashAmount);
        Assert.Empty(state.ActiveCashCustodySession.OpeningDenominations!);
        Assert.Equal(0, await reconciled.CashCustodyDenominationEntries.CountAsync());
    }

    public static IEnumerable<object[]> InvalidBreakdowns()
    {
        var valid = CashCustodyDenominationCatalog.CreateCount().ToArray();
        yield return [valid.Skip(1).ToArray()];
        yield return [valid.Take(12).Append(valid[0]).ToArray()];
        yield return [valid.Take(12).Append(new CashCustodyDenominationLine("PHP-2", 200, 1)).ToArray()];
        yield return [valid.Select((line, index) => index == 0 ? line with { Quantity = -1 } : line).ToArray()];
        yield return [valid.Select((line, index) => index == 0 ? line with { DenominationValueMinorUnits = 99_999 } : line).ToArray()];
    }

    private static IReadOnlyCollection<CashCustodyDenominationLine> WithQuantities(
        params (string Code, int Quantity)[] quantities) =>
        CashCustodyDenominationCatalog.CreateCount(
            quantities.ToDictionary(value => value.Code, value => value.Quantity, StringComparer.Ordinal));

    private static CashCustodySession CreateLegacySession(CashCustodySessionStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            CashierId = "cashier-001",
            AuthenticatedCashierSessionReference = "auth-session-001",
            CashierShiftId = "shift-001",
            TerminalId = "terminal-001",
            SiteId = "11111111-1111-4111-8111-111111111111",
            SiteGroupId = "22222222-2222-4222-8222-222222222222",
            PosServerId = "pos-server-001",
            OpeningCashAmount = 1_000m,
            OpenedAt = DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
            Status = status
        };
}
