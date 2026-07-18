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
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = CreateDbContext();
        var command = await dbContext.TerminalCashPaymentOutboxCommands
            .Include(value => value.Attempts)
            .SingleAsync(value => value.Id == localCommandId, cancellationToken)
            .ConfigureAwait(false);

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

        if (command.Status is TerminalCashPaymentCommandStatus.Conflict
            or TerminalCashPaymentCommandStatus.Rejected)
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
