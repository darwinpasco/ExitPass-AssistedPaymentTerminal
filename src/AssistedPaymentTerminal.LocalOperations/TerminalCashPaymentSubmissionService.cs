using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashPaymentSubmissionService
{
    private readonly LocalOperationsDatabaseOptions _options;
    private readonly ICentralPmsTerminalCashPaymentClient _client;
    private readonly LocalOperationsDatabaseConfigurationException? _configurationError;

    public TerminalCashPaymentSubmissionService(
        ICentralPmsTerminalCashPaymentClient client,
        LocalOperationsDatabaseOptions? options = null)
    {
        _client = client;
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

    public async Task<TerminalCashPaymentOutboxCommand> SubmitOrReadbackAsync(
        Guid localCommandId,
        CancellationToken cancellationToken = default)
    {
        return await SubmitOrReadbackCoreAsync(
            localCommandId,
            allowStaleTariffRecovery: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalCashPaymentOutboxCommand> RecoverStaleTariffConflictAsync(
        Guid localCommandId,
        CancellationToken cancellationToken = default)
    {
        return await SubmitOrReadbackCoreAsync(
            localCommandId,
            allowStaleTariffRecovery: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerminalCashPaymentOutboxCommand> SubmitOrReadbackCoreAsync(
        Guid localCommandId,
        bool allowStaleTariffRecovery,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var command = await dbContext.TerminalCashPaymentOutboxCommands
            .Include(value => value.Attempts)
            .SingleAsync(value => value.Id == localCommandId, cancellationToken)
            .ConfigureAwait(false);

        RebindNeverAttemptedPlaceholder(command);

        if (allowStaleTariffRecovery)
        {
            EnsureStaleTariffRecoveryCommand(command);
            await EnsureImmutableCashReceivedLinkageAsync(dbContext, command, cancellationToken).ConfigureAwait(false);
        }

        if (command.Status == TerminalCashPaymentCommandStatus.Confirmed)
        {
            await TerminalCashFiscalSubmissionService.EnsureCommandForConfirmedPaymentAsync(
                    dbContext,
                    command,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return command;
        }

        if (!allowStaleTariffRecovery
            && command.Status is (TerminalCashPaymentCommandStatus.Conflict or TerminalCashPaymentCommandStatus.Rejected))
        {
            return command;
        }

        if (command.Attempts.Count > 0)
        {
            var readback = await _client.ReadbackAsync(
                new Uri(command.CentralPmsTarget, UriKind.Absolute),
                command.TerminalCashTenderId,
                command.OriginalCorrelationId,
                TimeSpan.FromSeconds(_options.CentralPmsTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            await RecordAttemptAndApplyResultAsync(
                dbContext,
                command,
                TerminalCashPaymentOutboxOperationType.Readback,
                readback,
                cancellationToken).ConfigureAwait(false);

            if (readback.Outcome == TerminalCashPaymentAttemptOutcome.Confirmed)
            {
                return command;
            }

            if (readback.Outcome != TerminalCashPaymentAttemptOutcome.NotFound)
            {
                return command;
            }
        }

        var payload = JsonSerializer.Deserialize<TerminalCashPaymentRequest>(
            command.RequestPayloadJson,
            TerminalCashPaymentPayloadFactory.JsonOptions)!;

        var submit = await _client.SubmitAsync(
            new Uri(command.CentralPmsTarget, UriKind.Absolute),
            payload,
            command.IdempotencyKey,
            command.OriginalCorrelationId,
            TimeSpan.FromSeconds(_options.CentralPmsTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        await RecordAttemptAndApplyResultAsync(
            dbContext,
            command,
            TerminalCashPaymentOutboxOperationType.Submit,
            submit,
            cancellationToken).ConfigureAwait(false);

        return command;
    }

    private static void EnsureStaleTariffRecoveryCommand(TerminalCashPaymentOutboxCommand command)
    {
        var latestAttempt = command.Attempts
            .OrderByDescending(attempt => attempt.AttemptSequence)
            .FirstOrDefault();
        if (command.Status != TerminalCashPaymentCommandStatus.Conflict
            || !string.Equals(command.LastSafeErrorCode, "STALE_TARIFF", StringComparison.Ordinal)
            || latestAttempt is null
            || latestAttempt.OutcomeClassification != TerminalCashPaymentAttemptOutcome.Conflict
            || !string.Equals(latestAttempt.SafeErrorCode, "STALE_TARIFF", StringComparison.Ordinal)
            || command.AttemptCount != command.Attempts.Count
            || command.CanonicalPaymentAttemptId is not null
            || command.CanonicalPaymentConfirmationId is not null)
        {
            throw new InvalidOperationException("Only an unchanged STALE_TARIFF conflict without canonical payment references is eligible for recovery.");
        }
    }

    private static async Task EnsureImmutableCashReceivedLinkageAsync(
        CashJournalDbContext dbContext,
        TerminalCashPaymentOutboxCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                TerminalCashPaymentPayloadFactory.ComputeHash(command.RequestPayloadJson),
                command.RequestPayloadHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The frozen terminal-cash payload does not match its persisted hash.");
        }

        var payload = JsonSerializer.Deserialize<TerminalCashPaymentRequest>(
            command.RequestPayloadJson,
            TerminalCashPaymentPayloadFactory.JsonOptions)
            ?? throw new InvalidOperationException("The frozen terminal-cash payload is unavailable.");
        var tender = await dbContext.CashTenders
            .AsNoTracking()
            .Include(value => value.CashCustodySession)
            .Include(value => value.Events)
                .ThenInclude(value => value.DenominationEntries)
            .SingleOrDefaultAsync(value => value.Id == command.TerminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
        if (tender is null || tender.CashCustodySession is null)
        {
            throw new InvalidOperationException("The durable cash tender or its custody ownership is unavailable.");
        }

        var cashReceivedEvents = tender.Events
            .Where(value => value.EventType == CashTenderEventType.CashReceived)
            .ToArray();
        if (cashReceivedEvents.Length != 1)
        {
            throw new InvalidOperationException("STALE_TARIFF recovery requires exactly one durable CASH_RECEIVED event.");
        }

        var receivedEvent = cashReceivedEvents[0];
        var custody = tender.CashCustodySession;
        var tenderParkingSessionId = ParseGuid(tender.ParkingSessionId, nameof(tender.ParkingSessionId));
        var tenderTariffSnapshotId = ParseGuid(tender.TariffSnapshotId, nameof(tender.TariffSnapshotId));
        var custodySiteId = ParseGuid(custody.SiteId, nameof(custody.SiteId));
        var custodySiteGroupId = ParseGuid(custody.SiteGroupId, nameof(custody.SiteGroupId));
        var linkageMatches =
            tender.CurrentLocalState == CashTenderState.CashReceived
            && receivedEvent.CashierAttested
            && payload.TerminalCashTenderId == tender.Id
            && payload.TerminalCashTenderId == command.TerminalCashTenderId
            && payload.CashCustodySessionId == tender.CashCustodySessionId
            && payload.CashCustodySessionId == command.CashCustodySessionId
            && payload.ParkingSessionId == tenderParkingSessionId
            && payload.TariffSnapshotId == tenderTariffSnapshotId
            && string.Equals(payload.Currency, tender.Currency, StringComparison.Ordinal)
            && payload.AmountDueMinorUnits == ToMinorUnits(tender.AmountDue)
            && payload.AmountTenderedMinorUnits == ToMinorUnits(tender.AmountTendered)
            && payload.ChangeDueMinorUnits == ToMinorUnits(tender.ChangeDue)
            && payload.CashReceivedAt == receivedEvent.OccurredAt
            && string.Equals(payload.LocalEventReference, receivedEvent.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
            && payload.AmountTenderedMinorUnits == ToMinorUnits(receivedEvent.AmountTendered)
            && payload.ChangeDueMinorUnits == ToMinorUnits(receivedEvent.ChangeDue)
            && string.Equals(payload.CashierId, receivedEvent.ActorCashierId, StringComparison.Ordinal)
            && string.Equals(receivedEvent.CorrelationId, tender.CorrelationId, StringComparison.Ordinal)
            && string.Equals(command.IdempotencyKey, tender.LocalIdempotencyIdentity, StringComparison.Ordinal)
            && string.Equals(command.OriginalCorrelationId, tender.CorrelationId, StringComparison.Ordinal)
            && payload.CashierId == custody.CashierId
            && payload.CashierSessionReference == custody.AuthenticatedCashierSessionReference
            && payload.CashierShiftId == custody.CashierShiftId
            && payload.TerminalId == custody.TerminalId
            && payload.SiteId == custodySiteId
            && payload.SiteGroupId == custodySiteGroupId
            && payload.PosServerId == custody.PosServerId;
        if (!linkageMatches)
        {
            throw new InvalidOperationException("The frozen terminal-cash payload does not match the durable CASH_RECEIVED evidence.");
        }

        var persistedDenominations = receivedEvent.DenominationEntries
            .Where(value => value.Quantity > 0)
            .Select(value => new TerminalCashDenominationEntry(
                value.DenominationCode,
                ToMinorUnits(value.DenominationValue),
                value.Quantity))
            .OrderBy(value => value.DenominationCode, StringComparer.Ordinal)
            .ThenBy(value => value.DenominationValueMinorUnits)
            .ThenBy(value => value.Quantity)
            .ToArray();
        var payloadDenominations = (payload.DenominationEntries ?? [])
            .OrderBy(value => value.DenominationCode, StringComparer.Ordinal)
            .ThenBy(value => value.DenominationValueMinorUnits)
            .ThenBy(value => value.Quantity)
            .ToArray();
        if (!persistedDenominations.SequenceEqual(payloadDenominations))
        {
            throw new InvalidOperationException("The frozen terminal-cash denominations do not match the durable CASH_RECEIVED evidence.");
        }
    }

    private static Guid ParseGuid(string value, string fieldName) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{fieldName} is not a valid recovery identifier.");

    private static long ToMinorUnits(decimal amount) =>
        decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_configurationError is not null)
        {
            throw _configurationError;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    private CashJournalDbContext CreateDbContext()
    {
        var service = new CashJournalService(_options);
        return service.CreateDbContext();
    }

    private void RebindNeverAttemptedPlaceholder(TerminalCashPaymentOutboxCommand command)
    {
        if (!string.Equals(command.CentralPmsTarget, "UNCONFIGURED_CENTRAL_PMS", StringComparison.Ordinal)
            || command.Status != TerminalCashPaymentCommandStatus.Pending
            || command.AttemptCount != 0
            || command.Attempts.Count != 0
            || command.FirstAttemptedAt is not null
            || command.LastAttemptedAt is not null)
        {
            return;
        }

        if (!_options.EnableCentralPmsCashSubmission
            || !Uri.TryCreate(_options.CentralPmsBaseUrl, UriKind.Absolute, out var configuredTarget)
            || configuredTarget.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(configuredTarget.Host)
            || configuredTarget.Host.EndsWith(".example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Central PMS cash submission is not configured for this pending local command.");
        }

        command.CentralPmsTarget = configuredTarget.ToString().TrimEnd('/');
        command.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task RecordAttemptAndApplyResultAsync<T>(
        CashJournalDbContext dbContext,
        TerminalCashPaymentOutboxCommand command,
        TerminalCashPaymentOutboxOperationType operationType,
        CentralPmsTerminalCashPaymentResult<T> result,
        CancellationToken cancellationToken)
        where T : class
    {
        var now = DateTimeOffset.UtcNow;
        var lastSequence = await dbContext.TerminalCashPaymentSubmissionAttempts
            .Where(attempt => attempt.LocalCommandId == command.Id)
            .Select(attempt => (int?)attempt.AttemptSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        var sequence = lastSequence + 1;

        command.AttemptCount++;
        command.FirstAttemptedAt ??= now;
        command.LastAttemptedAt = now;
        command.LastSafeHttpStatus = result.HttpStatus;
        command.LastSafeErrorCode = result.SafeErrorCode;
        command.UpdatedAt = now;

        dbContext.TerminalCashPaymentSubmissionAttempts.Add(new TerminalCashPaymentSubmissionAttempt
        {
            Id = Guid.NewGuid(),
            LocalCommandId = command.Id,
            OperationType = operationType,
            AttemptSequence = sequence,
            StartedAt = now,
            CompletedAt = now,
            OutcomeClassification = result.Outcome,
            HttpStatus = result.HttpStatus,
            SafeErrorCode = result.SafeErrorCode,
            CorrelationId = command.OriginalCorrelationId
        });

        ApplyResult(command, result);
        if (command.Status == TerminalCashPaymentCommandStatus.Confirmed)
        {
            await TerminalCashFiscalSubmissionService.EnsureCommandForConfirmedPaymentAsync(
                    dbContext,
                    command,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyResult<T>(TerminalCashPaymentOutboxCommand command, CentralPmsTerminalCashPaymentResult<T> result)
        where T : class
    {
        switch (result.Outcome)
        {
            case TerminalCashPaymentAttemptOutcome.Confirmed:
                ApplyConfirmed(command, result.Payload!);
                break;
            case TerminalCashPaymentAttemptOutcome.Conflict:
                command.Status = TerminalCashPaymentCommandStatus.Conflict;
                command.ResultClassification = "CONFLICT";
                break;
            case TerminalCashPaymentAttemptOutcome.Rejected:
                command.Status = TerminalCashPaymentCommandStatus.Rejected;
                command.ResultClassification = "REJECTED";
                break;
            case TerminalCashPaymentAttemptOutcome.Timeout:
                command.Status = TerminalCashPaymentCommandStatus.ReadbackRequired;
                command.ResultClassification = "UNCERTAIN";
                break;
            case TerminalCashPaymentAttemptOutcome.Unavailable:
            case TerminalCashPaymentAttemptOutcome.Unknown:
                command.Status = TerminalCashPaymentCommandStatus.RetryPending;
                command.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
                command.ResultClassification = result.Outcome.ToString().ToUpperInvariant();
                break;
            case TerminalCashPaymentAttemptOutcome.NotFound:
                command.Status = TerminalCashPaymentCommandStatus.Pending;
                command.ResultClassification = "READBACK_NOT_FOUND";
                break;
        }
    }

    private static void ApplyConfirmed<T>(TerminalCashPaymentOutboxCommand command, T payload)
        where T : class
    {
        command.Status = TerminalCashPaymentCommandStatus.Confirmed;
        command.ResultClassification = ReadProperty<string>(payload, "ResultClassification") ?? "CONFIRMED";
        command.CanonicalPaymentConfirmationId = ReadProperty<Guid>(payload, "PaymentConfirmationId");
        command.CanonicalPaymentAttemptId = ReadProperty<Guid>(payload, "PaymentAttemptId");
        command.ConfirmedAt = ReadProperty<DateTimeOffset>(payload, "ConfirmedAt");
        command.NextRetryAt = null;
    }

    private static T? ReadProperty<T>(object payload, string propertyName)
    {
        var property = payload.GetType().GetProperty(propertyName);
        if (property is null)
        {
            return default;
        }

        return property.GetValue(payload) is T value ? value : default;
    }
}
