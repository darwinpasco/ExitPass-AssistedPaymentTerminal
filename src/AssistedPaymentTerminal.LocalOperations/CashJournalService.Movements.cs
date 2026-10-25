using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed partial class CashJournalService
{
    public async Task<CashJournalResult<CashCustodyMovementSnapshot>> InitiateCashCustodyMovementAsync(
        InitiateCashCustodyMovementRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCustodyDenominations(request.Denominations);
        if (!normalized.IsSuccess)
        {
            return CashJournalResult<CashCustodyMovementSnapshot>.Failure(normalized.Error!);
        }

        if (normalized.Value!.TotalMinorUnits <= 0
            || string.IsNullOrWhiteSpace(request.IntendedCounterpartyReference)
            || string.IsNullOrWhiteSpace(request.ExternalHandoverReference)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "A positive amount and complete handover references are required.");
        }

        if ((request.MovementType == CashCustodyMovementType.CashReturn) != request.OriginalMovementId.HasValue)
        {
            return MovementFailure(CashJournalErrorCode.InvalidOriginalMovement, "A cash return must reference one completed outbound movement.");
        }

        var payloadHash = ComputeMovementPayloadHash(request, normalized.Value.Lines);
        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContext = CreateDbContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var replay = await dbContext.CashCustodyMovements
                .Include(movement => movement.Denominations)
                .Include(movement => movement.Events)
                .SingleOrDefaultAsync(movement => movement.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return string.Equals(replay.CanonicalRequestPayloadHash, payloadHash, StringComparison.Ordinal)
                    ? CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(replay))
                    : MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "The movement idempotency key is bound to different immutable evidence.");
            }

            var session = await dbContext.CashCustodySessions
                .SingleOrDefaultAsync(value => value.Id == request.CashCustodySessionId, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                return MovementFailure(CashJournalErrorCode.NotFound, "The cash-custody session was not found.");
            }

            if (!MovementScopeMatches(session, request.CashierId, request.CashierShiftId, request.TerminalId, request.SiteId, request.SiteGroupId, request.PosServerId)
                || session.Status != CashCustodySessionStatus.Open)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "Only the owning cashier may initiate a movement from their open custody session.");
            }

            var shiftOpen = await dbContext.CashierShifts.AsNoTracking().AnyAsync(
                shift => shift.Id == session.CashierShiftId
                    && shift.CashierId == request.CashierId
                    && shift.Status == CashierShiftStatus.Open,
                cancellationToken).ConfigureAwait(false);
            if (!shiftOpen)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "The owning cashier shift is not open.");
            }

            if (request.OriginalMovementId is Guid originalId)
            {
                var original = await dbContext.CashCustodyMovements
                    .Include(movement => movement.Events)
                    .SingleOrDefaultAsync(movement => movement.Id == originalId, cancellationToken)
                    .ConfigureAwait(false);
                if (original is null
                    || original.CashCustodySessionId != session.Id
                    || original.MovementType is not (CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
                    || ResolveMovementStatus(original.Events) != CashCustodyMovementStatus.Completed)
                {
                    return MovementFailure(CashJournalErrorCode.InvalidOriginalMovement, "The referenced outbound movement is not eligible for return.");
                }
            }

            var createdAt = request.InitiatedAt ?? DateTimeOffset.UtcNow;
            var movement = new CashCustodyMovement
            {
                Id = request.MovementId ?? Guid.NewGuid(),
                CashCustodySessionId = session.Id,
                MovementType = request.MovementType,
                AmountMinorUnits = normalized.Value.TotalMinorUnits,
                OriginalMovementId = request.OriginalMovementId,
                InitiatingCashierId = request.CashierId,
                InitiatingCashierSessionReference = request.AuthenticatedCashierSessionReference,
                IntendedCounterpartyReference = request.IntendedCounterpartyReference.Trim(),
                ExternalHandoverReference = request.ExternalHandoverReference.Trim(),
                IdempotencyKey = request.IdempotencyKey.Trim(),
                CanonicalRequestPayloadHash = payloadHash,
                CreatedAt = createdAt
            };
            foreach (var line in normalized.Value.Lines)
            {
                movement.Denominations.Add(new CashCustodyMovementDenomination
                {
                    Id = Guid.NewGuid(),
                    MovementId = movement.Id,
                    DenominationCode = line.DenominationCode,
                    DenominationValueMinorUnits = line.DenominationValueMinorUnits,
                    Quantity = line.Quantity,
                    CalculatedAmountMinorUnits = checked((long)line.DenominationValueMinorUnits * line.Quantity),
                    CreatedAt = createdAt
                });
            }
            movement.Events.Add(new CashCustodyMovementEvent
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                EventType = CashCustodyMovementEventType.Initiated,
                AuthorizedActorId = request.CashierId,
                ActorSessionReference = request.AuthenticatedCashierSessionReference,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = $"{request.IdempotencyKey}:initiated",
                OccurredAt = createdAt
            });

            dbContext.CashCustodyMovements.Add(movement);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(movement));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    public async Task<CashJournalResult<CashCustodyMovementSnapshot>> AcknowledgeCashCustodyMovementAsync(
        AcknowledgeCashCustodyMovementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AuthorizedActorId)
            || string.IsNullOrWhiteSpace(request.AuthorizedActorUsername)
            || string.IsNullOrWhiteSpace(request.ActorSessionReference)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "Complete authenticated acknowledgment evidence is required.");
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContext = CreateDbContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var movement = await LoadMovementAsync(dbContext, request.MovementId, cancellationToken).ConfigureAwait(false);
            if (movement is null)
            {
                return MovementFailure(CashJournalErrorCode.NotFound, "The cash-custody movement was not found.");
            }

            var replayByKey = await dbContext.CashCustodyMovementEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (replayByKey is not null)
            {
                return replayByKey.MovementId == movement.Id
                    && replayByKey.EventType == CashCustodyMovementEventType.Completed
                    && string.Equals(replayByKey.AuthorizedActorId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase)
                    ? CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(movement))
                    : MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "The acknowledgment idempotency key is bound to different evidence.");
            }

            if (!MovementTerminalScopeMatches(movement.CashCustodySession!, request.TerminalId, request.SiteId, request.SiteGroupId, request.PosServerId)
                || movement.CashCustodySession!.Status != CashCustodySessionStatus.Open)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "The movement is outside the current open custody scope.");
            }

            if (!string.Equals(movement.InitiatingCashierId, request.OwningCashierId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(movement.InitiatingCashierId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase))
            {
                return MovementFailure(CashJournalErrorCode.DistinctPrincipalRequired, "A distinct authorized person must acknowledge the physical transfer.");
            }

            if (!string.Equals(movement.IntendedCounterpartyReference, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(movement.IntendedCounterpartyReference, request.AuthorizedActorUsername, StringComparison.OrdinalIgnoreCase))
            {
                return MovementFailure(CashJournalErrorCode.DistinctPrincipalRequired, "The authenticated receiver does not match the intended counterparty.");
            }

            var status = ResolveMovementStatus(movement.Events);
            if (status == CashCustodyMovementStatus.Completed)
            {
                var completion = movement.Events.Single(value => value.EventType == CashCustodyMovementEventType.Completed);
                return string.Equals(completion.AuthorizedActorId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase)
                    ? CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(movement))
                    : MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "The movement was completed by a different authorized principal.");
            }
            if (status != CashCustodyMovementStatus.PendingAcknowledgment)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "A cancelled or rejected movement cannot be completed.");
            }

            if (movement.MovementType is CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
            {
                var available = await CalculateAvailableCashMinorUnitsAsync(dbContext, movement.CashCustodySession!, cancellationToken).ConfigureAwait(false);
                if (movement.AmountMinorUnits > available)
                {
                    return MovementFailure(CashJournalErrorCode.InsufficientCashCustody, "The custody movement exceeds available physical cash.");
                }
            }
            else
            {
                var returnValidation = await ValidateCashReturnLimitAsync(dbContext, movement, cancellationToken).ConfigureAwait(false);
                if (returnValidation is not null)
                {
                    return MovementFailure(returnValidation.Value.Code, returnValidation.Value.Message);
                }
            }

            var completedEvent = new CashCustodyMovementEvent
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                EventType = CashCustodyMovementEventType.Completed,
                AuthorizedActorId = request.AuthorizedActorId,
                ActorSessionReference = request.ActorSessionReference,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                OccurredAt = request.AcknowledgedAt ?? DateTimeOffset.UtcNow
            };
            await InsertMovementEventAsync(dbContext, completedEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CashJournalResult<CashCustodyMovementSnapshot>.Success(
                await LoadMovementSnapshotAsync(dbContext, movement.Id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    public async Task<CashJournalResult<CashCustodyMovementSnapshot>> ResolvePendingCashCustodyMovementAsync(
        ResolvePendingCashCustodyMovementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Outcome is not (CashCustodyMovementEventType.Cancelled or CashCustodyMovementEventType.Rejected)
            || string.IsNullOrWhiteSpace(request.ReasonCode)
            || string.IsNullOrWhiteSpace(request.AuthorizedActorId)
            || string.IsNullOrWhiteSpace(request.ActorSessionReference)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "Only a reasoned cancellation or rejection is supported.");
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContext = CreateDbContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var movement = await LoadMovementAsync(dbContext, request.MovementId, cancellationToken).ConfigureAwait(false);
            if (movement is null)
            {
                return MovementFailure(CashJournalErrorCode.NotFound, "The cash-custody movement was not found.");
            }
            var replayByKey = await dbContext.CashCustodyMovementEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (replayByKey is not null)
            {
                return replayByKey.MovementId == movement.Id
                    && replayByKey.EventType == request.Outcome
                    && replayByKey.ReasonCode == request.ReasonCode.Trim()
                    && string.Equals(replayByKey.AuthorizedActorId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase)
                    ? CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(movement))
                    : MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "The outcome idempotency key is bound to different evidence.");
            }
            if (!MovementTerminalScopeMatches(movement.CashCustodySession!, request.TerminalId, request.SiteId, request.SiteGroupId, request.PosServerId)
                || movement.CashCustodySession!.Status != CashCustodySessionStatus.Open)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "The movement is outside the current open custody scope.");
            }
            if (ResolveMovementStatus(movement.Events) != CashCustodyMovementStatus.PendingAcknowledgment)
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "Completed movement outcomes are immutable.");
            }
            if (request.Outcome == CashCustodyMovementEventType.Cancelled
                && !string.Equals(movement.InitiatingCashierId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase))
            {
                return MovementFailure(CashJournalErrorCode.DistinctPrincipalRequired, "Only the initiating cashier may cancel this pending movement.");
            }
            if (request.Outcome == CashCustodyMovementEventType.Rejected
                && string.Equals(movement.InitiatingCashierId, request.AuthorizedActorId, StringComparison.OrdinalIgnoreCase))
            {
                return MovementFailure(CashJournalErrorCode.DistinctPrincipalRequired, "A distinct authorized person must reject the physical transfer.");
            }

            var terminalEvent = new CashCustodyMovementEvent
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                EventType = request.Outcome,
                AuthorizedActorId = request.AuthorizedActorId,
                ActorSessionReference = request.ActorSessionReference,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                ReasonCode = request.ReasonCode.Trim(),
                OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow
            };
            await InsertMovementEventAsync(dbContext, terminalEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CashJournalResult<CashCustodyMovementSnapshot>.Success(
                await LoadMovementSnapshotAsync(dbContext, movement.Id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    public async Task<CashJournalResult<CashCustodyMovementSnapshot>> AppendCashCustodyMovementNoteAsync(
        AppendCashCustodyMovementNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.NoteType is not (CashCustodyMovementEventType.CorrectionNoted or CashCustodyMovementEventType.DisputeRaised or CashCustodyMovementEventType.DisputeResolved)
            || string.IsNullOrWhiteSpace(request.ReasonCode)
            || string.IsNullOrWhiteSpace(request.ControlledMetadata)
            || string.IsNullOrWhiteSpace(request.AuthorizedActorId)
            || string.IsNullOrWhiteSpace(request.ActorSessionReference)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "Only append-only correction or dispute notes are supported.");
        }

        await _cashCustodyMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContext = CreateDbContext();
            var movement = await LoadMovementAsync(dbContext, request.MovementId, cancellationToken).ConfigureAwait(false);
            if (movement is null)
            {
                return MovementFailure(CashJournalErrorCode.NotFound, "The cash-custody movement was not found.");
            }
            if (!MovementTerminalScopeMatches(movement.CashCustodySession!, request.TerminalId, request.SiteId, request.SiteGroupId, request.PosServerId))
            {
                return MovementFailure(CashJournalErrorCode.InvalidStateTransition, "The movement is outside the current custody scope.");
            }

            var replay = await dbContext.CashCustodyMovementEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay.MovementId == movement.Id
                    && replay.EventType == request.NoteType
                    && replay.ReasonCode == request.ReasonCode.Trim()
                    && replay.ControlledMetadata == request.ControlledMetadata.Trim()
                    ? CashJournalResult<CashCustodyMovementSnapshot>.Success(ToMovementSnapshot(movement))
                    : MovementFailure(CashJournalErrorCode.CashCustodyMovementConflict, "The note idempotency key is bound to different evidence.");
            }

            var noteEvent = new CashCustodyMovementEvent
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                EventType = request.NoteType,
                AuthorizedActorId = request.AuthorizedActorId,
                ActorSessionReference = request.ActorSessionReference,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                ReasonCode = request.ReasonCode.Trim(),
                ControlledMetadata = request.ControlledMetadata.Trim(),
                OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow
            };
            await InsertMovementEventAsync(dbContext, noteEvent, cancellationToken).ConfigureAwait(false);
            return CashJournalResult<CashCustodyMovementSnapshot>.Success(
                await LoadMovementSnapshotAsync(dbContext, movement.Id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _cashCustodyMutationLock.Release();
        }
    }

    private static async Task EnsureCashCustodyMovementSchemaAsync(
        CashJournalDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "CompletedMovementCountAtClose", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "CompletedCashInMinorUnitsAtClose", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "CompletedCashOutMinorUnitsAtClose", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(dbContext, "cash_custody_sessions", "CompletedMovementReferencesAtCloseJson", "TEXT NULL", cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS cash_custody_movements (
                Id TEXT NOT NULL CONSTRAINT PK_cash_custody_movements PRIMARY KEY,
                CashCustodySessionId TEXT NOT NULL,
                MovementType TEXT NOT NULL,
                AmountMinorUnits INTEGER NOT NULL,
                OriginalMovementId TEXT NULL,
                InitiatingCashierId TEXT NOT NULL,
                InitiatingCashierSessionReference TEXT NOT NULL,
                IntendedCounterpartyReference TEXT NOT NULL,
                ExternalHandoverReference TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                CanonicalRequestPayloadHash TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_cash_custody_movements_cash_custody_sessions_CashCustodySessionId
                    FOREIGN KEY (CashCustodySessionId) REFERENCES cash_custody_sessions (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_cash_custody_movements_cash_custody_movements_OriginalMovementId
                    FOREIGN KEY (OriginalMovementId) REFERENCES cash_custody_movements (Id) ON DELETE RESTRICT,
                CONSTRAINT CK_cash_custody_movements_MovementType CHECK (MovementType IN ('SafeDrop', 'DirectRemittance', 'CashReturn')),
                CONSTRAINT CK_cash_custody_movements_Amount CHECK (AmountMinorUnits > 0),
                CONSTRAINT CK_cash_custody_movements_OriginalReference CHECK (
                    (MovementType = 'CashReturn' AND OriginalMovementId IS NOT NULL)
                    OR (MovementType <> 'CashReturn' AND OriginalMovementId IS NULL))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cash_custody_movements_IdempotencyKey
                ON cash_custody_movements (IdempotencyKey);
            CREATE INDEX IF NOT EXISTS IX_cash_custody_movements_Session_CreatedAt
                ON cash_custody_movements (CashCustodySessionId, CreatedAt);

            CREATE TABLE IF NOT EXISTS cash_custody_movement_denominations (
                Id TEXT NOT NULL CONSTRAINT PK_cash_custody_movement_denominations PRIMARY KEY,
                MovementId TEXT NOT NULL,
                DenominationCode TEXT NOT NULL,
                DenominationValueMinorUnits INTEGER NOT NULL,
                Quantity INTEGER NOT NULL,
                CalculatedAmountMinorUnits INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_cash_custody_movement_denominations_cash_custody_movements_MovementId
                    FOREIGN KEY (MovementId) REFERENCES cash_custody_movements (Id) ON DELETE RESTRICT,
                CONSTRAINT CK_cash_custody_movement_denominations_Value CHECK (DenominationValueMinorUnits > 0),
                CONSTRAINT CK_cash_custody_movement_denominations_Quantity CHECK (Quantity >= 0),
                CONSTRAINT CK_cash_custody_movement_denominations_Amount
                    CHECK (CalculatedAmountMinorUnits = DenominationValueMinorUnits * Quantity)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cash_custody_movement_denominations_Movement_Code
                ON cash_custody_movement_denominations (MovementId, DenominationCode);

            CREATE TABLE IF NOT EXISTS cash_custody_movement_events (
                Id TEXT NOT NULL CONSTRAINT PK_cash_custody_movement_events PRIMARY KEY,
                MovementId TEXT NOT NULL,
                EventType TEXT NOT NULL,
                AuthorizedActorId TEXT NOT NULL,
                ActorSessionReference TEXT NOT NULL,
                CorrelationId TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                ReasonCode TEXT NULL,
                ControlledMetadata TEXT NULL,
                OccurredAt INTEGER NOT NULL,
                CONSTRAINT FK_cash_custody_movement_events_cash_custody_movements_MovementId
                    FOREIGN KEY (MovementId) REFERENCES cash_custody_movements (Id) ON DELETE RESTRICT,
                CONSTRAINT CK_cash_custody_movement_events_EventType CHECK (
                    EventType IN ('Initiated', 'Completed', 'Cancelled', 'Rejected', 'CorrectionNoted', 'DisputeRaised', 'DisputeResolved'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cash_custody_movement_events_IdempotencyKey
                ON cash_custody_movement_events (IdempotencyKey);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cash_custody_movement_events_TerminalOutcome
                ON cash_custody_movement_events (MovementId)
                WHERE EventType IN ('Completed', 'Cancelled', 'Rejected');
            CREATE INDEX IF NOT EXISTS IX_cash_custody_movement_events_Movement_OccurredAt
                ON cash_custody_movement_events (MovementId, OccurredAt);

            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movements_immutable_update
                BEFORE UPDATE ON cash_custody_movements
                BEGIN SELECT RAISE(ABORT, 'cash custody movement evidence is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movements_immutable_delete
                BEFORE DELETE ON cash_custody_movements
                BEGIN SELECT RAISE(ABORT, 'cash custody movement evidence is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movement_denominations_immutable_update
                BEFORE UPDATE ON cash_custody_movement_denominations
                BEGIN SELECT RAISE(ABORT, 'cash custody movement denomination evidence is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movement_denominations_immutable_delete
                BEFORE DELETE ON cash_custody_movement_denominations
                BEGIN SELECT RAISE(ABORT, 'cash custody movement denomination evidence is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movement_events_immutable_update
                BEFORE UPDATE ON cash_custody_movement_events
                BEGIN SELECT RAISE(ABORT, 'cash custody movement events are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movement_events_immutable_delete
                BEFORE DELETE ON cash_custody_movement_events
                BEGIN SELECT RAISE(ABORT, 'cash custody movement events are append-only'); END;

            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_movement_complete_evidence
                BEFORE INSERT ON cash_custody_movement_events
                WHEN NEW.EventType = 'Completed'
                BEGIN
                    SELECT CASE WHEN (SELECT count(*) FROM cash_custody_movement_denominations WHERE MovementId = NEW.MovementId) <> 13
                        THEN RAISE(ABORT, 'complete movement denomination evidence is required') END;
                    SELECT CASE WHEN (SELECT coalesce(sum(CalculatedAmountMinorUnits), 0) FROM cash_custody_movement_denominations WHERE MovementId = NEW.MovementId)
                        <> (SELECT AmountMinorUnits FROM cash_custody_movements WHERE Id = NEW.MovementId)
                        THEN RAISE(ABORT, 'movement total does not match denomination evidence') END;
                END;

            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_cash_return_limit
                BEFORE INSERT ON cash_custody_movement_events
                WHEN NEW.EventType = 'Completed'
                  AND (SELECT MovementType FROM cash_custody_movements WHERE Id = NEW.MovementId) = 'CashReturn'
                BEGIN
                    SELECT CASE WHEN NOT EXISTS (
                        SELECT 1
                        FROM cash_custody_movements returned
                        JOIN cash_custody_movements original ON original.Id = returned.OriginalMovementId
                        JOIN cash_custody_movement_events completed ON completed.MovementId = original.Id AND completed.EventType = 'Completed'
                        WHERE returned.Id = NEW.MovementId
                          AND returned.CashCustodySessionId = original.CashCustodySessionId
                          AND original.MovementType IN ('SafeDrop', 'DirectRemittance'))
                        THEN RAISE(ABORT, 'cash return original movement is invalid') END;
                    SELECT CASE WHEN (
                        SELECT coalesce(sum(returned.AmountMinorUnits), 0)
                        FROM cash_custody_movements returned
                        JOIN cash_custody_movement_events completed ON completed.MovementId = returned.Id AND completed.EventType = 'Completed'
                        WHERE returned.OriginalMovementId = (SELECT OriginalMovementId FROM cash_custody_movements WHERE Id = NEW.MovementId))
                        + (SELECT AmountMinorUnits FROM cash_custody_movements WHERE Id = NEW.MovementId)
                        > (SELECT original.AmountMinorUnits FROM cash_custody_movements original
                           WHERE original.Id = (SELECT OriginalMovementId FROM cash_custody_movements WHERE Id = NEW.MovementId))
                        THEN RAISE(ABORT, 'cash return exceeds the original outbound amount') END;
                END;

            CREATE TRIGGER IF NOT EXISTS TR_cash_custody_sessions_stage2b_close_guard
                BEFORE UPDATE OF Status ON cash_custody_sessions
                WHEN OLD.Status = 'Open' AND NEW.Status = 'Closed'
                  AND EXISTS (
                      SELECT 1 FROM cash_custody_movements movement
                      JOIN cash_custody_movement_events completed
                        ON completed.MovementId = movement.Id AND completed.EventType = 'Completed'
                      WHERE movement.CashCustodySessionId = NEW.Id)
                  AND NEW.CompletedMovementCountAtClose IS NULL
                BEGIN SELECT RAISE(ABORT, 'Stage 2B movement-aware closing is required'); END;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    internal static CashCustodyMovementStatus ResolveMovementStatus(IEnumerable<CashCustodyMovementEvent> events)
    {
        var terminal = events.OrderBy(value => value.OccurredAt).LastOrDefault(value =>
            value.EventType is CashCustodyMovementEventType.Completed or CashCustodyMovementEventType.Cancelled or CashCustodyMovementEventType.Rejected);
        return terminal?.EventType switch
        {
            CashCustodyMovementEventType.Completed => CashCustodyMovementStatus.Completed,
            CashCustodyMovementEventType.Cancelled => CashCustodyMovementStatus.Cancelled,
            CashCustodyMovementEventType.Rejected => CashCustodyMovementStatus.Rejected,
            _ => CashCustodyMovementStatus.PendingAcknowledgment
        };
    }

    internal static CashCustodyMovementSnapshot ToMovementSnapshot(CashCustodyMovement movement) =>
        new(
            movement.Id,
            movement.CashCustodySessionId,
            movement.MovementType,
            ResolveMovementStatus(movement.Events),
            movement.AmountMinorUnits,
            movement.OriginalMovementId,
            movement.InitiatingCashierId,
            movement.IntendedCounterpartyReference,
            movement.ExternalHandoverReference,
            movement.CreatedAt,
            movement.Denominations.OrderByDescending(value => value.DenominationValueMinorUnits)
                .Select(value => new CashCustodyMovementDenominationSnapshot(
                    value.DenominationCode,
                    value.DenominationValueMinorUnits,
                    value.Quantity,
                    value.CalculatedAmountMinorUnits)).ToArray(),
            movement.Events.OrderBy(value => value.OccurredAt)
                .Select(value => new CashCustodyMovementEventSnapshot(
                    value.Id,
                    value.EventType,
                    value.AuthorizedActorId,
                    value.ActorSessionReference,
                    value.CorrelationId,
                    value.ReasonCode,
                    value.ControlledMetadata,
                    value.OccurredAt)).ToArray());

    private static async Task<CashCustodyMovement?> LoadMovementAsync(
        CashJournalDbContext dbContext,
        Guid movementId,
        CancellationToken cancellationToken) =>
        await dbContext.CashCustodyMovements
            .Include(movement => movement.CashCustodySession)
            .Include(movement => movement.Denominations)
            .Include(movement => movement.Events)
            .SingleOrDefaultAsync(movement => movement.Id == movementId, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<CashCustodyMovementSnapshot> LoadMovementSnapshotAsync(
        CashJournalDbContext dbContext,
        Guid movementId,
        CancellationToken cancellationToken)
    {
        var movement = await dbContext.CashCustodyMovements.AsNoTracking()
            .Include(value => value.Denominations)
            .Include(value => value.Events)
            .SingleAsync(value => value.Id == movementId, cancellationToken)
            .ConfigureAwait(false);
        return ToMovementSnapshot(movement);
    }

    private static Task<int> InsertMovementEventAsync(
        CashJournalDbContext dbContext,
        CashCustodyMovementEvent movementEvent,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO cash_custody_movement_events (
                Id, MovementId, EventType, AuthorizedActorId, ActorSessionReference,
                CorrelationId, IdempotencyKey, ReasonCode, ControlledMetadata, OccurredAt)
            VALUES (
                {movementEvent.Id}, {movementEvent.MovementId}, {movementEvent.EventType.ToString()},
                {movementEvent.AuthorizedActorId}, {movementEvent.ActorSessionReference},
                {movementEvent.CorrelationId}, {movementEvent.IdempotencyKey},
                {movementEvent.ReasonCode}, {movementEvent.ControlledMetadata},
                {movementEvent.OccurredAt.UtcDateTime.Ticks})
            """,
            cancellationToken);

    private static bool MovementScopeMatches(
        CashCustodySession session,
        string cashierId,
        string shiftId,
        string terminalId,
        string siteId,
        string siteGroupId,
        string posServerId) =>
        string.Equals(session.CashierId, cashierId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(session.CashierShiftId, shiftId, StringComparison.Ordinal)
        && MovementTerminalScopeMatches(session, terminalId, siteId, siteGroupId, posServerId);

    private static bool MovementTerminalScopeMatches(
        CashCustodySession session,
        string terminalId,
        string siteId,
        string siteGroupId,
        string posServerId) =>
        string.Equals(session.TerminalId, terminalId, StringComparison.Ordinal)
        && string.Equals(session.SiteId, siteId, StringComparison.Ordinal)
        && string.Equals(session.SiteGroupId, siteGroupId, StringComparison.Ordinal)
        && string.Equals(session.PosServerId, posServerId, StringComparison.Ordinal);

    private static string ComputeMovementPayloadHash(
        InitiateCashCustodyMovementRequest request,
        IReadOnlyList<CashCustodyDenominationLine> denominations)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.CashCustodySessionId,
            request.MovementType,
            request.OriginalMovementId,
            request.CashierId,
            request.CashierShiftId,
            request.TerminalId,
            request.SiteId,
            request.SiteGroupId,
            request.PosServerId,
            IntendedCounterpartyReference = request.IntendedCounterpartyReference.Trim(),
            ExternalHandoverReference = request.ExternalHandoverReference.Trim(),
            Denominations = denominations.OrderBy(value => value.DenominationCode, StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task<long> CalculateAvailableCashMinorUnitsAsync(
        CashJournalDbContext dbContext,
        CashCustodySession session,
        CancellationToken cancellationToken)
    {
        var cashReceived = await dbContext.CashTenders.AsNoTracking()
            .Where(tender => tender.CashCustodySessionId == session.Id && tender.CurrentLocalState == CashTenderState.CashReceived)
            .Select(tender => tender.AmountDue)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var completed = await dbContext.CashCustodyMovements.AsNoTracking()
            .Include(movement => movement.Events)
            .Where(movement => movement.CashCustodySessionId == session.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var cashIn = completed.Where(value => value.MovementType == CashCustodyMovementType.CashReturn && ResolveMovementStatus(value.Events) == CashCustodyMovementStatus.Completed)
            .Sum(value => value.AmountMinorUnits);
        var cashOut = completed.Where(value =>
                (value.MovementType is CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
                && ResolveMovementStatus(value.Events) == CashCustodyMovementStatus.Completed)
            .Sum(value => value.AmountMinorUnits);
        return checked(AmountToMinorUnits(session.OpeningCashAmount)
            + cashReceived.Sum(AmountToMinorUnits)
            + cashIn
            - cashOut);
    }

    private static async Task<(CashJournalErrorCode Code, string Message)?> ValidateCashReturnLimitAsync(
        CashJournalDbContext dbContext,
        CashCustodyMovement movement,
        CancellationToken cancellationToken)
    {
        if (movement.OriginalMovementId is not Guid originalId)
        {
            return (CashJournalErrorCode.InvalidOriginalMovement, "The cash return has no original outbound movement.");
        }
        var original = await dbContext.CashCustodyMovements.AsNoTracking()
            .Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.Id == originalId, cancellationToken).ConfigureAwait(false);
        if (original is null
            || original.CashCustodySessionId != movement.CashCustodySessionId
            || original.MovementType is not (CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
            || ResolveMovementStatus(original.Events) != CashCustodyMovementStatus.Completed)
        {
            return (CashJournalErrorCode.InvalidOriginalMovement, "The referenced outbound movement is not eligible for return.");
        }

        var completedReturns = await dbContext.CashCustodyMovements.AsNoTracking()
            .Include(value => value.Events)
            .Where(value => value.OriginalMovementId == originalId && value.Id != movement.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var returnedMinorUnits = completedReturns
            .Where(value => ResolveMovementStatus(value.Events) == CashCustodyMovementStatus.Completed)
            .Sum(value => value.AmountMinorUnits);
        return checked(returnedMinorUnits + movement.AmountMinorUnits) <= original.AmountMinorUnits
            ? null
            : (CashJournalErrorCode.CashReturnLimitExceeded, "The cash return exceeds the unreturned amount of the original transfer.");
    }

    private static long AmountToMinorUnits(decimal amount) => checked(decimal.ToInt64(amount * 100m));

    private static CashJournalResult<CashCustodyMovementSnapshot> MovementFailure(CashJournalErrorCode code, string message) =>
        CashJournalResult<CashCustodyMovementSnapshot>.Failure(new CashJournalError(code, message));
}
