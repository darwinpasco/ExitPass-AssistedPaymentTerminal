using System.Security.Cryptography;
using System.Text;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class LocalDatabasePlaintextMigrationTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        "ExitPass.APT.LocalOperations.PlaintextMigration.Tests",
        Guid.NewGuid().ToString("N"));

    public LocalDatabasePlaintextMigrationTests()
    {
        Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ClassifiesPlaintextDatabaseWithoutEnvelopeAsMigrationRequired()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var result = await CreateService(databasePath).ClassifyAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationRequired, result.Status);
        Assert.True(result.MigrationRequired);
        Assert.Equal(1, result.SourceRowCounts["cashier_shifts"]);
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ExplicitMigrationExportsPlaintextSourceToEncryptedDatabaseAndEnvelope()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true, includePayableBasis: true);
        var sourceBytes = await File.ReadAllBytesAsync(databasePath);

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.True(result.Succeeded, $"{result.Status} {result.Phase} {result.SafeMessage} {result.SafeAction}");
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, result.Status);
        Assert.True(result.Succeeded);
        Assert.False(HasPlainSqliteHeader(databasePath));
        Assert.False(FileContains(databasePath, "cashier-001"));
        Assert.True(File.Exists(EnvelopePath(databasePath)));
        Assert.True(result.SourceRowCounts.SequenceEqual(result.TargetRowCounts));
        Assert.True(File.Exists(SourceQuarantinePath(databasePath)));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(SourceQuarantinePath(databasePath)));

        var restarted = CreateCashJournalService(databasePath);
        var state = await restarted.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());
        Assert.Equal(1, state.ActiveShiftRecordCount);
        Assert.Equal(1, state.ActiveCashCustodySessionRecordCount);
        Assert.Equal("shift-001", state.ActiveShift?.Id);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task HistoricalReceiptRetrievalSchemaClassifiesAsMigrationRequired()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);
        await ConvertToHistoricalReceiptRetrievalSchemaAsync(databasePath);

        var result = await CreateService(databasePath).ClassifyAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationRequired, result.Status);
        Assert.Equal(1, result.SourceRowCounts["cash_custody_sessions"]);
        Assert.Equal(1, result.SourceRowCounts["cash_tenders"]);
        Assert.Equal(1, result.SourceRowCounts["cash_tender_events"]);
        Assert.Equal(0, result.SourceRowCounts["cashier_shifts"]);
        Assert.Equal(0, result.SourceRowCounts["terminal_cash_receipt_print_jobs"]);
        Assert.Equal(0, result.SourceRowCounts["terminal_cash_payable_basis_states"]);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task HistoricalReceiptRetrievalSchemaMigratesToCurrentEncryptedSchemaAndPreservesRows()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);
        await ConvertToHistoricalReceiptRetrievalSchemaAsync(databasePath);
        var before = await CreateService(databasePath).ClassifyAsync();

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.True(result.Succeeded, $"{result.Status} {result.Phase} {result.SafeMessage} {result.SafeAction}");
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, result.Status);
        Assert.True(before.SourceRowCounts.SequenceEqual(result.SourceRowCounts));
        Assert.True(result.SourceRowCounts.SequenceEqual(result.TargetRowCounts));
        Assert.False(HasPlainSqliteHeader(databasePath));
        Assert.True(File.Exists(EnvelopePath(databasePath)));

        await using var dbContext = CreateCashJournalService(databasePath).CreateDbContext();
        Assert.Equal(1, await dbContext.CashCustodySessions.CountAsync());
        Assert.Equal(1, await dbContext.CashTenders.CountAsync());
        Assert.Equal(1, await dbContext.CashTenderEvents.CountAsync());
        Assert.Equal(0, await dbContext.CashierShifts.CountAsync());
        Assert.Equal(0, await dbContext.TerminalCashReceiptPrintJobs.CountAsync());
        Assert.Equal(0, await dbContext.TerminalCashPayableBasisStates.CountAsync());

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));
        }

        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_check;";
            Assert.Equal(0L, Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));
        }

        var repeated = await CreateService(databasePath, authorized: true).MigrateAsync();
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted, repeated.Status);
        Assert.True(repeated.SourceRowCounts.SequenceEqual(result.SourceRowCounts));
        Assert.True(repeated.TargetRowCounts.SequenceEqual(result.TargetRowCounts));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task HistoricalReceiptRetrievalSchemaNearMissRemainsUnsupported()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);
        await ConvertToHistoricalReceiptRetrievalSchemaAsync(databasePath);
        await ExecutePlaintextAsync(databasePath, "ALTER TABLE cash_tenders ADD COLUMN UnexpectedLegacyColumn TEXT NULL;");

        var result = await CreateService(databasePath).ClassifyAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.UnsupportedSchema, result.Status);
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task MigrationPreservesCommittedWalContent()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        await AddCommittedWalShiftAsync(databasePath);

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.True(result.Succeeded, $"{result.Status} {result.Phase} {result.SafeMessage} {result.SafeAction}");
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, result.Status);
        var state = await CreateCashJournalService(databasePath).GetLocalOperationalStateAsync();
        Assert.Equal(2, state.ActiveShiftRecordCount);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NormalStartupStillFailsClosedForPlaintextSource()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateCashJournalService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired, exception.SafeStatus);
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task MigrationRequiresExplicitAuthorization()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var result = await CreateService(databasePath, authorized: false).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.BlockedForSupport, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task PlaintextDatabaseWithMalformedEnvelopeFailsClosed()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        await File.WriteAllTextAsync(EnvelopePath(databasePath), "synthetic non-production envelope conflict");

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.KeyEnvelopeMalformed, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.Equal("synthetic non-production envelope conflict", await File.ReadAllTextAsync(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task PlaintextDatabaseWithValidExistingEnvelopeBlocksAsConflict()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        WriteValidEnvelope(databasePath);

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.ExistingEnvelopeConflict, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.True(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task LockedSourceBlocksBeforeBackupOrCutover()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        using var sourceLock = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.SourceLocked, result.Status);
        Assert.True(File.Exists(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ApplicationRunningBlocksMigration()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var result = await CreateService(databasePath, authorized: true, isAppRunning: () => true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.ApplicationRunning, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InsufficientDiskBlocksBeforeBackup()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var result = await CreateService(databasePath, authorized: true, availableDiskBytes: () => 1).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.InsufficientDisk, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task UnsupportedPlaintextSchemaBlocks()
    {
        var databasePath = DatabasePath();
        await CreateUnsupportedPlaintextDatabaseAsync(databasePath);

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.UnsupportedSchema, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CorruptPlaintextHeaderBlocksAsSourceCorrupt()
    {
        var databasePath = DatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllBytesAsync(databasePath, Encoding.ASCII.GetBytes("SQLite format 3\0synthetic corrupt source"));

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.SourceCorrupt, result.Status);
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ExistingTargetConflictBlocksWithoutDeletingTarget()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var targetPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "PlaintextMigration", "cash-journal.encrypted-target.db");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "synthetic target conflict");

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.ExistingTargetConflict, result.Status);
        Assert.Equal("synthetic target conflict", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ExistingBackupConflictBlocksWithoutDeletingBackup()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var backupPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "PlaintextMigration", "backups", "active-operation", "cash-journal.plaintext.source.backup.db");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await File.WriteAllTextAsync(backupPath, "synthetic backup conflict");

        var result = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.ExistingBackupConflict, result.Status);
        Assert.Equal("synthetic backup conflict", await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CompletedMigrationIsIdempotent()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);
        var first = await CreateService(databasePath, authorized: true).MigrateAsync();
        Assert.True(first.Succeeded, $"{first.Status} {first.Phase} {first.SafeMessage} {first.SafeAction}");
        var databaseHash = Sha256(databasePath);
        var envelopeHash = Sha256(EnvelopePath(databasePath));

        var second = await CreateService(databasePath, authorized: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted, second.Status);
        Assert.Equal(databaseHash, Sha256(databasePath));
        Assert.Equal(envelopeHash, Sha256(EnvelopePath(databasePath)));
        Assert.Equal(first.OperationId, second.OperationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RollbackRestoresPlaintextFailClosedPostureWhenRequestedBeforeCompletion()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var dryRun = await CreateService(databasePath, authorized: true, dryRun: true).MigrateAsync();

        var rollback = await CreateService(databasePath, rollback: true).MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.RollbackCompleted, rollback.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
        Assert.NotNull(dryRun.OperationId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task AlreadyEncryptedDatabaseIsClassifiedWithoutMigration()
    {
        var databasePath = DatabasePath();
        await CreateCashJournalService(databasePath).InitializeAsync();

        var result = await CreateService(databasePath).ClassifyAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.AlreadyEncrypted, result.Status);
        Assert.False(result.MigrationRequired);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task SafeMessagesDoNotExposeSecretOrDiagnosticInternals()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);

        var result = await CreateService(databasePath, authorized: false).MigrateAsync();

        var text = string.Join('\n', result.SafeMessage, result.SafeAction, result.SupportReference);
        var forbidden = new[]
        {
            "stack trace",
            "connection string",
            "raw row",
            "protected bytes",
            "envelope bytes",
            ("password" + "="),
            "Senior Citizen ID",
            "PWD ID"
        };
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ProductionNoOpFaultInjectorDoesNotInterruptMigration()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);

        var result = await CreateService(
                databasePath,
                authorized: true,
                faultInjector: NoOpLocalDatabasePlaintextMigrationFaultInjector.Instance)
            .MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, result.Status);
        Assert.True(result.Succeeded);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FaultInjectorIsScopedToOneOperation()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var injector = new OneShotPhaseFaultInjector(
            "other-operation",
            LocalDatabasePlaintextMigrationPhase.SourceClassified,
            LocalDatabasePlaintextMigrationFaultTiming.BeforePhase);

        var result = await CreateService(
                databasePath,
                authorized: true,
                operationId: "migration-operation",
                faultInjector: injector)
            .MigrateAsync();

        Assert.False(injector.Fired);
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, result.Status);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InterruptedBackupCanRestartDeterministically()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true);
        var injector = new OneShotPhaseFaultInjector(
            "operation-backup",
            LocalDatabasePlaintextMigrationPhase.BackupStarted,
            LocalDatabasePlaintextMigrationFaultTiming.AfterPhase);

        var interrupted = await CreateService(
                databasePath,
                authorized: true,
                operationId: "operation-backup",
                faultInjector: injector)
            .MigrateAsync();

        Assert.True(injector.Fired);
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.InterruptedMigration, interrupted.Status);

        var resumed = await CreateService(databasePath, authorized: true, operationId: "operation-backup").MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, resumed.Status);
        Assert.True(resumed.Succeeded);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task SourceChangeAfterValidationBlocksRetry()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var injector = new OneShotPhaseFaultInjector(
            "operation-source-change",
            LocalDatabasePlaintextMigrationPhase.SourceValidated,
            LocalDatabasePlaintextMigrationFaultTiming.AfterPhase);
        var interrupted = await CreateService(
                databasePath,
                authorized: true,
                operationId: "operation-source-change",
                faultInjector: injector)
            .MigrateAsync();
        await AddCommittedWalShiftAsync(databasePath);

        var result = await CreateService(databasePath, authorized: true, operationId: "operation-source-change").MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.InterruptedMigration, interrupted.Status);
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.BlockedForSupport, result.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InterruptedCutoverPublishesMatchingEnvelopeOnResume()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: true, includePayableBasis: true);
        var injector = new OneShotPhaseFaultInjector(
            "operation-cutover",
            LocalDatabasePlaintextMigrationPhase.CutoverStarted,
            LocalDatabasePlaintextMigrationFaultTiming.AfterPhase);

        var interrupted = await CreateService(
                databasePath,
                authorized: true,
                operationId: "operation-cutover",
                faultInjector: injector)
            .MigrateAsync();

        Assert.True(injector.Fired);
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.InterruptedMigration, interrupted.Status);
        Assert.False(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));

        var resumed = await CreateService(databasePath, authorized: true, operationId: "operation-cutover").MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationCompleted, resumed.Status);
        Assert.True(File.Exists(EnvelopePath(databasePath)));
        var state = await CreateCashJournalService(databasePath).GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());
        Assert.Equal("shift-001", state.ActiveShift?.Id);
        Assert.Equal(1, state.ActiveCashCustodySessionRecordCount);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InterruptedRollbackCanResumeDeterministically()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextSourceAsync(databasePath, includeCustody: false);
        var dryRun = await CreateService(databasePath, authorized: true, dryRun: true, operationId: "operation-rollback").MigrateAsync();
        Assert.Equal(LocalDatabasePlaintextMigrationStatus.MigrationStarted, dryRun.Status);
        var injector = new OneShotPhaseFaultInjector(
            "operation-rollback",
            LocalDatabasePlaintextMigrationPhase.RollbackStarted,
            LocalDatabasePlaintextMigrationFaultTiming.AfterPhase);

        var exception = await Assert.ThrowsAsync<LocalDatabasePlaintextMigrationException>(() =>
            CreateService(
                    databasePath,
                    rollback: true,
                    operationId: "operation-rollback",
                    faultInjector: injector)
                .MigrateAsync());

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.InterruptedMigration, exception.Status);
        var resumed = await CreateService(databasePath, rollback: true, operationId: "operation-rollback").MigrateAsync();

        Assert.Equal(LocalDatabasePlaintextMigrationStatus.RollbackCompleted, resumed.Status);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_directoryPath))
                {
                    Directory.Delete(_directoryPath, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string DatabasePath() => Path.Combine(_directoryPath, $"{Guid.NewGuid():N}", "cash-journal.db");

    private static string EnvelopePath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath)!, LocalDatabaseKeyEnvelope.EnvelopeFileName);

    private static string SourceQuarantinePath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath)!, "PlaintextMigration", "cash-journal.plaintext.source.quarantine.db");

    private static LocalDatabasePlaintextMigrationService CreateService(
        string databasePath,
        bool authorized = false,
        bool dryRun = false,
        bool rollback = false,
        string? operationId = null,
        ILocalDatabasePlaintextMigrationFaultInjector? faultInjector = null,
        Func<long>? availableDiskBytes = null,
        Func<bool>? isAppRunning = null) =>
        new(new LocalDatabasePlaintextMigrationOptions(
            DatabasePath: databasePath,
            Authorized: authorized,
            DryRun: dryRun,
            Rollback: rollback,
            OperationId: operationId,
            DatabaseKeyProtector: new TestLocalDatabaseKeyProtector(),
            FaultInjector: faultInjector,
            UtcNow: () => DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            AvailableDiskBytes: availableDiskBytes,
            IsAptApplicationRunning: isAppRunning ?? (() => false)));

    private static CashJournalService CreateCashJournalService(string databasePath) =>
        new(new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "https://central-pms.example.invalid",
            DatabaseKeyProtector: new TestLocalDatabaseKeyProtector()));

    private static void WriteValidEnvelope(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var manager = new LocalDatabaseEncryptionManager(databasePath, new TestLocalDatabaseKeyProtector());
        var key = LocalDatabaseKeyGenerator.Generate();
        var protectedKey = new TestLocalDatabaseKeyProtector().Protect(key, LocalDatabaseKeyEnvelope.EntropyBytes);
        try
        {
            var envelope = LocalDatabaseKeyEnvelope.Create(manager.DatabaseIdentity, protectedKey, DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
            File.WriteAllText(EnvelopePath(databasePath), envelope.ToJson(), Encoding.UTF8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static async Task CreatePlaintextSourceAsync(
        string databasePath,
        bool includeCustody,
        bool includePayableBasis = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var dbContext = CreatePlaintextDbContext(connection);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.Parse("2026-07-15T00:00:00Z");
        dbContext.CashierShifts.Add(new CashierShift
        {
            Id = "shift-001",
            CashierId = "cashier-001",
            AuthenticatedCashierSessionReference = "auth-session-001",
            TerminalId = "terminal-001",
            SiteId = "11111111-1111-4111-8111-111111111111",
            SiteGroupId = "22222222-2222-4222-8222-222222222222",
            PosServerId = "pos-server-001",
            OpenedAt = now,
            Status = CashierShiftStatus.Open
        });

        var custodyId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa0001");
        if (includeCustody)
        {
            dbContext.CashCustodySessions.Add(new CashCustodySession
            {
                Id = custodyId,
                CashierId = "cashier-001",
                AuthenticatedCashierSessionReference = "auth-session-001",
                CashierShiftId = "shift-001",
                TerminalId = "terminal-001",
                SiteId = "11111111-1111-4111-8111-111111111111",
                SiteGroupId = "22222222-2222-4222-8222-222222222222",
                PosServerId = "pos-server-001",
                OpeningCashAmount = 1000m,
                OpenedAt = now,
                Status = CashCustodySessionStatus.Open
            });

            var tenderId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb0001");
            dbContext.CashTenders.Add(new CashTender
            {
                Id = tenderId,
                CashCustodySessionId = custodyId,
                ParkingSessionId = "parking-session-001",
                TariffSnapshotId = "tariff-snapshot-001",
                Currency = "PHP",
                AmountDue = 125m,
                AmountTendered = 125m,
                ChangeDue = 0m,
                CorrelationId = "corr-001",
                LocalIdempotencyIdentity = "idem-001",
                CurrentLocalState = CashTenderState.TenderStarted,
                CreatedAt = now,
                UpdatedAt = now
            });
            dbContext.CashTenderEvents.Add(new CashTenderEvent
            {
                Id = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccc0001"),
                CashTenderId = tenderId,
                EventType = CashTenderEventType.TenderStarted,
                OccurredAt = now,
                AmountTendered = 125m,
                ChangeDue = 0m,
                CashierAttested = false,
                ActorCashierId = "cashier-001",
                CorrelationId = "corr-001"
            });
        }

        if (includePayableBasis)
        {
            dbContext.TerminalCashPayableBasisStates.Add(new TerminalCashPayableBasisState
            {
                Id = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddd0001"),
                LocalWorkflowId = "workflow-001",
                LookupReferenceType = "ticket",
                LookupReferenceValue = "TICKET-001",
                ParkingSessionId = "parking-session-001",
                TariffSnapshotId = "tariff-snapshot-001",
                SiteId = "11111111-1111-4111-8111-111111111111",
                SiteGroupId = "22222222-2222-4222-8222-222222222222",
                SitePosServerId = "pos-server-001",
                TerminalId = "terminal-001",
                AuthoritativeAmountMinorUnits = 12500,
                Currency = "PHP",
                TariffValidUntil = now.AddMinutes(10),
                ParkingStatus = "ACTIVE",
                PaymentStatus = "UNPAID",
                ReadyForCashAcceptance = false,
                BlockingReasonCodesJson = "[]",
                Retryable = false,
                SafeUserFacingClassification = "Ready",
                CentralPmsCorrelationId = "corr-basis-001",
                CashierAcknowledgementRequired = false,
                AmountChanged = false,
                ResolvedAt = now,
                UpdatedAt = now
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task ConvertToHistoricalReceiptRetrievalSchemaAsync(string databasePath)
    {
        await ExecutePlaintextAsync(
            databasePath,
            """
            DROP TABLE terminal_cash_receipt_print_jobs;
            DROP TABLE terminal_cash_payable_basis_states;
            DROP TABLE cashier_shifts;

            ALTER TABLE cash_tenders DROP COLUMN StatutoryDiscountDecisionCommandId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryDiscountPayableBasisApplicationCommandId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryDiscountValidationId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryOriginalTariffSnapshotId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryAppliedTariffSnapshotId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryOriginalAmountMinorUnits;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryFinalAmountMinorUnits;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryCurrency;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryAmountAcknowledged;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryAmountAcknowledgedAt;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryImmediateRevalidationOutcome;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryImmediateRevalidatedAt;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryCorrelationId;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryReadinessStatus;
            ALTER TABLE cash_tenders DROP COLUMN StatutoryReadinessAction;

            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN CanonicalPaymentStatus;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN LastCentralPmsCorrelationId;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN SemanticRequestHash;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN SemanticRequestHashVersion;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN SemanticRequestHashStatus;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN LastUpdatedFromCentralPms;
            ALTER TABLE terminal_cash_receipt_retrieval_commands DROP COLUMN LastRetryable;
            ALTER TABLE terminal_cash_receipt_retrieval_attempts DROP COLUMN CentralPmsCorrelationId;
            ALTER TABLE terminal_cash_receipt_retrieval_attempts DROP COLUMN Retryable;
            """);
    }

    private static async Task ExecutePlaintextAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddCommittedWalShiftAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            INSERT INTO cashier_shifts (
                Id, CashierId, AuthenticatedCashierSessionReference, TerminalId, SiteId, SiteGroupId, PosServerId, OpenedAt, ClosedAt, Status
            ) VALUES (
                'shift-wal-001', 'cashier-001', 'auth-session-001', 'terminal-001',
                '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222222',
                'pos-server-001', 638881920000000000, NULL, 'Open'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateUnsupportedPlaintextDatabaseAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unsupported_values (Value TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
    }

    private static CashJournalDbContext CreatePlaintextDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CashJournalDbContext>()
            .UseSqlite(connection, contextOwnsConnection: false)
            .Options;
        return new CashJournalDbContext(options);
    }

    private static bool HasPlainSqliteHeader(string databasePath)
    {
        var expected = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Span<byte> header = stackalloc byte[expected.Length];
        using var stream = File.OpenRead(databasePath);
        var read = stream.Read(header);
        return read == expected.Length && header.SequenceEqual(expected);
    }

    private static bool FileContains(string databasePath, string value) =>
        File.ReadAllBytes(databasePath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    private static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class OneShotPhaseFaultInjector(
        string operationId,
        LocalDatabasePlaintextMigrationPhase phase,
        LocalDatabasePlaintextMigrationFaultTiming timing) : ILocalDatabasePlaintextMigrationFaultInjector
    {
        public bool Fired { get; private set; }

        public ValueTask OnPhaseAsync(
            string currentOperationId,
            LocalDatabasePlaintextMigrationPhase currentPhase,
            LocalDatabasePlaintextMigrationFaultTiming currentTiming,
            CancellationToken cancellationToken)
        {
            if (Fired ||
                !string.Equals(operationId, currentOperationId, StringComparison.Ordinal) ||
                phase != currentPhase ||
                timing != currentTiming)
            {
                return ValueTask.CompletedTask;
            }

            Fired = true;
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.InterruptedMigration,
                phase,
                "Synthetic non-production migration interruption.",
                "Rerun the validation operation.");
        }
    }
}
