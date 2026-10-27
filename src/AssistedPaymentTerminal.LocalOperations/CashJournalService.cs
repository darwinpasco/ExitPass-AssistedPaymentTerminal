using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed partial class CashJournalService
{
    private static readonly CashTenderState[] UnresolvedTenderStates =
    [
        CashTenderState.TenderStarted,
        CashTenderState.CashReceived
    ];

    private readonly LocalOperationsDatabaseOptions _options;
    private readonly LocalOperationsDatabaseConfigurationException? _configurationError;
    private readonly LocalDatabaseEncryptionManager? _encryptionManager;
    private readonly SemaphoreSlim _cashCustodyMutationLock = new(1, 1);

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

        if (_configurationError is null)
        {
            _encryptionManager = new LocalDatabaseEncryptionManager(
                DatabasePath,
                _options.DatabaseKeyProtector);
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
        await EnsureCashierShiftSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnsureCashCustodyClosureSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnsureCashCustodyDenominationSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnsureCashCustodyMovementSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnsureCashTenderStatutoryEvidenceSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnsureTerminalCashFiscalSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    public LocalPersistenceReadiness GetLocalPersistenceReadiness()
    {
        if (_configurationError is not null)
        {
            return new LocalPersistenceReadiness(
                EncryptionConfigured: true,
                DpapiScope: LocalDatabaseKeyEnvelope.CurrentUserScope,
                KeyEnvelopeExists: false,
                KeyAvailable: false,
                DatabaseExists: false,
                DatabaseEncrypted: false,
                LegacyPlaintextDetected: false,
                MigrationRequired: false,
                IntegrityValidated: false,
                SchemaReady: false,
                PersistenceReady: false,
                RecoveryAllowed: false,
                CashOperationsAllowed: false,
                SafeStatus: LocalPersistenceSafeStatus.ConfigurationInvalid,
                SafeAction: "The configured local operational database path is invalid.",
                DatabasePath: DatabasePath,
                KeyEnvelopePath: string.Empty);
        }

        return EncryptionManager.GetReadiness();
    }

    public async Task<CashJournalResult<CashierShiftSnapshot>> OpenCashierShiftAsync(
        OpenCashierShiftRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.CashierShifts
            .AsNoTracking()
            .SingleOrDefaultAsync(shift => shift.Id == request.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            var sameOwner = string.Equals(existing.CashierId, request.CashierId, StringComparison.Ordinal)
                && string.Equals(existing.TerminalId, request.TerminalId, StringComparison.Ordinal)
                && string.Equals(existing.SiteId, request.SiteId, StringComparison.Ordinal)
                && string.Equals(existing.SiteGroupId, request.SiteGroupId, StringComparison.Ordinal)
                && string.Equals(existing.PosServerId, request.PosServerId, StringComparison.Ordinal);
            return existing.Status == CashierShiftStatus.Open && sameOwner
                ? CashJournalResult<CashierShiftSnapshot>.Success(CashierShiftSnapshot.FromEntity(existing))
                : CashJournalResult<CashierShiftSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.InvalidStateTransition,
                    "The cashier shift cannot be opened or inherited in its current state."));
        }

        var shift = new CashierShift
        {
            Id = request.CashierShiftId,
            CashierId = request.CashierId,
            AuthenticatedCashierSessionReference = request.AuthenticatedCashierSessionReference,
            TerminalId = request.TerminalId,
            SiteId = request.SiteId,
            SiteGroupId = request.SiteGroupId,
            PosServerId = request.PosServerId,
            OpenedAt = request.OpenedAt ?? DateTimeOffset.UtcNow,
            Status = CashierShiftStatus.Open
        };

        dbContext.CashierShifts.Add(shift);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CashJournalResult<CashierShiftSnapshot>.Success(CashierShiftSnapshot.FromEntity(shift));
    }

    public async Task<CashJournalResult<CashierShiftSnapshot>> CloseCashierShiftAsync(
        CloseCashierShiftRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var shift = await dbContext.CashierShifts
            .SingleOrDefaultAsync(value => value.Id == request.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return CashJournalResult<CashierShiftSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.NotFound,
                $"Cashier shift '{request.CashierShiftId}' was not found."));
        }

        var sameOwnerAndScope =
            string.Equals(shift.CashierId, request.CashierId, StringComparison.Ordinal)
            && string.Equals(shift.TerminalId, request.TerminalId, StringComparison.Ordinal)
            && string.Equals(shift.SiteId, request.SiteId, StringComparison.Ordinal)
            && string.Equals(shift.SiteGroupId, request.SiteGroupId, StringComparison.Ordinal)
            && string.Equals(shift.PosServerId, request.PosServerId, StringComparison.Ordinal);
        if (!sameOwnerAndScope)
        {
            return CashJournalResult<CashierShiftSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "Only the authenticated owning cashier may close this shift in its original terminal scope."));
        }

        if (shift.Status == CashierShiftStatus.Closed)
        {
            return CashJournalResult<CashierShiftSnapshot>.Success(CashierShiftSnapshot.FromEntity(shift));
        }

        var openCustodyExists = await dbContext.CashCustodySessions
            .AsNoTracking()
            .AnyAsync(session => session.CashierShiftId == shift.Id && session.Status == CashCustodySessionStatus.Open, cancellationToken)
            .ConfigureAwait(false);
        if (openCustodyExists)
        {
            return CashJournalResult<CashierShiftSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "A cashier shift cannot close while physical cash custody remains open."));
        }

        shift.Status = CashierShiftStatus.Closed;
        shift.ClosedAt = request.ClosedAt ?? DateTimeOffset.UtcNow;
        shift.ClosedByAuthenticatedCashierSessionReference = request.AuthenticatedCashierSessionReference;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return CashJournalResult<CashierShiftSnapshot>.Success(CashierShiftSnapshot.FromEntity(shift));
    }

    public async Task<LocalOperationalStateSnapshot> GetLocalOperationalStateAsync(
        LocalOperationalStateRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();

        var shiftQuery = ApplyShiftScope(dbContext.CashierShifts.AsNoTracking(), request)
            .Where(shift => shift.Status == CashierShiftStatus.Open);
        var activeShiftIds = await shiftQuery
            .Select(shift => shift.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var sessionQuery = ApplyCustodyScope(dbContext.CashCustodySessions.AsNoTracking(), request)
            .Where(session => session.Status == CashCustodySessionStatus.Open && activeShiftIds.Contains(session.CashierShiftId));

        var activeShiftCount = activeShiftIds.Length;
        var activeCustodyCount = await sessionQuery.CountAsync(cancellationToken).ConfigureAwait(false);
        var activeShift = await shiftQuery
            .OrderBy(shift => shift.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeSession = await sessionQuery
            .Include(session => session.DenominationEntries)
            .OrderBy(session => session.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new LocalOperationalStateSnapshot(
            activeShiftCount,
            activeCustodyCount,
            activeShift is null ? null : CashierShiftSnapshot.FromEntity(activeShift),
            activeSession is null ? null : CashCustodySessionSnapshot.FromEntity(activeSession));
    }

    public async Task<CashCustodyOverviewSnapshot> GetCashCustodyOverviewAsync(
        CashCustodyOverviewRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var sessions = await dbContext.CashCustodySessions
            .AsNoTracking()
            .Include(session => session.DenominationEntries)
            .Include(session => session.Movements)
                .ThenInclude(movement => movement.Denominations)
            .Include(session => session.Movements)
                .ThenInclude(movement => movement.Events)
            .Where(session => session.CashierId == request.CashierId)
            .Where(session => session.TerminalId == request.TerminalId)
            .Where(session => session.SiteId == request.SiteId)
            .Where(session => session.SiteGroupId == request.SiteGroupId)
            .Where(session => session.PosServerId == request.PosServerId)
            .OrderByDescending(session => session.ClosedAt ?? session.OpenedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var summaries = new List<CashCustodySummarySnapshot>(sessions.Count);
        foreach (var session in sessions)
        {
            summaries.Add(await BuildCashCustodySummaryAsync(dbContext, session, cancellationToken).ConfigureAwait(false));
        }

        return new CashCustodyOverviewSnapshot(
            summaries.FirstOrDefault(summary => summary.Session.Status == CashCustodySessionStatus.Open),
            summaries.Where(summary => summary.Session.Status == CashCustodySessionStatus.Closed).ToArray());
    }

    public async Task<CashJournalResult<CashCustodySessionSnapshot>> CreateCashCustodySessionAsync(
        CreateCashCustodySessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCustodyDenominations(request.Denominations);
        if (!normalized.IsSuccess)
        {
            return CashJournalResult<CashCustodySessionSnapshot>.Failure(normalized.Error!);
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateCashCustodySessionCoreAsync(request, normalized.Value!, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    public async Task<CashJournalResult<CashCustodySessionSnapshot>> CreateOrGetCashCustodySessionAsync(
        CreateCashCustodySessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCustodyDenominations(request.Denominations);
        if (!normalized.IsSuccess)
        {
            return CashJournalResult<CashCustodySessionSnapshot>.Failure(normalized.Error!);
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            await using var dbContext = CreateDbContext();
            var existing = await dbContext.CashCustodySessions
                .AsNoTracking()
                .Include(session => session.DenominationEntries)
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

            if (existing is null)
            {
                return await CreateCashCustodySessionCoreAsync(request, normalized.Value!, cancellationToken)
                    .ConfigureAwait(false);
            }

            var openingEvidence = existing.DenominationEntries
                .Where(entry => entry.CaptureType == CashCustodyDenominationCaptureType.Opening)
                .ToArray();
            if (openingEvidence.Length == 0)
            {
                return CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(existing));
            }

            return EvidenceMatches(openingEvidence, normalized.Value!.Lines)
                ? CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(existing))
                : CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.ConflictingDenominationEvidence,
                    "The existing cash-custody opening count does not match this request."));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    private async Task<CashJournalResult<CashCustodySessionSnapshot>> CreateCashCustodySessionCoreAsync(
        CreateCashCustodySessionRequest request,
        NormalizedCustodyDenominations normalized,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (request.CashCustodySessionId is Guid requestedId)
        {
            var existingById = await dbContext.CashCustodySessions
                .AsNoTracking()
                .Include(session => session.DenominationEntries)
                .SingleOrDefaultAsync(session => session.Id == requestedId, cancellationToken)
                .ConfigureAwait(false);
            if (existingById is not null)
            {
                return OpeningReplayResult(existingById, request, normalized);
            }
        }

        var shift = await dbContext.CashierShifts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == request.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);
        if (shift is null
            || shift.Status != CashierShiftStatus.Open
            || !string.Equals(shift.CashierId, request.CashierId, StringComparison.Ordinal)
            || !string.Equals(shift.TerminalId, request.TerminalId, StringComparison.Ordinal)
            || !string.Equals(shift.SiteId, request.SiteId, StringComparison.Ordinal)
            || !string.Equals(shift.SiteGroupId, request.SiteGroupId, StringComparison.Ordinal)
            || !string.Equals(shift.PosServerId, request.PosServerId, StringComparison.Ordinal))
        {
            return CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "Cash custody requires the authenticated cashier's own active shift and terminal scope."));
        }

        var openedAt = request.OpenedAt ?? DateTimeOffset.UtcNow;
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
            OpeningCashAmount = MinorUnitsToAmount(normalized.TotalMinorUnits),
            OpenedAt = openedAt,
            Status = CashCustodySessionStatus.Open
        };
        AddDenominationEvidence(session, CashCustodyDenominationCaptureType.Opening, normalized.Lines, openedAt);

        dbContext.CashCustodySessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(session));
    }

    public async Task<CashJournalResult<CashCustodySessionSnapshot>> CloseCashCustodySessionAsync(
        CloseCashCustodySessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCustodyDenominations(request.Denominations);
        if (!normalized.IsSuccess)
        {
            return CashJournalResult<CashCustodySessionSnapshot>.Failure(normalized.Error!);
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            await using var dbContext = CreateDbContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var session = await dbContext.CashCustodySessions
                .Include(value => value.DenominationEntries)
                .SingleOrDefaultAsync(value => value.Id == request.CashCustodySessionId, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                return CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.NotFound,
                    "The cash-custody session was not found."));
            }

            var sameOwnerAndScope =
                string.Equals(session.CashierId, request.CashierId, StringComparison.Ordinal)
                && string.Equals(session.CashierShiftId, request.CashierShiftId, StringComparison.Ordinal)
                && string.Equals(session.TerminalId, request.TerminalId, StringComparison.Ordinal)
                && string.Equals(session.SiteId, request.SiteId, StringComparison.Ordinal)
                && string.Equals(session.SiteGroupId, request.SiteGroupId, StringComparison.Ordinal)
                && string.Equals(session.PosServerId, request.PosServerId, StringComparison.Ordinal);
            if (!sameOwnerAndScope)
            {
                return CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.InvalidStateTransition,
                    "Only the authenticated owning cashier may close this cash custody in its original terminal scope."));
            }

            if (session.Status == CashCustodySessionStatus.Closed)
            {
                var committedClosingEvidence = session.DenominationEntries
                    .Where(entry => entry.CaptureType == CashCustodyDenominationCaptureType.Closing)
                    .ToArray();
                return committedClosingEvidence.Length > 0
                    && EvidenceMatches(committedClosingEvidence, normalized.Value!.Lines)
                    ? CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(session))
                    : CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                        CashJournalErrorCode.ConflictingDenominationEvidence,
                        "Closed cash-custody evidence is immutable."));
            }

            var shift = await dbContext.CashierShifts
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == session.CashierShiftId, cancellationToken)
                .ConfigureAwait(false);
            if (shift is null
                || shift.Status != CashierShiftStatus.Open
                || !string.Equals(shift.CashierId, request.CashierId, StringComparison.Ordinal))
            {
                return CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.InvalidStateTransition,
                    "Cash custody can close only under the owning cashier's open shift."));
            }

            var movements = await dbContext.CashCustodyMovements
                .AsNoTracking()
                .Include(movement => movement.Events)
                .Where(movement => movement.CashCustodySessionId == session.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (movements.Any(movement => ResolveMovementStatus(movement.Events) == CashCustodyMovementStatus.PendingAcknowledgment))
            {
                return CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.PendingCashCustodyMovement,
                    "Resolve pending cash-custody movements before closing custody."));
            }

            var completedMovements = movements
                .Where(movement => ResolveMovementStatus(movement.Events) == CashCustodyMovementStatus.Completed)
                .ToArray();
            var completedCashInMinorUnits = completedMovements
                .Where(movement => movement.MovementType == CashCustodyMovementType.CashReturn)
                .Sum(movement => movement.AmountMinorUnits);
            var completedCashOutMinorUnits = completedMovements
                .Where(movement => movement.MovementType is CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
                .Sum(movement => movement.AmountMinorUnits);

            var receivedCashAmounts = await dbContext.CashTenders
                .AsNoTracking()
                .Where(tender => tender.CashCustodySessionId == session.Id)
                .Where(tender => tender.CurrentLocalState == CashTenderState.CashReceived)
                .Select(tender => tender.AmountDue)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var receivedCash = receivedCashAmounts.Sum();
            var expectedClosingCash = session.OpeningCashAmount
                + receivedCash
                + MinorUnitsToAmount(completedCashInMinorUnits)
                - MinorUnitsToAmount(completedCashOutMinorUnits);
            var submissionSummary = await BuildCashCustodySubmissionSummaryAsync(dbContext, session, cancellationToken)
                .ConfigureAwait(false);
            var closingAmount = MinorUnitsToAmount(normalized.Value!.TotalMinorUnits);
            var closedAt = request.ClosedAt ?? DateTimeOffset.UtcNow;

            session.ExpectedClosingCashAmount = expectedClosingCash;
            session.ClosingCashAmount = closingAmount;
            session.VarianceAmount = closingAmount - expectedClosingCash;
            session.UnresolvedSubmissionCountAtClose = submissionSummary.UnresolvedSubmissionCount;
            session.CompletedMovementCountAtClose = completedMovements.Length;
            session.CompletedCashInMinorUnitsAtClose = completedCashInMinorUnits;
            session.CompletedCashOutMinorUnitsAtClose = completedCashOutMinorUnits;
            session.CompletedMovementReferencesAtCloseJson = System.Text.Json.JsonSerializer.Serialize(
                completedMovements.OrderBy(movement => movement.CreatedAt).Select(movement => movement.Id));
            session.ClosedAt = closedAt;
            session.ClosedByAuthenticatedCashierSessionReference = request.AuthenticatedCashierSessionReference;
            session.Status = CashCustodySessionStatus.Closed;
            var closingEvidence = AddDenominationEvidence(
                session,
                CashCustodyDenominationCaptureType.Closing,
                normalized.Value.Lines,
                closedAt);
            dbContext.CashCustodyDenominationEntries.AddRange(closingEvidence);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(session));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
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

        var activeShift = await dbContext.CashierShifts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == session.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);
        if (session.Status != CashCustodySessionStatus.Open
            || activeShift is null
            || activeShift.Status != CashierShiftStatus.Open
            || !string.Equals(activeShift.CashierId, session.CashierId, StringComparison.Ordinal))
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "Cash tender requires the cashier's own active shift and cash custody."));
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

        var tenderCustody = tender.CashCustodySession;
        if (tenderCustody is null)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "CASH_RECEIVED requires durable cash-custody ownership."));
        }

        var activeShiftForCustody = await dbContext.CashierShifts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == tenderCustody.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);
        if (tenderCustody.Status != CashCustodySessionStatus.Open
            || activeShiftForCustody is null
            || activeShiftForCustody.Status != CashierShiftStatus.Open
            || !string.Equals(activeShiftForCustody.CashierId, tenderCustody.CashierId, StringComparison.Ordinal))
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.InvalidStateTransition,
                "CASH_RECEIVED requires the cashier's own active shift and cash custody."));
        }

        if (tender.AmountTendered < tender.AmountDue)
        {
            return CashJournalResult<CashTenderSnapshot>.Failure(new CashJournalError(
                CashJournalErrorCode.AmountTenderedBelowAmountDue,
                "Amount tendered must cover the amount due before CASH_RECEIVED can be committed."));
        }

        var now = request.ReceivedAt ?? DateTimeOffset.UtcNow;
        ApplyStatutoryTenderEvidence(tender, request.StatutoryTenderEvidence, now);
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


    public async Task<PayableBasisStateSnapshot> SavePayableBasisStateAsync(
        SavePayableBasisStateRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await EnsurePayableBasisStateSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var now = request.RecordedAt ?? DateTimeOffset.UtcNow;
        var state = await dbContext.TerminalCashPayableBasisStates
            .SingleOrDefaultAsync(value => value.LocalWorkflowId == request.LocalWorkflowId, cancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            state = new TerminalCashPayableBasisState
            {
                Id = Guid.NewGuid(),
                LocalWorkflowId = request.LocalWorkflowId,
                ResolvedAt = now
            };
            dbContext.TerminalCashPayableBasisStates.Add(state);
        }

        state.LookupReferenceType = request.LookupReferenceType;
        state.LookupReferenceValue = request.LookupReferenceValue;
        state.ParkingSessionId = request.ParkingSessionId;
        state.TariffSnapshotId = request.TariffSnapshotId;
        state.SiteId = request.SiteId;
        state.SiteGroupId = request.SiteGroupId;
        state.SitePosServerId = request.SitePosServerId;
        state.TerminalId = request.TerminalId;
        state.AuthoritativeAmountMinorUnits = request.AuthoritativeAmountMinorUnits;
        state.Currency = request.Currency;
        state.TariffCalculatedAt = request.TariffCalculatedAt;
        state.TariffValidUntil = request.TariffValidUntil;
        state.FeeValidUntil = request.FeeValidUntil;
        state.ParkingStatus = request.ParkingStatus;
        state.PaymentStatus = request.PaymentStatus;
        state.SessionReadiness = request.SessionReadiness;
        state.TariffReadiness = request.TariffReadiness;
        state.PaymentEligibility = request.PaymentEligibility;
        state.TerminalCashAvailability = request.TerminalCashAvailability;
        state.FiscalReadiness = request.FiscalReadiness;
        state.SalesInvoiceConfigurationReadiness = request.SalesInvoiceConfigurationReadiness;
        state.CashAcceptanceReadiness = request.CashAcceptanceReadiness;
        state.ReadyForCashAcceptance = request.ReadyForCashAcceptance;
        state.BlockingReasonCodesJson = System.Text.Json.JsonSerializer.Serialize(request.BlockingReasonCodes);
        state.Retryable = request.Retryable;
        state.SafeUserFacingClassification = request.SafeUserFacingClassification;
        state.CentralPmsCorrelationId = request.CentralPmsCorrelationId;
        state.RevalidationOutcome = request.RevalidationOutcome;
        state.CashierAcknowledgementRequired = request.CashierAcknowledgementRequired;
        state.AmountChanged = request.AmountChanged;
        state.PriorDisplayedAmountMinorUnits = request.PriorDisplayedAmountMinorUnits;
        state.StatutoryDiscountStateJson = request.StatutoryDiscountStateJson;
        state.LastRevalidatedAt = string.IsNullOrWhiteSpace(request.RevalidationOutcome) ? state.LastRevalidatedAt : now;
        state.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return PayableBasisStateSnapshot.FromEntity(state);
    }

    public async Task<PayableBasisStateSnapshot?> GetLatestPayableBasisStateAsync(
        string terminalId,
        string siteId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await EnsurePayableBasisStateSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var state = await dbContext.TerminalCashPayableBasisStates
            .AsNoTracking()
            .Where(value => value.TerminalId == terminalId)
            .Where(value => value.SiteId == siteId)
            .OrderByDescending(value => value.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return state is null ? null : PayableBasisStateSnapshot.FromEntity(state);
    }

    private static async Task EnsurePayableBasisStateSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            @"CREATE TABLE IF NOT EXISTS terminal_cash_payable_basis_states (
                Id TEXT NOT NULL CONSTRAINT PK_terminal_cash_payable_basis_states PRIMARY KEY,
                LocalWorkflowId TEXT NOT NULL,
                LookupReferenceType TEXT NOT NULL,
                LookupReferenceValue TEXT NOT NULL,
                ParkingSessionId TEXT NOT NULL,
                TariffSnapshotId TEXT NOT NULL,
                SiteId TEXT NOT NULL,
                SiteGroupId TEXT NOT NULL,
                SitePosServerId TEXT NULL,
                TerminalId TEXT NOT NULL,
                AuthoritativeAmountMinorUnits INTEGER NOT NULL,
                Currency TEXT NOT NULL,
                TariffCalculatedAt INTEGER NULL,
                TariffValidUntil INTEGER NOT NULL,
                FeeValidUntil INTEGER NULL,
                ParkingStatus TEXT NOT NULL,
                PaymentStatus TEXT NOT NULL,
                SessionReadiness TEXT NULL,
                TariffReadiness TEXT NULL,
                PaymentEligibility TEXT NULL,
                TerminalCashAvailability TEXT NULL,
                FiscalReadiness TEXT NULL,
                SalesInvoiceConfigurationReadiness TEXT NULL,
                CashAcceptanceReadiness TEXT NULL,
                ReadyForCashAcceptance INTEGER NOT NULL,
                BlockingReasonCodesJson TEXT NOT NULL,
                Retryable INTEGER NOT NULL,
                SafeUserFacingClassification TEXT NOT NULL,
                CentralPmsCorrelationId TEXT NOT NULL,
                RevalidationOutcome TEXT NULL,
                CashierAcknowledgementRequired INTEGER NOT NULL,
                AmountChanged INTEGER NOT NULL,
                PriorDisplayedAmountMinorUnits INTEGER NULL,
                StatutoryDiscountStateJson TEXT NULL,
                ResolvedAt INTEGER NOT NULL,
                LastRevalidatedAt INTEGER NULL,
                UpdatedAt INTEGER NOT NULL
            );",
            cancellationToken).ConfigureAwait(false);

        await AddColumnIfMissingAsync(dbContext, "terminal_cash_payable_basis_states", "StatutoryDiscountStateJson", "TEXT NULL", cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_terminal_cash_payable_basis_states_LocalWorkflowId ON terminal_cash_payable_basis_states (LocalWorkflowId);",
            cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_terminal_cash_payable_basis_states_TerminalId_SiteId_UpdatedAt ON terminal_cash_payable_basis_states (TerminalId, SiteId, UpdatedAt);",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCashTenderStatutoryEvidenceSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryDiscountDecisionCommandId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryDiscountPayableBasisApplicationCommandId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryDiscountValidationId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryOriginalTariffSnapshotId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryAppliedTariffSnapshotId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryOriginalAmountMinorUnits", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryFinalAmountMinorUnits", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryCurrency", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryAmountAcknowledged", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryAmountAcknowledgedAt", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryImmediateRevalidationOutcome", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryImmediateRevalidatedAt", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryCorrelationId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryReadinessStatus", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_tenders", "StatutoryReadinessAction", "TEXT NULL", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCashierShiftSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cashier_shifts (
                Id TEXT NOT NULL CONSTRAINT PK_cashier_shifts PRIMARY KEY,
                CashierId TEXT NOT NULL,
                AuthenticatedCashierSessionReference TEXT NOT NULL,
                TerminalId TEXT NOT NULL,
                SiteId TEXT NOT NULL,
                SiteGroupId TEXT NOT NULL,
                PosServerId TEXT NOT NULL,
                OpenedAt INTEGER NOT NULL,
                ClosedAt INTEGER NULL,
                Status TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_cashier_shifts_TerminalId_CashierId_Status
            ON cashier_shifts (TerminalId, CashierId, Status);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await AddColumnIfMissingAsync(
            dbContext,
            "cashier_shifts",
            "ClosedByAuthenticatedCashierSessionReference",
            "TEXT NULL",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCashCustodyClosureSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "ExpectedClosingCashAmount", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "ClosingCashAmount", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "VarianceAmount", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "UnresolvedSubmissionCountAtClose", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "ClosedAt", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            dbContext,
            "cash_custody_sessions",
            "ClosedByAuthenticatedCashierSessionReference",
            "TEXT NULL",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCashCustodyDenominationSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS cash_custody_denomination_entries (
                Id TEXT NOT NULL CONSTRAINT PK_cash_custody_denomination_entries PRIMARY KEY,
                CashCustodySessionId TEXT NOT NULL,
                CaptureType TEXT NOT NULL,
                DenominationCode TEXT NOT NULL,
                DenominationValueMinorUnits INTEGER NOT NULL,
                Quantity INTEGER NOT NULL,
                CalculatedAmountMinorUnits INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_cash_custody_denomination_entries_cash_custody_sessions_CashCustodySessionId
                    FOREIGN KEY (CashCustodySessionId) REFERENCES cash_custody_sessions (Id) ON DELETE RESTRICT,
                CONSTRAINT CK_cash_custody_denomination_entries_CaptureType CHECK (CaptureType IN ('Opening', 'Closing')),
                CONSTRAINT CK_cash_custody_denomination_entries_Code CHECK (length(DenominationCode) > 0),
                CONSTRAINT CK_cash_custody_denomination_entries_Value CHECK (DenominationValueMinorUnits > 0),
                CONSTRAINT CK_cash_custody_denomination_entries_Quantity CHECK (Quantity >= 0),
                CONSTRAINT CK_cash_custody_denomination_entries_CalculatedAmount
                    CHECK (CalculatedAmountMinorUnits = DenominationValueMinorUnits * Quantity)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cash_custody_denomination_entries_Session_Capture_Code
                ON cash_custody_denomination_entries (CashCustodySessionId, CaptureType, DenominationCode);
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_denomination_entries_immutable_update
                BEFORE UPDATE ON cash_custody_denomination_entries
                BEGIN SELECT RAISE(ABORT, 'cash custody denomination evidence is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_denomination_entries_immutable_delete
                BEFORE DELETE ON cash_custody_denomination_entries
                BEGIN SELECT RAISE(ABORT, 'cash custody denomination evidence is immutable'); END;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CashCustodySummarySnapshot> BuildCashCustodySummaryAsync(
        CashJournalDbContext dbContext,
        CashCustodySession session,
        CancellationToken cancellationToken)
    {
        var submissions = await BuildCashCustodySubmissionSummaryAsync(dbContext, session, cancellationToken)
            .ConfigureAwait(false);
        var accountability = await BuildCashierAccountabilityActivityAsync(dbContext, session, cancellationToken)
            .ConfigureAwait(false);

        if (session.Status == CashCustodySessionStatus.Closed
            && session.UnresolvedSubmissionCountAtClose is int unresolvedAtClose)
        {
            submissions = submissions with { UnresolvedSubmissionCount = unresolvedAtClose };
        }

        return new CashCustodySummarySnapshot(
            CashCustodySessionSnapshot.FromEntity(session),
            submissions,
            session.Movements
                .OrderByDescending(movement => movement.CreatedAt)
                .Select(ToMovementSnapshot)
                .ToArray(),
            accountability);
    }

    private static async Task<CashierAccountabilityActivitySnapshot> BuildCashierAccountabilityActivityAsync(
        CashJournalDbContext dbContext,
        CashCustodySession session,
        CancellationToken cancellationToken)
    {
        var cashReceivedTenders = await dbContext.CashTenders
            .AsNoTracking()
            .Where(tender => tender.CashCustodySessionId == session.Id)
            .Where(tender => tender.CurrentLocalState == CashTenderState.CashReceived)
            .Select(tender => new { tender.Id, tender.AmountDue })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var cashReceivedTenderIds = cashReceivedTenders.Select(tender => tender.Id).ToArray();
        var confirmedTenderIds = cashReceivedTenderIds.Length == 0
            ? []
            : await dbContext.TerminalCashPaymentOutboxCommands
                .AsNoTracking()
                .Where(command => cashReceivedTenderIds.Contains(command.TerminalCashTenderId))
                .Where(command => command.Status == TerminalCashPaymentCommandStatus.Confirmed)
                .Select(command => command.TerminalCashTenderId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        var confirmedTenderIdSet = confirmedTenderIds.ToHashSet();
        var completedMovements = session.Movements
            .Where(movement => ResolveMovementStatus(movement.Events) == CashCustodyMovementStatus.Completed)
            .ToArray();

        return new CashierAccountabilityActivitySnapshot(
            SumMinorUnits(cashReceivedTenders.Select(tender => tender.AmountDue)),
            SumMinorUnits(cashReceivedTenders
                .Where(tender => confirmedTenderIdSet.Contains(tender.Id))
                .Select(tender => tender.AmountDue)),
            SumMovementMinorUnits(completedMovements, CashCustodyMovementType.CashReturn),
            SumMovementMinorUnits(completedMovements, CashCustodyMovementType.SafeDrop),
            SumMovementMinorUnits(completedMovements, CashCustodyMovementType.DirectRemittance));
    }

    private static long SumMinorUnits(IEnumerable<decimal> amounts) =>
        amounts.Aggregate(0L, (sum, amount) => checked(sum + AmountToMinorUnits(amount)));

    private static long SumMovementMinorUnits(
        IEnumerable<CashCustodyMovement> movements,
        CashCustodyMovementType movementType) =>
        movements
            .Where(movement => movement.MovementType == movementType)
            .Aggregate(0L, (sum, movement) => checked(sum + movement.AmountMinorUnits));

    private static async Task<CashCustodySubmissionSummarySnapshot> BuildCashCustodySubmissionSummaryAsync(
        CashJournalDbContext dbContext,
        CashCustodySession session,
        CancellationToken cancellationToken)
    {
        var cashReceivedTenderIds = await dbContext.CashTenders
            .AsNoTracking()
            .Where(tender => tender.CashCustodySessionId == session.Id)
            .Where(tender => tender.CurrentLocalState == CashTenderState.CashReceived)
            .Select(tender => tender.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cashReceivedTenderIds.Length == 0)
        {
            return new CashCustodySubmissionSummarySnapshot(0, 0, 0, 0, 0, 0, 0);
        }

        var paymentCommands = await dbContext.TerminalCashPaymentOutboxCommands
            .AsNoTracking()
            .Where(command => cashReceivedTenderIds.Contains(command.TerminalCashTenderId))
            .Select(command => new { command.TerminalCashTenderId, command.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var paymentStatusByTender = paymentCommands.ToDictionary(command => command.TerminalCashTenderId, command => command.Status);

        var confirmedTenderIds = cashReceivedTenderIds
            .Where(tenderId => paymentStatusByTender.GetValueOrDefault(tenderId) == TerminalCashPaymentCommandStatus.Confirmed)
            .ToArray();
        var conflictingCount = cashReceivedTenderIds.Count(tenderId =>
            paymentStatusByTender.GetValueOrDefault(tenderId) == TerminalCashPaymentCommandStatus.Conflict);
        var rejectedCount = cashReceivedTenderIds.Count(tenderId =>
            paymentStatusByTender.GetValueOrDefault(tenderId) == TerminalCashPaymentCommandStatus.Rejected);
        var pendingCount = cashReceivedTenderIds.Length - confirmedTenderIds.Length - conflictingCount - rejectedCount;

        var fiscalCommands = await dbContext.TerminalCashFiscalOutboxCommands
            .AsNoTracking()
            .Where(command => confirmedTenderIds.Contains(command.TerminalCashTenderId))
            .Select(command => new { command.TerminalCashTenderId, command.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var fiscalStatusByTender = fiscalCommands.ToDictionary(command => command.TerminalCashTenderId, command => command.Status);
        var pendingFiscalCount = confirmedTenderIds.Count(tenderId =>
            fiscalStatusByTender.GetValueOrDefault(tenderId) != TerminalCashFiscalCommandStatus.Recorded);

        return new CashCustodySubmissionSummarySnapshot(
            cashReceivedTenderIds.Length,
            confirmedTenderIds.Length,
            pendingCount,
            conflictingCount,
            rejectedCount,
            pendingCount + conflictingCount + rejectedCount,
            pendingFiscalCount);
    }

    private static CashJournalResult<CashCustodySessionSnapshot> OpeningReplayResult(
        CashCustodySession existing,
        CreateCashCustodySessionRequest request,
        NormalizedCustodyDenominations normalized)
    {
        var sameScope = string.Equals(existing.CashierId, request.CashierId, StringComparison.Ordinal)
            && string.Equals(existing.AuthenticatedCashierSessionReference, request.AuthenticatedCashierSessionReference, StringComparison.Ordinal)
            && string.Equals(existing.CashierShiftId, request.CashierShiftId, StringComparison.Ordinal)
            && string.Equals(existing.TerminalId, request.TerminalId, StringComparison.Ordinal)
            && string.Equals(existing.SiteId, request.SiteId, StringComparison.Ordinal)
            && string.Equals(existing.SiteGroupId, request.SiteGroupId, StringComparison.Ordinal)
            && string.Equals(existing.PosServerId, request.PosServerId, StringComparison.Ordinal);
        var openingEvidence = existing.DenominationEntries
            .Where(entry => entry.CaptureType == CashCustodyDenominationCaptureType.Opening)
            .ToArray();

        return sameScope
            && openingEvidence.Length > 0
            && EvidenceMatches(openingEvidence, normalized.Lines)
                ? CashJournalResult<CashCustodySessionSnapshot>.Success(CashCustodySessionSnapshot.FromEntity(existing))
                : CashJournalResult<CashCustodySessionSnapshot>.Failure(new CashJournalError(
                    CashJournalErrorCode.ConflictingDenominationEvidence,
                    "The cash-custody opening request conflicts with existing immutable evidence."));
    }

    private static CashJournalResult<NormalizedCustodyDenominations> NormalizeCustodyDenominations(
        IReadOnlyCollection<CashCustodyDenominationLine>? submitted)
    {
        var supported = CashCustodyDenominationCatalog.Supported;
        if (submitted is null || submitted.Count != supported.Count)
        {
            return InvalidDenominationResult("A complete count for every supported denomination is required.");
        }

        var submittedByCode = new Dictionary<string, CashCustodyDenominationLine>(StringComparer.Ordinal);
        foreach (var line in submitted)
        {
            if (string.IsNullOrWhiteSpace(line.DenominationCode)
                || !submittedByCode.TryAdd(line.DenominationCode, line))
            {
                return InvalidDenominationResult("Denomination codes must be present and unique.");
            }
        }

        var normalized = new List<CashCustodyDenominationLine>(supported.Count);
        long totalMinorUnits = 0;
        foreach (var definition in supported)
        {
            if (!submittedByCode.TryGetValue(definition.Code, out var line)
                || line.DenominationValueMinorUnits != definition.ValueMinorUnits
                || line.Quantity < 0)
            {
                return InvalidDenominationResult("The cash count contains a missing, unsupported, or invalid denomination.");
            }

            try
            {
                totalMinorUnits = checked(totalMinorUnits + ((long)definition.ValueMinorUnits * line.Quantity));
            }
            catch (OverflowException)
            {
                return InvalidDenominationResult("The cash count total exceeds the supported range.");
            }

            normalized.Add(new CashCustodyDenominationLine(definition.Code, definition.ValueMinorUnits, line.Quantity));
        }

        if (submittedByCode.Keys.Except(supported.Select(value => value.Code), StringComparer.Ordinal).Any())
        {
            return InvalidDenominationResult("The cash count contains an unsupported denomination.");
        }

        return CashJournalResult<NormalizedCustodyDenominations>.Success(
            new NormalizedCustodyDenominations(normalized, totalMinorUnits));
    }

    private static CashJournalResult<NormalizedCustodyDenominations> InvalidDenominationResult(string message) =>
        CashJournalResult<NormalizedCustodyDenominations>.Failure(new CashJournalError(
            CashJournalErrorCode.InvalidDenominationEvidence,
            message));

    private static IReadOnlyList<CashCustodyDenominationEntry> AddDenominationEvidence(
        CashCustodySession session,
        CashCustodyDenominationCaptureType captureType,
        IReadOnlyList<CashCustodyDenominationLine> lines,
        DateTimeOffset createdAt)
    {
        var entries = new List<CashCustodyDenominationEntry>(lines.Count);
        foreach (var line in lines)
        {
            var entry = new CashCustodyDenominationEntry
            {
                Id = Guid.NewGuid(),
                CashCustodySessionId = session.Id,
                CaptureType = captureType,
                DenominationCode = line.DenominationCode,
                DenominationValueMinorUnits = line.DenominationValueMinorUnits,
                Quantity = line.Quantity,
                CalculatedAmountMinorUnits = (long)line.DenominationValueMinorUnits * line.Quantity,
                CreatedAt = createdAt
            };
            session.DenominationEntries.Add(entry);
            entries.Add(entry);
        }

        return entries;
    }

    private static bool EvidenceMatches(
        IReadOnlyCollection<CashCustodyDenominationEntry> committed,
        IReadOnlyList<CashCustodyDenominationLine> submitted)
    {
        if (committed.Count != submitted.Count)
        {
            return false;
        }

        var committedByCode = committed.ToDictionary(entry => entry.DenominationCode, StringComparer.Ordinal);
        return submitted.All(line =>
            committedByCode.TryGetValue(line.DenominationCode, out var entry)
            && entry.DenominationValueMinorUnits == line.DenominationValueMinorUnits
            && entry.Quantity == line.Quantity
            && entry.CalculatedAmountMinorUnits == (long)line.DenominationValueMinorUnits * line.Quantity);
    }

    private static decimal MinorUnitsToAmount(long minorUnits) => minorUnits / 100m;

    private static Task EnsureTerminalCashFiscalSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken) =>
        AddColumnIfMissingAsync(
            dbContext,
            "terminal_cash_fiscal_outbox_commands",
            "ExitAuthorizationIssued",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

    private static void ApplyStatutoryTenderEvidence(
        CashTender tender,
        StatutoryTenderEvidence? evidence,
        DateTimeOffset recordedAt)
    {
        if (evidence is null)
        {
            return;
        }

        tender.StatutoryDiscountDecisionCommandId = evidence.StatutoryDiscountDecisionCommandId;
        tender.StatutoryDiscountPayableBasisApplicationCommandId = evidence.StatutoryDiscountPayableBasisApplicationCommandId;
        tender.StatutoryDiscountValidationId = evidence.StatutoryDiscountValidationId;
        tender.StatutoryOriginalTariffSnapshotId = evidence.OriginalTariffSnapshotId;
        tender.StatutoryAppliedTariffSnapshotId = evidence.AppliedTariffSnapshotId;
        tender.StatutoryOriginalAmountMinorUnits = evidence.OriginalAmountMinorUnits;
        tender.StatutoryFinalAmountMinorUnits = evidence.FinalAmountMinorUnits;
        tender.StatutoryCurrency = evidence.Currency;
        tender.StatutoryAmountAcknowledged = evidence.AmountAcknowledged;
        tender.StatutoryAmountAcknowledgedAt = evidence.AmountAcknowledgedAt ?? recordedAt;
        tender.StatutoryImmediateRevalidationOutcome = evidence.ImmediateRevalidationOutcome;
        tender.StatutoryImmediateRevalidatedAt = evidence.ImmediateRevalidatedAt ?? recordedAt;
        tender.StatutoryCorrelationId = evidence.CentralPmsCorrelationId;
        tender.StatutoryReadinessStatus = evidence.ReadinessStatus;
        tender.StatutoryReadinessAction = evidence.ReadinessAction;
    }


    private static async Task AddColumnIfMissingAsync(
        CashJournalDbContext dbContext,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    public CashJournalDbContext CreateDbContext()
    {
        var connection = EncryptionManager.OpenEncryptedConnection();

        var options = new DbContextOptionsBuilder<CashJournalDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .Options;

        return new CashJournalDbContext(options);
    }

    private LocalDatabaseEncryptionManager EncryptionManager =>
        _encryptionManager ?? throw new LocalOperationsDatabaseConfigurationException(
            "APT_LOCAL_DB_PATH is not a valid local database path.");

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

    private static IQueryable<CashierShift> ApplyShiftScope(
        IQueryable<CashierShift> query,
        LocalOperationalStateRequest? request)
    {
        if (request is null)
        {
            return query;
        }

        if (!string.IsNullOrWhiteSpace(request.CashierId))
        {
            query = query.Where(shift => shift.CashierId == request.CashierId);
        }

        if (!string.IsNullOrWhiteSpace(request.CashierShiftId))
        {
            query = query.Where(shift => shift.Id == request.CashierShiftId);
        }

        if (!string.IsNullOrWhiteSpace(request.TerminalId))
        {
            query = query.Where(shift => shift.TerminalId == request.TerminalId);
        }

        if (!string.IsNullOrWhiteSpace(request.SiteId))
        {
            query = query.Where(shift => shift.SiteId == request.SiteId);
        }

        if (!string.IsNullOrWhiteSpace(request.SiteGroupId))
        {
            query = query.Where(shift => shift.SiteGroupId == request.SiteGroupId);
        }

        if (!string.IsNullOrWhiteSpace(request.PosServerId))
        {
            query = query.Where(shift => shift.PosServerId == request.PosServerId);
        }

        return query;
    }

    private static IQueryable<CashCustodySession> ApplyCustodyScope(
        IQueryable<CashCustodySession> query,
        LocalOperationalStateRequest? request)
    {
        if (request is null)
        {
            return query;
        }

        if (!string.IsNullOrWhiteSpace(request.CashierId))
        {
            query = query.Where(session => session.CashierId == request.CashierId);
        }

        if (!string.IsNullOrWhiteSpace(request.CashierShiftId))
        {
            query = query.Where(session => session.CashierShiftId == request.CashierShiftId);
        }

        if (!string.IsNullOrWhiteSpace(request.TerminalId))
        {
            query = query.Where(session => session.TerminalId == request.TerminalId);
        }

        if (!string.IsNullOrWhiteSpace(request.SiteId))
        {
            query = query.Where(session => session.SiteId == request.SiteId);
        }

        if (!string.IsNullOrWhiteSpace(request.SiteGroupId))
        {
            query = query.Where(session => session.SiteGroupId == request.SiteGroupId);
        }

        if (!string.IsNullOrWhiteSpace(request.PosServerId))
        {
            query = query.Where(session => session.PosServerId == request.PosServerId);
        }

        return query;
    }

    private sealed record NormalizedCustodyDenominations(
        IReadOnlyList<CashCustodyDenominationLine> Lines,
        long TotalMinorUnits);
}
