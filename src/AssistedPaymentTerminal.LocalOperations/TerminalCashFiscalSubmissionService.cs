using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashFiscalSubmissionService
{
    private static readonly string[] RecordedFiscalStates =
    [
        "FISCAL_ISSUANCE_RECORDED",
        "FISCAL_ISSUANCE_REPLAYED",
        "FISCAL_ISSUANCE_RECONCILED"
    ];

    private static readonly string[] ConflictFiscalStates =
    [
        "FISCAL_ISSUANCE_CONFLICT"
    ];

    private static readonly string[] RejectedFiscalStates =
    [
        "FISCAL_ISSUANCE_FAILED_REQUEST",
        "FISCAL_ISSUANCE_FAILED_CONFIGURATION"
    ];

    private readonly LocalOperationsDatabaseOptions _options;
    private readonly ICentralPmsTerminalCashFiscalClient _client;

    public TerminalCashFiscalSubmissionService(
        ICentralPmsTerminalCashFiscalClient client,
        LocalOperationsDatabaseOptions? options = null)
    {
        _client = client;
        _options = options ?? new LocalOperationsDatabaseOptions();
        DatabasePath = LocalOperationsDatabasePath.Resolve(_options.DatabasePath);
    }

    public string DatabasePath { get; }

    public async Task<TerminalCashFiscalOutboxCommand> EnsureForConfirmedPaymentAsync(
        Guid terminalCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var paymentCommand = await dbContext.TerminalCashPaymentOutboxCommands
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
        if (paymentCommand is null)
        {
            throw new InvalidOperationException($"Cash-payment outbox command for terminal cash tender '{terminalCashTenderId}' was not found.");
        }

        if (paymentCommand.Status != TerminalCashPaymentCommandStatus.Confirmed ||
            paymentCommand.CanonicalPaymentAttemptId is null ||
            paymentCommand.CanonicalPaymentConfirmationId is null)
        {
            throw new InvalidOperationException($"Terminal cash tender '{terminalCashTenderId}' does not have confirmed canonical payment state.");
        }

        var fiscalCommand = await EnsureCommandForConfirmedPaymentAsync(
                dbContext,
                paymentCommand,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return fiscalCommand;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            await using var recoveryContext = CreateDbContext();
            var existing = await recoveryContext.TerminalCashFiscalOutboxCommands
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

    public async Task<TerminalCashFiscalOutboxCommand> SubmitOrReadbackFiscalAsync(
        Guid localFiscalCommandId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var command = await dbContext.TerminalCashFiscalOutboxCommands
            .Include(value => value.Attempts)
            .SingleAsync(value => value.Id == localFiscalCommandId, cancellationToken)
            .ConfigureAwait(false);

        if (command.Status is TerminalCashFiscalCommandStatus.Recorded
            or TerminalCashFiscalCommandStatus.Conflict
            or TerminalCashFiscalCommandStatus.Rejected)
        {
            return command;
        }

        if (command.Attempts.Count > 0)
        {
            var readback = await _client.ReadbackAsync(
                new Uri(command.CentralPmsTarget, UriKind.Absolute),
                command.TerminalCashTenderId,
                command.FiscalCorrelationId,
                TimeSpan.FromSeconds(_options.CentralPmsTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            await RecordAttemptAndApplyResultAsync(
                dbContext,
                command,
                TerminalCashFiscalOperationType.Readback,
                readback,
                cancellationToken).ConfigureAwait(false);

            if (readback.Outcome == TerminalCashFiscalAttemptOutcome.Recorded)
            {
                return command;
            }

            if (readback.Outcome != TerminalCashFiscalAttemptOutcome.NotFound)
            {
                return command;
            }
        }

        var payload = JsonSerializer.Deserialize<TerminalCashFiscalIssuanceRequest>(
            command.RequestRepresentationJson,
            TerminalCashPaymentPayloadFactory.JsonOptions)!;

        var submit = await _client.SubmitAsync(
            new Uri(command.CentralPmsTarget, UriKind.Absolute),
            command.TerminalCashTenderId,
            payload,
            command.FiscalIdempotencyKey,
            command.FiscalCorrelationId,
            TimeSpan.FromSeconds(_options.CentralPmsTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        await RecordAttemptAndApplyResultAsync(
            dbContext,
            command,
            TerminalCashFiscalOperationType.Submit,
            submit,
            cancellationToken).ConfigureAwait(false);

        return command;
    }

    public async Task<TerminalCashFiscalOutboxCommand?> GetFiscalCommandByTenderAsync(
        Guid terminalCashTenderId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashFiscalOutboxCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == terminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalCashFiscalAttempt>> GetFiscalAttemptsAsync(
        Guid localFiscalCommandId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashFiscalAttempts
            .AsNoTracking()
            .Where(attempt => attempt.LocalFiscalCommandId == localFiscalCommandId)
            .OrderBy(attempt => attempt.AttemptSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TerminalCashFiscalOutboxCommand> EnsureCommandForConfirmedPaymentAsync(
        CashJournalDbContext dbContext,
        TerminalCashPaymentOutboxCommand paymentCommand,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (paymentCommand.Status != TerminalCashPaymentCommandStatus.Confirmed ||
            paymentCommand.CanonicalPaymentAttemptId is null ||
            paymentCommand.CanonicalPaymentConfirmationId is null)
        {
            throw new InvalidOperationException("Fiscal command creation requires confirmed Central PMS payment state.");
        }

        var existing = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleOrDefaultAsync(command => command.TerminalCashTenderId == paymentCommand.TerminalCashTenderId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.CanonicalPaymentAttemptId != paymentCommand.CanonicalPaymentAttemptId.Value ||
                existing.CanonicalPaymentConfirmationId != paymentCommand.CanonicalPaymentConfirmationId.Value)
            {
                throw new InvalidOperationException("Existing fiscal command is linked to different canonical payment identifiers.");
            }

            return existing;
        }

        var request = TerminalCashFiscalPayloadFactory.CreateRequest();
        var requestRepresentation = TerminalCashFiscalPayloadFactory.Serialize(request);
        var command = new TerminalCashFiscalOutboxCommand
        {
            Id = Guid.NewGuid(),
            TerminalCashTenderId = paymentCommand.TerminalCashTenderId,
            RelatedCashPaymentOutboxCommandId = paymentCommand.Id,
            CanonicalPaymentAttemptId = paymentCommand.CanonicalPaymentAttemptId.Value,
            CanonicalPaymentConfirmationId = paymentCommand.CanonicalPaymentConfirmationId.Value,
            RequestRepresentationJson = requestRepresentation,
            RequestHash = TerminalCashFiscalPayloadFactory.ComputeHash(requestRepresentation),
            FiscalIdempotencyKey = $"terminal-cash-fiscal-{paymentCommand.TerminalCashTenderId:N}",
            FiscalCorrelationId = Guid.NewGuid().ToString("D"),
            CentralPmsTarget = paymentCommand.CentralPmsTarget,
            Status = TerminalCashFiscalCommandStatus.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.TerminalCashFiscalOutboxCommands.Add(command);
        return command;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    private CashJournalDbContext CreateDbContext()
    {
        var service = new CashJournalService(_options);
        return service.CreateDbContext();
    }

    private static async Task RecordAttemptAndApplyResultAsync(
        CashJournalDbContext dbContext,
        TerminalCashFiscalOutboxCommand command,
        TerminalCashFiscalOperationType operationType,
        CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse> result,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lastSequence = await dbContext.TerminalCashFiscalAttempts
            .Where(attempt => attempt.LocalFiscalCommandId == command.Id)
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

        dbContext.TerminalCashFiscalAttempts.Add(new TerminalCashFiscalAttempt
        {
            Id = Guid.NewGuid(),
            LocalFiscalCommandId = command.Id,
            OperationType = operationType,
            AttemptSequence = sequence,
            StartedAt = now,
            CompletedAt = now,
            OutcomeClassification = result.Outcome,
            HttpStatus = result.HttpStatus,
            SafeErrorCode = result.SafeErrorCode,
            CorrelationId = command.FiscalCorrelationId
        });

        ApplyResult(command, operationType, result);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyResult(
        TerminalCashFiscalOutboxCommand command,
        TerminalCashFiscalOperationType operationType,
        CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse> result)
    {
        switch (result.Outcome)
        {
            case TerminalCashFiscalAttemptOutcome.Recorded:
                ApplyFiscalResponse(command, result.Payload!);
                break;
            case TerminalCashFiscalAttemptOutcome.Conflict:
                command.Status = TerminalCashFiscalCommandStatus.Conflict;
                command.ResultClassification = "CONFLICT";
                break;
            case TerminalCashFiscalAttemptOutcome.Rejected:
                command.Status = TerminalCashFiscalCommandStatus.Rejected;
                command.ResultClassification = "REJECTED";
                break;
            case TerminalCashFiscalAttemptOutcome.Timeout:
                command.Status = TerminalCashFiscalCommandStatus.ReadbackRequired;
                command.ResultClassification = "UNCERTAIN";
                break;
            case TerminalCashFiscalAttemptOutcome.Unavailable:
                command.Status = TerminalCashFiscalCommandStatus.RetryPending;
                command.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
                command.ResultClassification = "UNAVAILABLE";
                break;
            case TerminalCashFiscalAttemptOutcome.Unknown:
                command.Status = TerminalCashFiscalCommandStatus.Unknown;
                command.ResultClassification = "UNKNOWN";
                break;
            case TerminalCashFiscalAttemptOutcome.NotFound:
                command.Status = operationType == TerminalCashFiscalOperationType.Readback
                    ? TerminalCashFiscalCommandStatus.Pending
                    : TerminalCashFiscalCommandStatus.Rejected;
                command.ResultClassification = operationType == TerminalCashFiscalOperationType.Readback
                    ? "READBACK_NOT_FOUND"
                    : "NOT_FOUND";
                break;
        }
    }

    private static void ApplyFiscalResponse(
        TerminalCashFiscalOutboxCommand command,
        TerminalCashFiscalIssuanceResponse payload)
    {
        command.FiscalIssuanceReferenceId = payload.FiscalIssuanceReferenceId;
        command.FiscalIssuanceState = payload.FiscalIssuanceState;
        command.ResultClassification = payload.ResultClassification ?? payload.FiscalIssuanceState;
        command.PosFiscalDocumentId = payload.PosFiscalDocumentId;
        command.FiscalDocumentNumber = payload.FiscalDocumentNumber;
        command.FiscalNumberAssignedAt = payload.FiscalNumberAssignedAt;
        command.SemanticHashSourceVersion = payload.SemanticHashSourceVersion;
        command.LastSafeErrorCode = payload.SafeErrorCode;
        command.UpdatedAt = payload.UpdatedAt;

        if (RecordedFiscalStates.Contains(payload.FiscalIssuanceState, StringComparer.Ordinal))
        {
            command.Status = TerminalCashFiscalCommandStatus.Recorded;
            command.RecordedAt = payload.FiscalNumberAssignedAt ?? payload.UpdatedAt;
            command.NextRetryAt = null;
            return;
        }

        if (ConflictFiscalStates.Contains(payload.FiscalIssuanceState, StringComparer.Ordinal))
        {
            command.Status = TerminalCashFiscalCommandStatus.Conflict;
            return;
        }

        if (RejectedFiscalStates.Contains(payload.FiscalIssuanceState, StringComparer.Ordinal))
        {
            command.Status = TerminalCashFiscalCommandStatus.Rejected;
            return;
        }

        command.Status = TerminalCashFiscalCommandStatus.ReadbackRequired;
        command.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
    }
}
