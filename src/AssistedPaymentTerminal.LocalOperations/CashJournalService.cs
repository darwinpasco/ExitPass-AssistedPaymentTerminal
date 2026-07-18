using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashJournalService
{
    private static readonly CashTenderState[] UnresolvedTenderStates =
    [
        CashTenderState.TenderStarted,
        CashTenderState.CashReceived
    ];

    private readonly LocalOperationsDatabaseOptions _options;
    private readonly LocalOperationsDatabaseConfigurationException? _configurationError;

    public CashJournalService(LocalOperationsDatabaseOptions? options = null)
    {
        _options = options ?? new LocalOperationsDatabaseOptions();
        try
        {
            DatabasePath = LocalOperationsDatabasePath.Resolve(_options.DatabasePath);
        }
        catch (LocalOperationsDatabaseConfigurationException exception)
        {
            DatabasePath = _options.DatabasePath ?? string.Empty;
            _configurationError = exception;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DatabasePath = _options.DatabasePath ?? string.Empty;
            _configurationError = new LocalOperationsDatabaseConfigurationException(
                "APT_LOCAL_DB_PATH is not a valid local database path.");
        }
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_configurationError is not null)
        {
            throw _configurationError;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CashJournalResult<CashCustodySessionSnapshot>> CreateCashCustodySessionAsync(
        CreateCashCustodySessionRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var session = new CashCustodySession
        {
            Id = request.CashCustodySessionId ?? Guid.NewGuid(),
            CashierId = request.CashierId,
            AuthenticatedCashierSessionReference = request.AuthenticatedCashierSessionReference,
            CashierShiftId = request.CashierShiftId,
            TerminalId = request.TerminalId,
            SiteId = request.SiteId,
            SiteGroupId = request.SiteGroupId,
            PosServerId = request.PosServerId,
            OpeningCashAmount = request.OpeningCashAmount,
            OpenedAt = request.OpenedAt ?? DateTimeOffset.UtcNow,
            Status = CashCustodySessionStatus.Open
        };

        dbContext.CashCustodySessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(session));
    }

    public async Task<CashJournalResult<CashCustodySessionSnapshot>> CreateOrGetCashCustodySessionAsync(
        CreateCashCustodySessionRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var existing = await dbContext.CashCustodySessions
            .AsNoTracking()
            .Where(session => session.CashierId == request.CashierId)
            .Where(session => session.AuthenticatedCashierSessionReference == request.AuthenticatedCashierSessionReference)
            .Where(session => session.CashierShiftId == request.CashierShiftId)
            .Where(session => session.TerminalId == request.TerminalId)
            .Where(session => session.SiteId == request.SiteId)
            .Where(session => session.SiteGroupId == request.SiteGroupId)
            .Where(session => session.PosServerId == request.PosServerId)
            .Where(session => session.Status == CashCustodySessionStatus.Open)
            .OrderBy(session => session.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return existing is not null
            ? CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(existing))
            : await CreateCashCustodySessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CashJournalResult<CashTenderSnapshot>> StartCashTenderAsync(
        StartCashTenderRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var session = await dbContext.CashCustodySessions
            .SingleOrDefaultAsync(value => value.Id == request.CashCustodySessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.NotFound,
                $"Cash-custody session '{request.CashCustodySessionId}' was not found."));
        }

        var duplicate = await FindUnresolvedTenderAsync(dbContext, request.ParkingSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate is not null)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(DuplicateTenderError(duplicate));
        }

        var now = request.StartedAt ?? DateTimeOffset.UtcNow;
        var tender = new CashTender
        {
            Id = request.LocalCashTenderId ?? Guid.NewGuid(),
            CashCustodySessionId = request.CashCustodySessionId,
            ParkingSessionId = request.ParkingSessionId,
            TariffSnapshotId = request.TariffSnapshotId,
            Currency = request.Currency,
            AmountDue = request.AmountDue,
            AmountTendered = request.AmountTendered,
            ChangeDue = request.AmountTendered >= request.AmountDue ? request.AmountTendered - request.AmountDue : 0m,
            CorrelationId = request.CorrelationId,
            LocalIdempotencyIdentity = request.LocalIdempotencyIdentity,
            CurrentLocalState = CashTenderState.TenderStarted,
            CreatedAt = now,
            UpdatedAt = now
        };

        var startedEvent = new CashTenderEvent
        {
            Id = Guid.NewGuid(),
            CashTenderId = tender.Id,
            EventType = CashTenderEventType.TenderStarted,
            OccurredAt = now,
            AmountTendered = tender.AmountTendered,
            ChangeDue = tender.ChangeDue,
            CashierAttested = false,
            ActorCashierId = session.CashierId,
            CorrelationId = tender.CorrelationId
        };

        dbContext.CashTenders.Add(tender);
        dbContext.CashTenderEvents.Add(startedEvent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            duplicate = await FindUnresolvedTenderAsync(dbContext, request.ParkingSessionId, cancellationToken)
                .ConfigureAwait(false);

            if (duplicate is not null)
            {
                return CashJournalResult<CashTenderSnapshot>.Failure(DuplicateTenderError(duplicate));
            }

            throw;
        }

        return CashJournalResult<CashTenderSnapshot>.Success(CashTenderSnapshot.FromEntity(tender));
    }

    public async Task<CashJournalResult<CashTenderSnapshot>> CommitCashReceivedAsync(
        CommitCashReceivedRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.CashierAttested)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.CashierAttestationRequired,
                "CASH_RECEIVED requires explicit cashier attestation."));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var tender = await dbContext.CashTenders
            .Include(value => value.CashCustodySession)
            .SingleOrDefaultAsync(value => value.Id == request.LocalCashTenderId, cancellationToken)
            .ConfigureAwait(false);

        if (tender is null)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.NotFound,
                $"Cash tender '{request.LocalCashTenderId}' was not found."));
        }

        if (tender.CurrentLocalState != CashTenderState.TenderStarted)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                $"Cash tender '{tender.Id}' cannot transition from '{tender.CurrentLocalState}' to '{CashTenderState.CashReceived}'."));
        }

        if (tender.AmountTendered < tender.AmountDue)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.AmountTenderedBelowAmountDue,
                "Amount tendered must cover the amount due before CASH_RECEIVED can be committed."));
        }

        var now = request.ReceivedAt ?? DateTimeOffset.UtcNow;
        tender.ChangeDue = tender.AmountTendered - tender.AmountDue;
        tender.CurrentLocalState = CashTenderState.CashReceived;
        tender.UpdatedAt = now;

        var receivedEvent = new CashTenderEvent
        {
            Id = Guid.NewGuid(),
            CashTenderId = tender.Id,
            EventType = CashTenderEventType.CashReceived,
            OccurredAt = now,
            AmountTendered = tender.AmountTendered,
            ChangeDue = tender.ChangeDue,
            CashierAttested = true,
            ActorCashierId = tender.CashCustodySession!.CashierId,
            CorrelationId = tender.CorrelationId
        };

        dbContext.CashTenderEvents.Add(receivedEvent);

        if (request.SimulateOutboxCreationFailure)
        {
            throw new InvalidOperationException("Simulated outbox creation failure.");
        }

        foreach (var denomination in request.Denominations)
        {
            dbContext.CashDenominationEntries.Add(new CashDenominationEntry
            {
                Id = Guid.NewGuid(),
                CashTenderEventId = receivedEvent.Id,
                DenominationCode = denomination.DenominationCode,
                DenominationValue = denomination.DenominationValue,
                Quantity = denomination.Quantity,
                CreatedAt = now
            });
        }

        var existingOutbox = await dbContext.TerminalCashPaymentOutboxCommands
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == tender.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existingOutbox is null)
        {
            var payload = TerminalCashPaymentPayloadFactory.CreatePayload(
                tender,
                tender.CashCustodySession!,
                receivedEvent,
                request.Denominations);
            var payloadJson = TerminalCashPaymentPayloadFactory.Serialize(payload);

            dbContext.TerminalCashPaymentOutboxCommands.Add(new TerminalCashPaymentOutboxCommand
            {
                Id = Guid.NewGuid(),
                TerminalCashTenderId = tender.Id,
                CashCustodySessionId = tender.CashCustodySessionId,
                RequestPayloadJson = payloadJson,
                RequestPayloadHash = TerminalCashPaymentPayloadFactory.ComputeHash(payloadJson),
                IdempotencyKey = tender.LocalIdempotencyIdentity,
                OriginalCorrelationId = tender.CorrelationId,
                CentralPmsTarget = request.CentralPmsTarget,
                Status = TerminalCashPaymentCommandStatus.Pending,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CashJournalResult<CashTenderSnapshot>.Success(CashTenderSnapshot.FromEntity(tender));
    }

    public async Task<CashJournalResult<CashTenderSnapshot>> TryReturnToTenderStartedAsync(
        Guid localCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var tender = await dbContext.CashTenders
            .SingleOrDefaultAsync(value => value.Id == localCashTenderId, cancellationToken)
            .ConfigureAwait(false);

        if (tender is null)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.NotFound,
                $"Cash tender '{localCashTenderId}' was not found."));
        }

        if (tender.CurrentLocalState == CashTenderState.CashReceived)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "CASH_RECEIVED cannot be converted back to TENDER_STARTED through the application API."));
        }

        return CashJournalResult<CashTenderSnapshot>.Success(CashTenderSnapshot.FromEntity(tender));
    }

    public async Task<CashTenderSnapshot?> GetCashTenderAsync(
        Guid localCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var tender = await dbContext.CashTenders
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == localCashTenderId, cancellationToken)
            .ConfigureAwait(false);

        return tender is null ? null : CashTenderSnapshot.FromEntity(tender);
    }

    public async Task<CashTenderSnapshot?> GetCashTenderByParkingSessionAsync(
        string parkingSessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var tender = await dbContext.CashTenders
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ParkingSessionId == parkingSessionId, cancellationToken)
            .ConfigureAwait(false);

        return tender is null ? null : CashTenderSnapshot.FromEntity(tender);
    }

    public async Task<IReadOnlyList<CashTenderEventSnapshot>> GetCashTenderEventsAsync(
        Guid localCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.CashTenderEvents
            .AsNoTracking()
            .Include(value => value.DenominationEntries)
            .Where(value => value.CashTenderId == localCashTenderId)
            .OrderBy(value => value.OccurredAt)
            .Select(value => CashTenderEventSnapshot.FromEntity(value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalCashPaymentOutboxCommand?> GetTerminalCashPaymentOutboxCommandByTenderAsync(
        Guid terminalCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashPaymentOutboxCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalCashPaymentSubmissionAttempt>> GetTerminalCashPaymentAttemptsAsync(
        Guid localCommandId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashPaymentSubmissionAttempts
            .AsNoTracking()
            .Where(attempt => attempt.LocalCommandId == localCommandId)
            .OrderBy(attempt => attempt.AttemptSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public CashJournalDbContext CreateDbContext()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString();

        var options = new DbContextOptionsBuilder<CashJournalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new CashJournalDbContext(options);
    }

    private static async Task<CashTender?> FindUnresolvedTenderAsync(
        CashJournalDbContext dbContext,
        string parkingSessionId,
        CancellationToken cancellationToken)
    {
        return await dbContext.CashTenders
            .AsNoTracking()
            .Where(tender => tender.ParkingSessionId == parkingSessionId)
            .Where(tender => UnresolvedTenderStates.Contains(tender.CurrentLocalState))
            .OrderBy(tender => tender.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static CashJournalError DuplicateTenderError(CashTender tender) =>
        new(
            CashJournalErrorCode.DuplicateUnresolvedTender,
            $"Parking session '{tender.ParkingSessionId}' already has unresolved local cash tender '{tender.Id}'.",
            tender.Id,
            tender.CurrentLocalState);
}
