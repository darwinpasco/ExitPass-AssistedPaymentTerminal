using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashReceiptRetrievalService
{
    private static readonly string[] RecordedFiscalStates =
    [
        "FISCAL_ISSUANCE_RECORDED",
        "FISCAL_ISSUANCE_REPLAYED",
        "FISCAL_ISSUANCE_RECONCILED"
    ];

    private readonly LocalOperationsDatabaseOptions _options;
    private readonly ICentralPmsTerminalCashReceiptClient _client;
    private readonly LocalOperationsDatabaseConfigurationException? _configurationError;

    public TerminalCashReceiptRetrievalService(
        ICentralPmsTerminalCashReceiptClient client,
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

    public async Task<TerminalCashReceiptRetrievalCommand> EnsureForRecordedFiscalAsync(
        Guid terminalCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var fiscalCommand = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
        if (fiscalCommand is null)
        {
            throw new InvalidOperationException($"Fiscal outbox command for terminal cash tender '{terminalCashTenderId}' was not found.");
        }

        var receiptCommand = await EnsureCommandForRecordedFiscalAsync(
                dbContext,
                fiscalCommand,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receiptCommand;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            await using var recoveryContext = CreateDbContext();
            var existing = await recoveryContext.TerminalCashReceiptRetrievalCommands
                .AsNoTracking()
                .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task<TerminalCashReceiptRetrievalCommand> RetrieveReceiptAsync(
        Guid localReceiptRetrievalId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var command = await dbContext.TerminalCashReceiptRetrievalCommands
            .Include(value => value.Attempts)
            .SingleAsync(value => value.Id == localReceiptRetrievalId, cancellationToken)
            .ConfigureAwait(false);

        var result = await _client.RetrieveAsync(
                new Uri(command.CentralPmsTarget, UriKind.Absolute),
                command.TerminalCashTenderId,
                command.RetrievalCorrelationId,
                TimeSpan.FromSeconds(_options.CentralPmsTimeoutSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        await RecordAttemptAndApplyResultAsync(dbContext, command, result, cancellationToken).ConfigureAwait(false);
        return command;
    }

    public async Task<TerminalCashReceiptRetrievalCommand?> GetReceiptRetrievalByTenderAsync(
        Guid terminalCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptRetrievalCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalCashReceiptRetrievalAttempt>> GetReceiptAttemptsAsync(
        Guid localReceiptRetrievalId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptRetrievalAttempts
            .AsNoTracking()
            .Where(attempt => attempt.LocalReceiptRetrievalId == localReceiptRetrievalId)
            .OrderBy(attempt => attempt.AttemptSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TerminalCashReceiptRetrievalCommand> EnsureCommandForRecordedFiscalAsync(
        CashJournalDbContext dbContext,
        TerminalCashFiscalOutboxCommand fiscalCommand,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureRecordedFiscal(fiscalCommand);

        var existing = await dbContext.TerminalCashReceiptRetrievalCommands
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == fiscalCommand.TerminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureExistingMatchesFiscal(existing, fiscalCommand);
            return existing;
        }

        var command = new TerminalCashReceiptRetrievalCommand
        {
            Id = Guid.NewGuid(),
            TerminalCashTenderId = fiscalCommand.TerminalCashTenderId,
            RelatedCashPaymentOutboxCommandId = fiscalCommand.RelatedCashPaymentOutboxCommandId,
            RelatedFiscalCommandId = fiscalCommand.Id,
            CanonicalPaymentAttemptId = fiscalCommand.CanonicalPaymentAttemptId,
            CanonicalPaymentConfirmationId = fiscalCommand.CanonicalPaymentConfirmationId,
            FiscalIssuanceReferenceId = fiscalCommand.FiscalIssuanceReferenceId!.Value,
            PosFiscalDocumentId = fiscalCommand.PosFiscalDocumentId!.Value,
            RetrievalCorrelationId = Guid.NewGuid().ToString("D"),
            CentralPmsTarget = fiscalCommand.CentralPmsTarget,
            Status = TerminalCashReceiptRetrievalStatus.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.TerminalCashReceiptRetrievalCommands.Add(command);
        return command;
    }

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

    private static void EnsureRecordedFiscal(TerminalCashFiscalOutboxCommand fiscalCommand)
    {
        if (fiscalCommand.Status != TerminalCashFiscalCommandStatus.Recorded ||
            string.IsNullOrWhiteSpace(fiscalCommand.FiscalIssuanceState) ||
            !RecordedFiscalStates.Contains(fiscalCommand.FiscalIssuanceState, StringComparer.Ordinal) ||
            fiscalCommand.FiscalIssuanceReferenceId is null)
        {
            throw new InvalidOperationException($"Terminal cash tender '{fiscalCommand.TerminalCashTenderId}' does not have recorded fiscal state.");
        }

        if (fiscalCommand.PosFiscalDocumentId is null || fiscalCommand.PosFiscalDocumentId == Guid.Empty)
        {
            throw new InvalidOperationException($"Terminal cash tender '{fiscalCommand.TerminalCashTenderId}' does not have a POS fiscal document reference.");
        }
    }

    private static void EnsureExistingMatchesFiscal(
        TerminalCashReceiptRetrievalCommand existing,
        TerminalCashFiscalOutboxCommand fiscalCommand)
    {
        if (existing.RelatedFiscalCommandId != fiscalCommand.Id ||
            existing.CanonicalPaymentAttemptId != fiscalCommand.CanonicalPaymentAttemptId ||
            existing.CanonicalPaymentConfirmationId != fiscalCommand.CanonicalPaymentConfirmationId ||
            existing.FiscalIssuanceReferenceId != fiscalCommand.FiscalIssuanceReferenceId ||
            existing.PosFiscalDocumentId != fiscalCommand.PosFiscalDocumentId)
        {
            throw new InvalidOperationException("Existing receipt retrieval command is linked to different fiscal identifiers.");
        }
    }

    private static async Task RecordAttemptAndApplyResultAsync(
        CashJournalDbContext dbContext,
        TerminalCashReceiptRetrievalCommand command,
        CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse> result,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lastSequence = await dbContext.TerminalCashReceiptRetrievalAttempts
            .Where(attempt => attempt.LocalReceiptRetrievalId == command.Id)
            .Select(attempt => (int?)attempt.AttemptSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        command.AttemptCount++;
        command.FirstAttemptedAt ??= now;
        command.LastAttemptedAt = now;
        command.LastSafeHttpStatus = result.HttpStatus;
        command.LastSafeErrorCode = result.SafeErrorCode;
        command.UpdatedAt = now;

        dbContext.TerminalCashReceiptRetrievalAttempts.Add(new TerminalCashReceiptRetrievalAttempt
        {
            Id = Guid.NewGuid(),
            LocalReceiptRetrievalId = command.Id,
            AttemptSequence = lastSequence + 1,
            StartedAt = now,
            CompletedAt = now,
            OutcomeClassification = result.Outcome,
            HttpStatus = result.HttpStatus,
            SafeErrorCode = result.SafeErrorCode,
            CorrelationId = command.RetrievalCorrelationId
        });

        ApplyResult(command, result, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyResult(
        TerminalCashReceiptRetrievalCommand command,
        CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse> result,
        DateTimeOffset now)
    {
        switch (result.Outcome)
        {
            case TerminalCashReceiptRetrievalAttemptOutcome.Available:
                ApplyAvailable(command, result.Payload!, now);
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.NotReady:
                command.Status = TerminalCashReceiptRetrievalStatus.NotReady;
                command.ResultClassification = "NOT_READY";
                command.NextRetryAt = now.AddMinutes(1);
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.NotFound:
                command.Status = TerminalCashReceiptRetrievalStatus.Inconsistent;
                command.ResultClassification = "NOT_FOUND";
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.Rejected:
                command.Status = TerminalCashReceiptRetrievalStatus.Rejected;
                command.ResultClassification = "REJECTED";
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.Inconsistent:
                command.Status = TerminalCashReceiptRetrievalStatus.Inconsistent;
                command.ResultClassification = "INCONSISTENT";
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.Timeout:
                command.Status = TerminalCashReceiptRetrievalStatus.RetryPending;
                command.ResultClassification = "UNCERTAIN";
                command.NextRetryAt = now.AddMinutes(1);
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.Unavailable:
                command.Status = command.AuthoritativePresentationJson is null
                    ? TerminalCashReceiptRetrievalStatus.Unavailable
                    : command.Status;
                command.ResultClassification = "UNAVAILABLE";
                command.NextRetryAt = now.AddMinutes(1);
                break;
            case TerminalCashReceiptRetrievalAttemptOutcome.Unknown:
                command.Status = TerminalCashReceiptRetrievalStatus.RetryPending;
                command.ResultClassification = "UNKNOWN";
                command.NextRetryAt = now.AddMinutes(1);
                break;
        }
    }

    private static void ApplyAvailable(
        TerminalCashReceiptRetrievalCommand command,
        TerminalCashReceiptPresentationResponse payload,
        DateTimeOffset now)
    {
        if (payload.TerminalCashTenderId != command.TerminalCashTenderId ||
            payload.PaymentAttemptId != command.CanonicalPaymentAttemptId ||
            payload.PaymentConfirmationId != command.CanonicalPaymentConfirmationId ||
            payload.FiscalIssuanceReferenceId != command.FiscalIssuanceReferenceId ||
            payload.PosFiscalDocumentId != command.PosFiscalDocumentId)
        {
            command.Status = TerminalCashReceiptRetrievalStatus.Inconsistent;
            command.ResultClassification = "REFERENCE_MISMATCH";
            return;
        }

        var authoritativePayload = TerminalCashReceiptPayloadFactory.Serialize(payload.AuthoritativePresentation);
        command.Status = string.Equals(payload.VoidStatus, "voided", StringComparison.OrdinalIgnoreCase)
            ? TerminalCashReceiptRetrievalStatus.Voided
            : TerminalCashReceiptRetrievalStatus.Available;
        command.ResultClassification = payload.ReceiptAvailabilityState;
        command.ReceiptAvailabilityState = payload.ReceiptAvailabilityState;
        command.FiscalDocumentNumber = payload.FiscalDocumentNumber;
        command.FiscalDocumentStatus = payload.FiscalDocumentStatus;
        command.PresentationVersion = payload.PresentationVersion;
        command.TemplateVersion = payload.TemplateVersion;
        command.ContentType = payload.ContentType;
        command.AuthoritativePresentationJson = authoritativePayload;
        command.AuthoritativePayloadHash = TerminalCashReceiptPayloadFactory.ComputeHash(authoritativePayload);
        command.VoidStatus = payload.VoidStatus;
        command.VoidReasonCode = payload.VoidReasonCode;
        command.VoidedAt = payload.VoidedAt;
        command.RetrievedAt = now;
        command.NextRetryAt = null;
    }
}
