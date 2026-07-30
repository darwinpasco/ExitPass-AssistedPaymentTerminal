using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AssistedPaymentTerminal.LocalOperations;

public enum LocalDatabasePlaintextMigrationStatus
{
    MigrationRequired = 1,
    MigrationAlreadyCompleted = 2,
    MigrationStarted = 3,
    MigrationResumed = 4,
    MigrationCompleted = 5,
    MigrationFailed = 6,
    SourceLocked = 7,
    ApplicationRunning = 8,
    WrongWindowsUser = 9,
    UnsupportedSchema = 10,
    SourceCorrupt = 11,
    InsufficientDisk = 12,
    BackupFailed = 13,
    ExportFailed = 14,
    TargetVerificationFailed = 15,
    EnvelopeVerificationFailed = 16,
    ExistingEnvelopeConflict = 17,
    InterruptedMigration = 18,
    RollbackRequired = 19,
    RollbackCompleted = 20,
    BlockedForSupport = 21,
    AlreadyEncrypted = 22,
    NoDatabase = 23,
    ExistingTargetConflict = 24,
    ExistingBackupConflict = 25
    ,
    KeyEnvelopeMissing = 26,
    KeyEnvelopeMalformed = 27,
    KeyEnvelopeWrongIdentity = 28,
    EncryptedDatabaseUnreadable = 29,
    CorruptDatabase = 30
}

public enum LocalDatabasePlaintextMigrationPhase
{
    NotStarted = 1,
    SourceClassified = 2,
    SourceValidated = 3,
    BackupStarted = 4,
    BackupVerified = 5,
    TargetCreated = 6,
    ExportStarted = 7,
    ExportCompleted = 8,
    TargetVerified = 9,
    EnvelopePrepared = 10,
    EnvelopeVerified = 11,
    CutoverStarted = 12,
    DatabaseSwitched = 13,
    EnvelopeSwitched = 14,
    PostCutoverVerificationStarted = 15,
    Completed = 16,
    RollbackRequired = 17,
    RolledBack = 18,
    Blocked = 19,
    RollbackStarted = 20,
    RollbackSourceRestored = 21
}

public enum LocalDatabasePlaintextMigrationFaultTiming
{
    BeforePhase = 1,
    AfterPhase = 2
}

public interface ILocalDatabasePlaintextMigrationFaultInjector
{
    ValueTask OnPhaseAsync(
        string operationId,
        LocalDatabasePlaintextMigrationPhase phase,
        LocalDatabasePlaintextMigrationFaultTiming timing,
        CancellationToken cancellationToken);
}

public sealed class NoOpLocalDatabasePlaintextMigrationFaultInjector : ILocalDatabasePlaintextMigrationFaultInjector
{
    public static NoOpLocalDatabasePlaintextMigrationFaultInjector Instance { get; } = new();

    private NoOpLocalDatabasePlaintextMigrationFaultInjector()
    {
    }

    public ValueTask OnPhaseAsync(
        string operationId,
        LocalDatabasePlaintextMigrationPhase phase,
        LocalDatabasePlaintextMigrationFaultTiming timing,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed record LocalDatabasePlaintextMigrationOptions(
    string? DatabasePath = null,
    bool Authorized = false,
    bool DryRun = false,
    bool Rollback = false,
    string? OperationId = null,
    string? ExpectedWindowsIdentityReference = null,
    ILocalDatabaseKeyProtector? DatabaseKeyProtector = null,
    ILocalDatabasePlaintextMigrationFaultInjector? FaultInjector = null,
    Func<DateTimeOffset>? UtcNow = null,
    Func<long>? AvailableDiskBytes = null,
    Func<bool>? IsAptApplicationRunning = null);

public sealed record LocalDatabasePlaintextMigrationResult(
    LocalDatabasePlaintextMigrationStatus Status,
    LocalDatabasePlaintextMigrationPhase Phase,
    string SafeMessage,
    string SafeAction,
    string SupportReference,
    string DatabasePath,
    string EnvelopePath,
    string? OperationId,
    bool Succeeded,
    bool MigrationRequired,
    bool RollbackRequired,
    IReadOnlyDictionary<string, long> SourceRowCounts,
    IReadOnlyDictionary<string, long> TargetRowCounts,
    string? SourceHash,
    string? BackupHash,
    string? TargetHash,
    string? EnvelopeHash);

public sealed class LocalDatabasePlaintextMigrationService
{
    private static readonly byte[] PlainSqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] RequiredTables =
    [
        "cashier_shifts",
        "cash_custody_sessions",
        "cash_tenders",
        "cash_tender_events",
        "cash_denomination_entries",
        "terminal_cash_payment_outbox_commands",
        "terminal_cash_payment_submission_attempts",
        "terminal_cash_fiscal_outbox_commands",
        "terminal_cash_fiscal_attempts",
        "terminal_cash_receipt_retrieval_commands",
        "terminal_cash_receipt_retrieval_attempts",
        "terminal_cash_receipt_print_jobs",
        "terminal_cash_payable_basis_states"
    ];

    private readonly LocalDatabasePlaintextMigrationOptions _options;
    private readonly ILocalDatabaseKeyProtector _keyProtector;
    private readonly ILocalDatabasePlaintextMigrationFaultInjector _faultInjector;
    private readonly Func<DateTimeOffset> _utcNow;

    public LocalDatabasePlaintextMigrationService(LocalDatabasePlaintextMigrationOptions? options = null)
    {
        _options = options ?? new LocalDatabasePlaintextMigrationOptions();
        _keyProtector = _options.DatabaseKeyProtector ?? new DpapiCurrentUserLocalDatabaseKeyProtector();
        _faultInjector = _options.FaultInjector ?? NoOpLocalDatabasePlaintextMigrationFaultInjector.Instance;
        _utcNow = _options.UtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LocalDatabasePlaintextMigrationResult> ClassifyAsync(CancellationToken cancellationToken = default)
    {
        var paths = ResolvePaths();
        var existingState = LoadState(paths.StatePath);
        if (existingState is not null)
        {
            return existingState.Phase switch
            {
                LocalDatabasePlaintextMigrationPhase.Completed => Result(
                    LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted,
                    existingState.Phase,
                    "Plaintext migration is already completed.",
                    "No migration action is required.",
                    paths,
                    existingState),
                LocalDatabasePlaintextMigrationPhase.RollbackRequired => Result(
                    LocalDatabasePlaintextMigrationStatus.RollbackRequired,
                    existingState.Phase,
                    "Plaintext migration requires rollback or support intervention.",
                    "Run the approved rollback operation or contact support.",
                    paths,
                    existingState),
                LocalDatabasePlaintextMigrationPhase.RolledBack => Result(
                    LocalDatabasePlaintextMigrationStatus.RollbackCompleted,
                    existingState.Phase,
                    "Plaintext migration rollback is completed.",
                    "Normal startup remains blocked until migration is reattempted.",
                    paths,
                    existingState),
                _ => Result(
                    LocalDatabasePlaintextMigrationStatus.InterruptedMigration,
                    existingState.Phase,
                    "Interrupted plaintext migration state was detected.",
                    "Resume the approved offline migration operation.",
                    paths,
                    existingState)
            };
        }

        if (!File.Exists(paths.DatabasePath))
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.NoDatabase,
                LocalDatabasePlaintextMigrationPhase.NotStarted,
                "No local database exists at the resolved path.",
                "Use normal encrypted first-time startup.",
                paths);
        }

        if (Directory.Exists(paths.DatabasePath))
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "The resolved local database path is not a database file.",
                "Contact support before retrying migration.",
                paths);
        }

        if (HasPlainSqliteHeader(paths.DatabasePath))
        {
            if (File.Exists(paths.EnvelopePath))
            {
                return ClassifyPlaintextEnvelopeConflict(paths);
            }

            await using var connection = await OpenPlaintextConnectionAsync(paths.DatabasePath, cancellationToken).ConfigureAwait(false);
            try
            {
                await ValidateSourceAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (LocalDatabasePlaintextMigrationException exception)
            {
                return Result(
                    exception.Status,
                    exception.Phase,
                    exception.SafeMessage,
                    exception.SafeAction,
                    paths);
            }

            var tables = await ReadRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
            return Result(
                LocalDatabasePlaintextMigrationStatus.MigrationRequired,
                LocalDatabasePlaintextMigrationPhase.NotStarted,
                "A legacy plaintext local database requires explicit offline migration.",
                "Run the approved maintenance migration while APT is stopped.",
                paths,
                sourceRowCounts: tables,
                sourceHash: ComputeSha256(paths.DatabasePath));
        }

        var readiness = new LocalDatabaseEncryptionManager(paths.DatabasePath, _keyProtector).GetReadiness();
        if (readiness.PersistenceReady)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.AlreadyEncrypted,
                LocalDatabasePlaintextMigrationPhase.NotStarted,
                "The local database is already encrypted and ready.",
                "No plaintext migration is required.",
                paths,
                envelopeHash: File.Exists(paths.EnvelopePath) ? ComputeSha256(paths.EnvelopePath) : null);
        }

        return Result(
            MapReadinessStatus(readiness.SafeStatus),
            LocalDatabasePlaintextMigrationPhase.Blocked,
            "The local database is not eligible for plaintext migration.",
            readiness.SafeAction,
            paths);
    }

    public async Task<LocalDatabasePlaintextMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var paths = ResolvePaths();
        if (_options.Rollback)
        {
            return await RollbackAsync(paths, cancellationToken).ConfigureAwait(false);
        }

        if (!_options.Authorized)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "Plaintext migration requires explicit authorized maintenance invocation.",
                "Rerun with the approved offline migration authorization switch.",
                paths);
        }

        if ((_options.IsAptApplicationRunning?.Invoke() ?? IsDesktopProcessRunning()))
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.ApplicationRunning,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "APT is running. Plaintext migration must be offline.",
                "Stop the APT application and retry.",
                paths);
        }

        var existing = LoadState(paths.StatePath);
        if (existing is { Phase: LocalDatabasePlaintextMigrationPhase.Completed })
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted,
                existing.Phase,
                "Plaintext migration is already completed.",
                "No migration action is required.",
                paths,
                existing);
        }

        if (existing is { Phase: LocalDatabasePlaintextMigrationPhase.RollbackRequired })
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.RollbackRequired,
                existing.Phase,
                "Plaintext migration requires rollback or support intervention.",
                "Run the approved rollback operation.",
                paths,
                existing);
        }

        var operationId = existing?.OperationId ?? _options.OperationId ?? Guid.NewGuid().ToString("D");
        using var migrationLock = TryAcquireMigrationLock(paths.LockPath);
        if (migrationLock is null)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.SourceLocked,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "Another migration operation owns the local database.",
                "Wait for the operation to finish or contact support.",
                paths,
                operationId: operationId);
        }

        try
        {
            EnsureDirectory(paths.WorkingDirectory);
            if (File.Exists(paths.DatabasePath) && !TryOpenExclusive(paths.DatabasePath))
            {
                return Result(
                    LocalDatabasePlaintextMigrationStatus.SourceLocked,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The plaintext source database could not be locked exclusively.",
                    "Stop all processes using the database and retry.",
                    paths,
                    operationId: operationId);
            }

            var state = existing ?? MigrationState.Create(operationId, paths, CurrentWindowsIdentityReference(), _utcNow());
            if (_options.ExpectedWindowsIdentityReference is not null
                && !string.Equals(_options.ExpectedWindowsIdentityReference, state.WindowsIdentityReference, StringComparison.Ordinal))
            {
                return Result(
                    LocalDatabasePlaintextMigrationStatus.WrongWindowsUser,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "Plaintext migration must run under the dedicated APT Windows account.",
                    "Sign in with the configured APT Windows account and retry.",
                    paths,
                    state);
            }

            if (state.Phase is LocalDatabasePlaintextMigrationPhase.CutoverStarted
                or LocalDatabasePlaintextMigrationPhase.DatabaseSwitched
                or LocalDatabasePlaintextMigrationPhase.EnvelopeSwitched
                or LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted)
            {
                return await RecoverCutoverAsync(paths, state, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(state.SourceHash)
                && File.Exists(paths.DatabasePath)
                && HasPlainSqliteHeader(paths.DatabasePath)
                && !string.Equals(state.SourceHash, ComputeSha256(paths.DatabasePath), StringComparison.Ordinal))
            {
                await SaveStateAsync(paths.StatePath, state with { Phase = LocalDatabasePlaintextMigrationPhase.Blocked }, cancellationToken).ConfigureAwait(false);
                return Result(
                    LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The plaintext source changed after migration validation started.",
                    "Preserve the source and backup, then contact support before retrying.",
                    paths,
                    state);
            }

            state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.SourceClassified, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(paths.DatabasePath) || !HasPlainSqliteHeader(paths.DatabasePath))
            {
                await SaveStateAsync(paths.StatePath, state with { Phase = LocalDatabasePlaintextMigrationPhase.Blocked }, cancellationToken).ConfigureAwait(false);
                return Result(
                    LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The resolved source is not an eligible plaintext database.",
                    "Contact support before retrying migration.",
                    paths,
                    state);
            }

            if (File.Exists(paths.EnvelopePath))
            {
                var conflict = ClassifyPlaintextEnvelopeConflict(paths, state);
                await SaveStateAsync(paths.StatePath, state with { Phase = conflict.Phase }, cancellationToken).ConfigureAwait(false);
                return conflict;
            }

            if (existing is null && File.Exists(paths.TargetPath))
            {
                return Result(
                    LocalDatabasePlaintextMigrationStatus.ExistingTargetConflict,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "A previous encrypted migration target already exists.",
                    "Contact support before retrying migration.",
                    paths,
                    state);
            }

            if (existing is null && File.Exists(paths.BackupPath))
            {
                return Result(
                    LocalDatabasePlaintextMigrationStatus.ExistingBackupConflict,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "A previous plaintext migration backup already exists.",
                    "Contact support before retrying migration.",
                    paths,
                    state);
            }

            string sourceHash;
            string backupHash;
            IReadOnlyDictionary<string, long> sourceRowCounts;
            sourceHash = ComputeSha256(paths.DatabasePath);
            await using (var sourceConnection = await OpenPlaintextConnectionAsync(paths.DatabasePath, cancellationToken).ConfigureAwait(false))
            {
                await BeginPhaseNotificationAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.SourceValidated, cancellationToken).ConfigureAwait(false);
                await ValidateSourceAsync(sourceConnection, cancellationToken).ConfigureAwait(false);
                sourceRowCounts = await ReadRowCountsAsync(sourceConnection, cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    Phase = LocalDatabasePlaintextMigrationPhase.SourceValidated,
                    SourceHash = sourceHash,
                    SourceRowCounts = sourceRowCounts
                };
                state = await PersistPhaseAsync(paths, state, cancellationToken, notifyBefore: false).ConfigureAwait(false);

                RequireDiskSpace(paths.DatabasePath);
                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.BackupStarted, cancellationToken).ConfigureAwait(false);
                await CreateConsistentBackupAsync(sourceConnection, paths.BackupPath, cancellationToken).ConfigureAwait(false);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.BackupStarted, cancellationToken).ConfigureAwait(false);
            }

            backupHash = ComputeSha256(paths.BackupPath);
            await BeginPhaseNotificationAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.BackupVerified, cancellationToken).ConfigureAwait(false);
            await using (var backupConnection = await OpenPlaintextConnectionAsync(paths.BackupPath, cancellationToken).ConfigureAwait(false))
            {
                await ValidateSourceAsync(backupConnection, cancellationToken).ConfigureAwait(false);
                var backupCounts = await ReadRowCountsAsync(backupConnection, cancellationToken).ConfigureAwait(false);
                EnsureRowCountsMatch(sourceRowCounts, backupCounts);
            }

            state = state with
            {
                Phase = LocalDatabasePlaintextMigrationPhase.BackupVerified,
                BackupHash = backupHash
            };
            state = await PersistPhaseAsync(paths, state, cancellationToken, notifyBefore: false).ConfigureAwait(false);

            DeleteIfExists(paths.TargetPath);
            var key = LocalDatabaseKeyGenerator.Generate();
            try
            {
                state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.TargetCreated, cancellationToken).ConfigureAwait(false);
                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.ExportStarted, cancellationToken).ConfigureAwait(false);
                await ExportBackupToEncryptedTargetAsync(paths.BackupPath, paths.TargetPath, key, cancellationToken).ConfigureAwait(false);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.ExportStarted, cancellationToken).ConfigureAwait(false);
                state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.ExportCompleted, cancellationToken).ConfigureAwait(false);

                await BeginPhaseNotificationAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.TargetVerified, cancellationToken).ConfigureAwait(false);
                var targetRowCounts = await VerifyEncryptedTargetAsync(paths.TargetPath, key, sourceRowCounts, cancellationToken).ConfigureAwait(false);
                var targetHash = ComputeSha256(paths.TargetPath);
                state = state with
                {
                    Phase = LocalDatabasePlaintextMigrationPhase.TargetVerified,
                    TargetHash = targetHash,
                    TargetRowCounts = targetRowCounts
                };
                state = await PersistPhaseAsync(paths, state, cancellationToken, notifyBefore: false).ConfigureAwait(false);

                await BeginPhaseNotificationAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.EnvelopePrepared, cancellationToken).ConfigureAwait(false);
                var manager = new LocalDatabaseEncryptionManager(paths.DatabasePath, _keyProtector);
                var protectedKey = _keyProtector.Protect(key, LocalDatabaseKeyEnvelope.EntropyBytes);
                try
                {
                    var envelope = LocalDatabaseKeyEnvelope.Create(manager.DatabaseIdentity, protectedKey, _utcNow());
                    await SaveEnvelopeAsync(paths.PreparedEnvelopePath, envelope, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedKey);
                }

                state = state with { Phase = LocalDatabasePlaintextMigrationPhase.EnvelopePrepared };
                state = await PersistPhaseAsync(paths, state, cancellationToken, notifyBefore: false).ConfigureAwait(false);
                await BeginPhaseNotificationAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.EnvelopeVerified, cancellationToken).ConfigureAwait(false);
                await VerifyPreparedEnvelopeAsync(paths.PreparedEnvelopePath, manager.DatabaseIdentity, paths.TargetPath, sourceRowCounts, cancellationToken).ConfigureAwait(false);
                var envelopeHash = ComputeSha256(paths.PreparedEnvelopePath);
                state = state with
                {
                    Phase = LocalDatabasePlaintextMigrationPhase.EnvelopeVerified,
                    EnvelopeHash = envelopeHash
                };
                state = await PersistPhaseAsync(paths, state, cancellationToken, notifyBefore: false).ConfigureAwait(false);

                if (_options.DryRun)
                {
                    return Result(
                        LocalDatabasePlaintextMigrationStatus.MigrationStarted,
                        state.Phase,
                        "Plaintext migration dry classification and verification completed before cutover.",
                        "Rerun without dry mode to perform cutover.",
                        paths,
                        state);
                }

                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.CutoverStarted, cancellationToken).ConfigureAwait(false);
                MoveRequired(paths.DatabasePath, paths.SourceQuarantinePath);
                MoveRequired(paths.TargetPath, paths.DatabasePath);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.CutoverStarted, cancellationToken).ConfigureAwait(false);
                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.DatabaseSwitched, cancellationToken).ConfigureAwait(false);
                MoveRequired(paths.PreparedEnvelopePath, paths.EnvelopePath);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.DatabaseSwitched, cancellationToken).ConfigureAwait(false);
                state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.EnvelopeSwitched, cancellationToken).ConfigureAwait(false);

                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted, cancellationToken).ConfigureAwait(false);
                var service = new CashJournalService(new LocalOperationsDatabaseOptions(
                    paths.DatabasePath,
                    CentralPmsBaseUrl: "UNCONFIGURED_CENTRAL_PMS",
                    DatabaseKeyProtector: _keyProtector));
                await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var readiness = service.GetLocalPersistenceReadiness();
                if (!readiness.PersistenceReady)
                {
                    throw new LocalDatabasePlaintextMigrationException(
                        LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                        LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                        "Post-cutover encrypted storage verification failed.",
                        "Rollback is required before normal startup can continue.");
                }

                var finalCounts = await ReadEncryptedRowCountsAsync(paths.DatabasePath, _keyProtector, cancellationToken).ConfigureAwait(false);
                EnsureRowCountsMatch(sourceRowCounts, finalCounts);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted, cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    Phase = LocalDatabasePlaintextMigrationPhase.Completed,
                    CompletedAt = _utcNow(),
                    TargetHash = ComputeSha256(paths.DatabasePath),
                    EnvelopeHash = ComputeSha256(paths.EnvelopePath),
                    TargetRowCounts = finalCounts
                };
                state = await PersistPhaseAsync(paths, state, cancellationToken).ConfigureAwait(false);
                return Result(
                    LocalDatabasePlaintextMigrationStatus.MigrationCompleted,
                    state.Phase,
                    "Plaintext migration completed and encrypted startup verification passed.",
                    "The APT may use normal encrypted startup.",
                    paths,
                    state,
                    succeeded: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (LocalDatabasePlaintextMigrationException exception)
        {
            var state = LoadState(paths.StatePath);
            if (exception.Phase == LocalDatabasePlaintextMigrationPhase.RollbackRequired && state is not null)
            {
                await SaveStateAsync(paths.StatePath, state with { Phase = LocalDatabasePlaintextMigrationPhase.RollbackRequired }, cancellationToken).ConfigureAwait(false);
            }

            return Result(
                exception.Status,
                exception.Phase,
                exception.SafeMessage,
                exception.SafeAction,
                paths,
                state);
        }
        catch (Exception)
        {
            var state = LoadState(paths.StatePath);
            var failurePhase = state?.Phase ?? LocalDatabasePlaintextMigrationPhase.RollbackRequired;
            if (state is not null)
            {
                await SaveStateAsync(paths.StatePath, state with { Phase = LocalDatabasePlaintextMigrationPhase.RollbackRequired }, cancellationToken).ConfigureAwait(false);
            }

            return Result(
                LocalDatabasePlaintextMigrationStatus.MigrationFailed,
                failurePhase,
                "Plaintext migration failed before completion.",
                "Rollback or support intervention is required.",
                paths,
                state);
        }
    }

    private async Task<LocalDatabasePlaintextMigrationResult> RollbackAsync(MigrationPaths paths, CancellationToken cancellationToken)
    {
        var state = LoadState(paths.StatePath);
        if (state is null)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "No migration state is available for rollback.",
                "Contact support before retrying rollback.",
                paths);
        }

        if (state.Phase == LocalDatabasePlaintextMigrationPhase.Completed)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "Completed plaintext migration cannot be silently rolled back.",
                "Use a separately approved support recovery procedure.",
                paths,
                state);
        }

        if (!File.Exists(paths.SourceQuarantinePath) && !File.Exists(paths.BackupPath))
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.BlockedForSupport,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "No verified plaintext source or backup is available for rollback.",
                "Contact support before changing local storage files.",
                paths,
                state);
        }

        state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.RollbackStarted, cancellationToken).ConfigureAwait(false);
        if (File.Exists(paths.DatabasePath) && !HasPlainSqliteHeader(paths.DatabasePath))
        {
            MoveRequired(paths.DatabasePath, paths.RollbackEncryptedQuarantinePath);
        }

        if (File.Exists(paths.SourceQuarantinePath))
        {
            MoveRequired(paths.SourceQuarantinePath, paths.DatabasePath);
        }
        else if (!File.Exists(paths.DatabasePath))
        {
            File.Copy(paths.BackupPath, paths.DatabasePath);
        }
        await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.RollbackStarted, cancellationToken).ConfigureAwait(false);
        state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.RollbackSourceRestored, cancellationToken).ConfigureAwait(false);

        DeleteIfExists(paths.EnvelopePath);
        DeleteIfExists(paths.PreparedEnvelopePath);
        DeleteIfExists(paths.TargetPath);
        state = state with { Phase = LocalDatabasePlaintextMigrationPhase.RolledBack };
        state = await PersistPhaseAsync(paths, state, cancellationToken).ConfigureAwait(false);
        return Result(
            LocalDatabasePlaintextMigrationStatus.RollbackCompleted,
            state.Phase,
            "Plaintext migration rollback completed.",
            "Normal startup remains blocked until explicit migration is reattempted.",
            paths,
            state);
    }

    private async Task<LocalDatabasePlaintextMigrationResult> RecoverCutoverAsync(
        MigrationPaths paths,
        MigrationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(paths.DatabasePath) && HasPlainSqliteHeader(paths.DatabasePath))
            {
                if (!File.Exists(paths.TargetPath) || !File.Exists(paths.PreparedEnvelopePath))
                {
                    throw new LocalDatabasePlaintextMigrationException(
                        LocalDatabasePlaintextMigrationStatus.RollbackRequired,
                        LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                        "Interrupted cutover is missing verified encrypted artifacts.",
                        "Rollback or support intervention is required.");
                }

                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.CutoverStarted, cancellationToken).ConfigureAwait(false);
                MoveRequired(paths.DatabasePath, paths.SourceQuarantinePath);
                MoveRequired(paths.TargetPath, paths.DatabasePath);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.CutoverStarted, cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(paths.DatabasePath) || HasPlainSqliteHeader(paths.DatabasePath))
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.RollbackRequired,
                    LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                    "Interrupted cutover did not leave a verified encrypted database at the active path.",
                    "Rollback or support intervention is required.");
            }

            if (!File.Exists(paths.EnvelopePath))
            {
                if (!File.Exists(paths.PreparedEnvelopePath))
                {
                    throw new LocalDatabasePlaintextMigrationException(
                        LocalDatabasePlaintextMigrationStatus.EnvelopeVerificationFailed,
                        LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                        "Interrupted cutover is missing the prepared protected key envelope.",
                        "Rollback or support intervention is required.");
                }

                state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.DatabaseSwitched, cancellationToken).ConfigureAwait(false);
                MoveRequired(paths.PreparedEnvelopePath, paths.EnvelopePath);
                await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.DatabaseSwitched, cancellationToken).ConfigureAwait(false);
            }

            state = await TransitionPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.EnvelopeSwitched, cancellationToken).ConfigureAwait(false);
            state = await BeginPhaseAsync(paths, state, LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted, cancellationToken).ConfigureAwait(false);

            var service = new CashJournalService(new LocalOperationsDatabaseOptions(
                paths.DatabasePath,
                CentralPmsBaseUrl: "UNCONFIGURED_CENTRAL_PMS",
                DatabaseKeyProtector: _keyProtector));
            await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var readiness = service.GetLocalPersistenceReadiness();
            if (!readiness.PersistenceReady)
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                    LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                    "Post-cutover encrypted storage verification failed.",
                    "Rollback or support intervention is required.");
            }

            var finalCounts = await ReadEncryptedRowCountsAsync(paths.DatabasePath, _keyProtector, cancellationToken).ConfigureAwait(false);
            EnsureRowCountsMatch(state.SourceRowCounts, finalCounts);
            await CompletePhaseAsync(state.OperationId, LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted, cancellationToken).ConfigureAwait(false);

            state = state with
            {
                Phase = LocalDatabasePlaintextMigrationPhase.Completed,
                CompletedAt = _utcNow(),
                TargetHash = ComputeSha256(paths.DatabasePath),
                EnvelopeHash = ComputeSha256(paths.EnvelopePath),
                TargetRowCounts = finalCounts
            };
            state = await PersistPhaseAsync(paths, state, cancellationToken).ConfigureAwait(false);
            return Result(
                LocalDatabasePlaintextMigrationStatus.MigrationCompleted,
                state.Phase,
                "Interrupted plaintext migration cutover resumed and encrypted startup verification passed.",
                "The APT may use normal encrypted startup.",
                paths,
                state,
                succeeded: true);
        }
        catch (LocalDatabasePlaintextMigrationException exception)
        {
            await SaveStateAsync(paths.StatePath, state with { Phase = LocalDatabasePlaintextMigrationPhase.RollbackRequired }, cancellationToken).ConfigureAwait(false);
            return Result(
                exception.Status,
                exception.Phase,
                exception.SafeMessage,
                exception.SafeAction,
                paths,
                state);
        }
    }

    private static async Task ValidateSourceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var integrity = Convert.ToString(await ExecuteScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.SourceCorrupt,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The plaintext source database failed integrity validation.",
                    "Preserve the database and contact support.");
            }

            var foreignKeyRows = await ReadForeignKeyFailuresAsync(connection, cancellationToken).ConfigureAwait(false);
            if (foreignKeyRows > 0)
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.SourceCorrupt,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The plaintext source database failed relationship validation.",
                    "Preserve the database and contact support.");
            }

            var tables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
            var missing = RequiredTables.Where(table => !tables.Contains(table)).ToArray();
            if (missing.Length > 0)
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.UnsupportedSchema,
                    LocalDatabasePlaintextMigrationPhase.Blocked,
                    "The plaintext source schema is not supported by this migration runtime.",
                    "Update the migration utility or contact support.");
            }
        }
        catch (SqliteException exception)
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.SourceCorrupt,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "The plaintext source database failed integrity validation.",
                "Preserve the database and contact support.",
                exception);
        }
    }

    private LocalDatabasePlaintextMigrationResult ClassifyPlaintextEnvelopeConflict(MigrationPaths paths, MigrationState? state = null)
    {
        try
        {
            var manager = new LocalDatabaseEncryptionManager(paths.DatabasePath, _keyProtector);
            var envelope = LocalDatabaseKeyEnvelope.Parse(File.ReadAllText(paths.EnvelopePath, Encoding.UTF8));
            envelope.Validate(manager.DatabaseIdentity);
            var protectedKey = envelope.DecodeProtectedKey();
            try
            {
                var key = _keyProtector.Unprotect(protectedKey, LocalDatabaseKeyEnvelope.EntropyBytes);
                CryptographicOperations.ZeroMemory(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }

            return Result(
                LocalDatabasePlaintextMigrationStatus.ExistingEnvelopeConflict,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "A plaintext database exists beside a protected key envelope.",
                "Contact support to resolve the envelope conflict before migration.",
                paths,
                state);
        }
        catch (LocalPersistenceUnavailableException exception)
        {
            return Result(
                MapReadinessStatus(exception.SafeStatus),
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "A plaintext database exists beside an unusable protected key envelope.",
                exception.SafeAction,
                paths,
                state);
        }
        catch (CryptographicException)
        {
            return Result(
                LocalDatabasePlaintextMigrationStatus.KeyEnvelopeWrongIdentity,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "A plaintext database exists beside a protected key envelope for another Windows context.",
                "Contact support to resolve the envelope conflict before migration.",
                paths,
                state);
        }
    }

    private static LocalDatabasePlaintextMigrationStatus MapReadinessStatus(LocalPersistenceSafeStatus status) =>
        status switch
        {
            LocalPersistenceSafeStatus.KeyEnvelopeMissing => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeMissing,
            LocalPersistenceSafeStatus.KeyEnvelopeMalformed => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeMalformed,
            LocalPersistenceSafeStatus.KeyEnvelopeUnsupportedVersion => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeMalformed,
            LocalPersistenceSafeStatus.KeyEnvelopeWrongIdentity => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeWrongIdentity,
            LocalPersistenceSafeStatus.KeyEnvelopeWrongScope => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeWrongIdentity,
            LocalPersistenceSafeStatus.ProtectedKeyUnavailable => LocalDatabasePlaintextMigrationStatus.KeyEnvelopeWrongIdentity,
            LocalPersistenceSafeStatus.EncryptedDatabaseUnreadable => LocalDatabasePlaintextMigrationStatus.EncryptedDatabaseUnreadable,
            LocalPersistenceSafeStatus.CorruptDatabase => LocalDatabasePlaintextMigrationStatus.CorruptDatabase,
            _ => LocalDatabasePlaintextMigrationStatus.BlockedForSupport
        };

    private static async Task CreateConsistentBackupAsync(SqliteConnection sourceConnection, string backupPath, CancellationToken cancellationToken)
    {
        EnsureDirectory(Path.GetDirectoryName(backupPath)!);
        DeleteIfExists(backupPath);
        await ExecuteNonQueryAsync(sourceConnection, $"VACUUM INTO '{EscapeSqlPath(backupPath)}';", cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExportBackupToEncryptedTargetAsync(string backupPath, string targetPath, byte[] key, CancellationToken cancellationToken)
    {
        try
        {
            DeleteIfExists(targetPath);
            await using var connection = await OpenPlaintextConnectionAsync(backupPath, cancellationToken, create: true).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, $"ATTACH DATABASE '{EscapeSqlPath(targetPath)}' AS encrypted KEY {ToSqlCipherRawKeyLiteral(key)};", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "SELECT sqlcipher_export('encrypted');", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DETACH DATABASE encrypted;", cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.ExportFailed,
                LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                "Plaintext database export to encrypted target failed.",
                "Preserve the source and backup, then contact support.",
                exception);
        }
    }

    private async Task VerifyPreparedEnvelopeAsync(
        string envelopePath,
        string databaseIdentity,
        string targetPath,
        IReadOnlyDictionary<string, long> expectedRowCounts,
        CancellationToken cancellationToken)
    {
        var envelope = LocalDatabaseKeyEnvelope.Parse(await File.ReadAllTextAsync(envelopePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
        envelope.Validate(databaseIdentity);
        var protectedKey = envelope.DecodeProtectedKey();
        byte[]? key = null;
        try
        {
            key = _keyProtector.Unprotect(protectedKey, LocalDatabaseKeyEnvelope.EntropyBytes);
            await VerifyEncryptedTargetAsync(targetPath, key, expectedRowCounts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CryptographicException or SqliteException or LocalDatabasePlaintextMigrationException)
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.EnvelopeVerificationFailed,
                LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                "The protected key envelope could not reopen the encrypted target.",
                "Rollback or support intervention is required.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, long>> VerifyEncryptedTargetAsync(
        string targetPath,
        byte[] key,
        IReadOnlyDictionary<string, long> expectedRowCounts,
        CancellationToken cancellationToken)
    {
        if (HasPlainSqliteHeader(targetPath))
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                "The encrypted target did not pass header validation.",
                "Rollback or support intervention is required.");
        }

        await using var connection = await OpenEncryptedConnectionAsync(targetPath, key, cancellationToken).ConfigureAwait(false);
        var integrity = Convert.ToString(await ExecuteScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                "The encrypted target failed integrity validation.",
                "Rollback or support intervention is required.");
        }

        var rowCounts = await ReadRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureRowCountsMatch(expectedRowCounts, rowCounts);
        return rowCounts;
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadEncryptedRowCountsAsync(
        string databasePath,
        ILocalDatabaseKeyProtector keyProtector,
        CancellationToken cancellationToken)
    {
        var manager = new LocalDatabaseEncryptionManager(databasePath, keyProtector);
        await using var connection = manager.OpenEncryptedConnection();
        return await ReadRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in RequiredTables)
        {
            result[table] = tables.Contains(table)
                ? Convert.ToInt64(await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM {table};", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture)
                : -1;
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<long> ReadForeignKeyFailuresAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        var count = 0L;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private void RequireDiskSpace(string sourcePath)
    {
        if (_options.AvailableDiskBytes is null)
        {
            return;
        }

        var required = new FileInfo(sourcePath).Length * 4;
        if (_options.AvailableDiskBytes() < required)
        {
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.InsufficientDisk,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "There is not enough local disk space to create migration backup and encrypted target files.",
                "Free disk space and retry the offline migration.");
        }
    }

    private static IDisposable? TryAcquireMigrationLock(string lockPath)
    {
        try
        {
            EnsureDirectory(Path.GetDirectoryName(lockPath)!);
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<SqliteConnection> OpenPlaintextConnectionAsync(string path, CancellationToken cancellationToken, bool create = false)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new LocalDatabasePlaintextMigrationException(
                LocalDatabasePlaintextMigrationStatus.SourceCorrupt,
                LocalDatabasePlaintextMigrationPhase.Blocked,
                "The plaintext source database could not be opened.",
                "Preserve the database and contact support.",
                exception);
        }
    }

    private static async Task<SqliteConnection> OpenEncryptedConnectionAsync(string path, byte[] key, CancellationToken cancellationToken)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, string.Concat("PRAGMA ", "key = ", ToSqlCipherRawKeyLiteral(key), ";"), cancellationToken).ConfigureAwait(false);
        _ = await ExecuteScalarAsync(connection, "PRAGMA schema_version;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveEnvelopeAsync(string path, LocalDatabaseKeyEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(envelope.ToJson());
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MigrationState> TransitionPhaseAsync(
        MigrationPaths paths,
        MigrationState state,
        LocalDatabasePlaintextMigrationPhase phase,
        CancellationToken cancellationToken)
    {
        state = await BeginPhaseAsync(paths, state, phase, cancellationToken).ConfigureAwait(false);
        await CompletePhaseAsync(state.OperationId, phase, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<MigrationState> BeginPhaseAsync(
        MigrationPaths paths,
        MigrationState state,
        LocalDatabasePlaintextMigrationPhase phase,
        CancellationToken cancellationToken)
    {
        await BeginPhaseNotificationAsync(state.OperationId, phase, cancellationToken).ConfigureAwait(false);
        state = state with { Phase = phase };
        await SaveStateAsync(paths.StatePath, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<MigrationState> PersistPhaseAsync(
        MigrationPaths paths,
        MigrationState state,
        CancellationToken cancellationToken,
        bool notifyBefore = true)
    {
        if (notifyBefore)
        {
            await BeginPhaseNotificationAsync(state.OperationId, state.Phase, cancellationToken).ConfigureAwait(false);
        }

        await SaveStateAsync(paths.StatePath, state, cancellationToken).ConfigureAwait(false);
        await CompletePhaseAsync(state.OperationId, state.Phase, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private ValueTask BeginPhaseNotificationAsync(
        string operationId,
        LocalDatabasePlaintextMigrationPhase phase,
        CancellationToken cancellationToken) =>
        _faultInjector.OnPhaseAsync(operationId, phase, LocalDatabasePlaintextMigrationFaultTiming.BeforePhase, cancellationToken);

    private ValueTask CompletePhaseAsync(
        string operationId,
        LocalDatabasePlaintextMigrationPhase phase,
        CancellationToken cancellationToken) =>
        _faultInjector.OnPhaseAsync(operationId, phase, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase, cancellationToken);

    private static async Task SaveStateAsync(string path, MigrationState state, CancellationToken cancellationToken)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions));
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static MigrationState? LoadState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MigrationState>(File.ReadAllText(statePath, Encoding.UTF8), JsonOptions);
        }
        catch (JsonException)
        {
            return new MigrationState(
                Guid.NewGuid().ToString("D"),
                LocalDatabasePlaintextMigrationPhase.Blocked,
                CurrentWindowsIdentityReference(),
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool HasPlainSqliteHeader(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[PlainSqliteHeader.Length];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(header);
        return read == PlainSqliteHeader.Length && header.SequenceEqual(PlainSqliteHeader);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureRowCountsMatch(IReadOnlyDictionary<string, long> expected, IReadOnlyDictionary<string, long> actual)
    {
        foreach (var (table, count) in expected)
        {
            if (!actual.TryGetValue(table, out var actualCount) || actualCount != count)
            {
                throw new LocalDatabasePlaintextMigrationException(
                    LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                    LocalDatabasePlaintextMigrationPhase.RollbackRequired,
                    "The encrypted target did not preserve the source row-count summary.",
                    "Rollback or support intervention is required.");
            }
        }
    }

    private static void MoveRequired(string source, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        DeleteIfExists(destination);
        File.Move(source, destination);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

    private static string EscapeSqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static string ToSqlCipherRawKeyLiteral(byte[] key) => $"\"x'{Convert.ToHexString(key)}'\"";

    private static bool IsDesktopProcessRunning()
    {
        try
        {
            var currentId = Environment.ProcessId;
            return Process.GetProcessesByName("AssistedPaymentTerminal.Desktop")
                .Any(process =>
                {
                    using (process)
                    {
                        return process.Id != currentId;
                    }
                });
        }
        catch
        {
            return true;
        }
    }

    private static string CurrentWindowsIdentityReference()
    {
        var name = Environment.UserName;
        var source = string.Concat(Environment.UserDomainName, "\\", name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private MigrationPaths ResolvePaths()
    {
        var databasePath = LocalOperationsDatabasePath.Resolve(_options.DatabasePath);
        return ResolvePaths(databasePath);
    }

    private static MigrationPaths ResolvePaths(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        var workingDirectory = Path.Combine(directory, "PlaintextMigration");
        var statePath = Path.Combine(workingDirectory, "cash-journal-migration-state.json");
        var backupDirectory = Path.Combine(workingDirectory, "backups", "active-operation");
        return new MigrationPaths(
            databasePath,
            Path.Combine(directory, LocalDatabaseKeyEnvelope.EnvelopeFileName),
            workingDirectory,
            statePath,
            Path.Combine(workingDirectory, "cash-journal-migration.lock"),
            Path.Combine(workingDirectory, "cash-journal.encrypted-target.db"),
            Path.Combine(workingDirectory, "cash-journal.prepared-envelope.json"),
            Path.Combine(backupDirectory, "cash-journal.plaintext.source.backup.db"),
            Path.Combine(workingDirectory, "cash-journal.plaintext.source.quarantine.db"),
            Path.Combine(workingDirectory, "cash-journal.encrypted.rollback-quarantine.db"));
    }

    private static LocalDatabasePlaintextMigrationResult Result(
        LocalDatabasePlaintextMigrationStatus status,
        LocalDatabasePlaintextMigrationPhase phase,
        string safeMessage,
        string safeAction,
        MigrationPaths paths,
        MigrationState? state = null,
        bool succeeded = false,
        IReadOnlyDictionary<string, long>? sourceRowCounts = null,
        IReadOnlyDictionary<string, long>? targetRowCounts = null,
        string? operationId = null,
        string? sourceHash = null,
        string? backupHash = null,
        string? targetHash = null,
        string? envelopeHash = null) =>
        new(
            status,
            phase,
            safeMessage,
            safeAction,
            SupportReferenceFor(state?.OperationId ?? operationId),
            paths.DatabasePath,
            paths.EnvelopePath,
            state?.OperationId ?? operationId,
            succeeded,
            status == LocalDatabasePlaintextMigrationStatus.MigrationRequired,
            status == LocalDatabasePlaintextMigrationStatus.RollbackRequired,
            sourceRowCounts ?? state?.SourceRowCounts ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            targetRowCounts ?? state?.TargetRowCounts ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            sourceHash ?? state?.SourceHash,
            backupHash ?? state?.BackupHash,
            targetHash ?? state?.TargetHash,
            envelopeHash ?? state?.EnvelopeHash);

    private static string SupportReferenceFor(string? operationId) =>
        string.IsNullOrWhiteSpace(operationId)
            ? "APT-MIGRATION-NOT-STARTED"
            : $"APT-MIGRATION-{operationId[..Math.Min(8, operationId.Length)]}";

    private sealed record MigrationPaths(
        string DatabasePath,
        string EnvelopePath,
        string WorkingDirectory,
        string StatePath,
        string LockPath,
        string TargetPath,
        string PreparedEnvelopePath,
        string BackupPath,
        string SourceQuarantinePath,
        string RollbackEncryptedQuarantinePath);

    private sealed record MigrationState(
        string OperationId,
        LocalDatabasePlaintextMigrationPhase Phase,
        string WindowsIdentityReference,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        string? SourceHash,
        string? BackupHash,
        string? TargetHash,
        string? EnvelopeHash,
        IReadOnlyDictionary<string, long> SourceRowCounts,
        IReadOnlyDictionary<string, long> TargetRowCounts)
    {
        public static MigrationState Create(string operationId, MigrationPaths paths, string identityReference, DateTimeOffset startedAt) =>
            new(
                operationId,
                LocalDatabasePlaintextMigrationPhase.NotStarted,
                identityReference,
                startedAt,
                null,
                File.Exists(paths.DatabasePath) ? ComputeSha256(paths.DatabasePath) : null,
                null,
                null,
                null,
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class LocalDatabasePlaintextMigrationException : Exception
{
    public LocalDatabasePlaintextMigrationException(
        LocalDatabasePlaintextMigrationStatus status,
        LocalDatabasePlaintextMigrationPhase phase,
        string safeMessage,
        string safeAction,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Status = status;
        Phase = phase;
        SafeMessage = safeMessage;
        SafeAction = safeAction;
    }

    public LocalDatabasePlaintextMigrationStatus Status { get; }

    public LocalDatabasePlaintextMigrationPhase Phase { get; }

    public string SafeMessage { get; }

    public string SafeAction { get; }
}
