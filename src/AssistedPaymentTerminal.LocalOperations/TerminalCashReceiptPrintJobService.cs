using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashReceiptPrintJobService
{
    private static readonly TerminalCashReceiptPrintJobStatus[] ActiveStatuses =
    [
        TerminalCashReceiptPrintJobStatus.Requested,
        TerminalCashReceiptPrintJobStatus.Preparing,
        TerminalCashReceiptPrintJobStatus.SubmissionPending
    ];

    private static readonly TerminalCashReceiptPrintJobStatus[] OriginalConsumedStatuses =
    [
        TerminalCashReceiptPrintJobStatus.SubmittedToSpooler,
        TerminalCashReceiptPrintJobStatus.Completed,
        TerminalCashReceiptPrintJobStatus.UnknownAfterRestart
    ];

    private readonly LocalOperationsDatabaseOptions _options;
    private readonly LocalOperationsDatabaseConfigurationException? _configurationError;
    private readonly Func<DateTimeOffset> _utcNow;

    public TerminalCashReceiptPrintJobService(LocalOperationsDatabaseOptions options, Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        try
        {
            DatabasePath = LocalOperationsDatabasePath.Resolve(options.DatabasePath);
            _options = options with { DatabasePath = DatabasePath };
        }
        catch (LocalOperationsDatabaseConfigurationException ex)
        {
            DatabasePath = options.DatabasePath ?? string.Empty;
            _options = options;
            _configurationError = ex;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DatabasePath = options.DatabasePath ?? string.Empty;
            _options = options;
            _configurationError = new LocalOperationsDatabaseConfigurationException(
                "The configured local operations database path is invalid.");
        }
    }

    public string DatabasePath { get; }

    public async Task<IReadOnlyList<TerminalCashReceiptPrintJob>> GetJobsForTenderAsync(
        Guid terminalCashTenderId,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .Where(job => job.TerminalCashTenderId == terminalCashTenderId)
            .OrderBy(job => job.CopySequence)
            .ThenBy(job => job.RequestedAt)
            .Take(Math.Clamp(maxResults, 1, 250))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalCashReceiptPrintJob>> GetJobsForFiscalDocumentAsync(
        Guid fiscalDocumentId,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .Where(job => job.PosFiscalDocumentId == fiscalDocumentId)
            .OrderBy(job => job.CopySequence)
            .ThenBy(job => job.RequestedAt)
            .Take(Math.Clamp(maxResults, 1, 250))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalCashReceiptPrintJob>> GetRecentJobsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .OrderByDescending(job => job.RequestedAt)
            .ThenByDescending(job => job.LastUpdatedAt)
            .Take(Math.Clamp(maxResults, 1, 250))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalCashReceiptPrintJob?> GetJobAsync(
        Guid printJobId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        return await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == printJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalCashReceiptPrintJob> RequestPrintJobAsync(
        TerminalCashReceiptRetrievalCommand receipt,
        string configuredPrinterName,
        int paperWidthMm,
        string paperProfileId,
        string correlationId,
        string? requestedBy = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (receipt.Status is not (TerminalCashReceiptRetrievalStatus.Available or TerminalCashReceiptRetrievalStatus.Voided))
        {
            throw new InvalidOperationException("Sales Invoice printing requires an available authoritative receipt presentation.");
        }

        if (string.IsNullOrWhiteSpace(receipt.FiscalDocumentNumber)
            || string.IsNullOrWhiteSpace(receipt.PresentationVersion)
            || string.IsNullOrWhiteSpace(receipt.TemplateVersion)
            || string.IsNullOrWhiteSpace(receipt.AuthoritativePayloadHash))
        {
            throw new InvalidOperationException("Sales Invoice printing requires complete fiscal identity and presentation evidence.");
        }

        await using var dbContext = CreateDbContext();
        var active = await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .AnyAsync(
                job => job.PosFiscalDocumentId == receipt.PosFiscalDocumentId
                    && job.AuthoritativePayloadHash == receipt.AuthoritativePayloadHash
                    && ActiveStatuses.Contains(job.Status),
                cancellationToken)
            .ConfigureAwait(false);

        if (active)
        {
            throw new InvalidOperationException("A Sales Invoice print request is already in progress for this fiscal document.");
        }

        var originalConsumed = await dbContext.TerminalCashReceiptPrintJobs
            .AsNoTracking()
            .AnyAsync(
                job => job.PosFiscalDocumentId == receipt.PosFiscalDocumentId
                    && job.AuthoritativePayloadHash == receipt.AuthoritativePayloadHash
                    && OriginalConsumedStatuses.Contains(job.Status),
                cancellationToken)
            .ConfigureAwait(false);

        var nextCopySequence = (await dbContext.TerminalCashReceiptPrintJobs
            .Where(job => job.PosFiscalDocumentId == receipt.PosFiscalDocumentId
                && job.AuthoritativePayloadHash == receipt.AuthoritativePayloadHash)
            .Select(job => (int?)job.CopySequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;

        var now = _utcNow();
        var job = new TerminalCashReceiptPrintJob
        {
            Id = Guid.NewGuid(),
            TerminalCashTenderId = receipt.TerminalCashTenderId,
            LocalReceiptRetrievalId = receipt.Id,
            FiscalIssuanceReferenceId = receipt.FiscalIssuanceReferenceId,
            PosFiscalDocumentId = receipt.PosFiscalDocumentId,
            FiscalDocumentNumber = receipt.FiscalDocumentNumber,
            PresentationVersion = receipt.PresentationVersion,
            TemplateVersion = receipt.TemplateVersion,
            AuthoritativePayloadHash = receipt.AuthoritativePayloadHash,
            SemanticRequestHash = receipt.SemanticRequestHash,
            PaperWidthMm = paperWidthMm,
            PaperProfileId = paperProfileId,
            ConfiguredPrinterName = configuredPrinterName.Trim(),
            Classification = originalConsumed
                ? TerminalCashReceiptPrintClassification.Reprint
                : TerminalCashReceiptPrintClassification.Original,
            CopySequence = nextCopySequence,
            Status = TerminalCashReceiptPrintJobStatus.Requested,
            RequestedAt = now,
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim(),
            LastUpdatedAt = now,
            CorrelationId = correlationId
        };

        dbContext.TerminalCashReceiptPrintJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public Task<TerminalCashReceiptPrintJob> MarkPreparingAsync(Guid printJobId, CancellationToken cancellationToken = default) =>
        UpdateAsync(printJobId, job =>
        {
            var now = _utcNow();
            job.Status = TerminalCashReceiptPrintJobStatus.Preparing;
            job.SubmissionStartedAt = now;
            job.LastUpdatedAt = now;
        }, cancellationToken);

    public Task<TerminalCashReceiptPrintJob> MarkSubmittedToSpoolerAsync(
        Guid printJobId,
        string? windowsSpoolerJobId,
        DateTimeOffset? submittedToSpoolerAt = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(printJobId, job =>
        {
            var now = submittedToSpoolerAt ?? _utcNow();
            job.Status = TerminalCashReceiptPrintJobStatus.SubmittedToSpooler;
            job.SubmittedToSpoolerAt = now;
            job.WindowsSpoolerJobId = string.IsNullOrWhiteSpace(windowsSpoolerJobId) ? null : windowsSpoolerJobId.Trim();
            job.Retryable = false;
            job.LastUpdatedAt = now;
        }, cancellationToken);

    public Task<TerminalCashReceiptPrintJob> MarkFailedAsync(
        Guid printJobId,
        TerminalCashReceiptPrintJobStatus status,
        string failureClassification,
        bool retryable,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(printJobId, job =>
        {
            var now = _utcNow();
            job.Status = status;
            job.FailedAt = now;
            job.FailureClassification = failureClassification;
            job.Retryable = retryable;
            job.LastUpdatedAt = now;
        }, cancellationToken);

    public async Task MarkInterruptedSubmissionsUnknownAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        var interrupted = await dbContext.TerminalCashReceiptPrintJobs
            .Where(job => job.Status == TerminalCashReceiptPrintJobStatus.Preparing
                || job.Status == TerminalCashReceiptPrintJobStatus.SubmissionPending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = _utcNow();
        foreach (var job in interrupted)
        {
            job.Status = TerminalCashReceiptPrintJobStatus.UnknownAfterRestart;
            job.Retryable = false;
            job.FailureClassification = "SPOOLER_OUTCOME_UNKNOWN_AFTER_RESTART";
            job.LastUpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerminalCashReceiptPrintJob> UpdateAsync(
        Guid printJobId,
        Action<TerminalCashReceiptPrintJob> update,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var dbContext = CreateDbContext();
        var job = await dbContext.TerminalCashReceiptPrintJobs
            .SingleAsync(value => value.Id == printJobId, cancellationToken)
            .ConfigureAwait(false);
        update(job);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return job;
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
        await EnsurePrintJobSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    private CashJournalDbContext CreateDbContext()
    {
        var service = new CashJournalService(_options);
        return service.CreateDbContext();
    }

    private static async Task EnsurePrintJobSchemaAsync(CashJournalDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS terminal_cash_receipt_print_jobs (
                    Id TEXT NOT NULL CONSTRAINT PK_terminal_cash_receipt_print_jobs PRIMARY KEY,
                    TerminalCashTenderId TEXT NOT NULL,
                    LocalReceiptRetrievalId TEXT NOT NULL,
                    FiscalIssuanceReferenceId TEXT NOT NULL,
                    PosFiscalDocumentId TEXT NOT NULL,
                    FiscalDocumentNumber TEXT NOT NULL,
                    PresentationVersion TEXT NOT NULL,
                    TemplateVersion TEXT NOT NULL,
                    AuthoritativePayloadHash TEXT NOT NULL,
                    SemanticRequestHash TEXT NULL,
                    PaperWidthMm INTEGER NOT NULL,
                    PaperProfileId TEXT NOT NULL,
                    ConfiguredPrinterName TEXT NOT NULL,
                    Classification TEXT NOT NULL,
                    CopySequence INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    RequestedAt INTEGER NOT NULL,
                    RequestedBy TEXT NULL,
                    SubmissionStartedAt INTEGER NULL,
                    SubmittedToSpoolerAt INTEGER NULL,
                    CompletedAt INTEGER NULL,
                    FailedAt INTEGER NULL,
                    FailureClassification TEXT NULL,
                    Retryable INTEGER NOT NULL,
                    WindowsSpoolerJobId TEXT NULL,
                    LastUpdatedAt INTEGER NOT NULL,
                    CorrelationId TEXT NOT NULL,
                    CONSTRAINT FK_terminal_cash_receipt_print_jobs_terminal_cash_receipt_retrieval_commands_LocalReceiptRetrievalId
                        FOREIGN KEY (LocalReceiptRetrievalId) REFERENCES terminal_cash_receipt_retrieval_commands (Id) ON DELETE RESTRICT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_terminal_cash_receipt_print_jobs_PosFiscalDocumentId_AuthoritativePayloadHash_CopySequence
                    ON terminal_cash_receipt_print_jobs (PosFiscalDocumentId, AuthoritativePayloadHash, CopySequence);
                CREATE INDEX IF NOT EXISTS IX_terminal_cash_receipt_print_jobs_TerminalCashTenderId_Status
                    ON terminal_cash_receipt_print_jobs (TerminalCashTenderId, Status);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
