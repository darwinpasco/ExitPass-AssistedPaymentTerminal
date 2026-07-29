using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class LocalDatabaseEncryptionManager
{
    private static readonly byte[] PlainSqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly string SqlCipherPragmaPrefix = "PRAGMA ";
    private static readonly string SqlCipherKeyName = "key = ";
    private readonly ILocalDatabaseKeyProtector _keyProtector;
    private readonly Func<DateTimeOffset> _utcNow;

    public LocalDatabaseEncryptionManager(
        string databasePath,
        ILocalDatabaseKeyProtector? keyProtector = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        EnvelopePath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, LocalDatabaseKeyEnvelope.EnvelopeFileName);
        DatabaseIdentity = CreateDatabaseIdentity(DatabasePath);
        _keyProtector = keyProtector ?? new DpapiCurrentUserLocalDatabaseKeyProtector();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string DatabasePath { get; }

    public string EnvelopePath { get; }

    public string DatabaseIdentity { get; }

    public SqliteConnection OpenEncryptedConnection()
    {
        SQLitePCL.Batteries_V2.Init();
        var key = PrepareAndUnprotectKey();
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());

            connection.Open();
            ApplySqlCipherKey(connection, key);
            ValidateConnection(connection);
            return connection;
        }
        catch (SqliteException exception)
        {
            connection?.Dispose();
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.EncryptedDatabaseUnreadable,
                "Local encrypted storage could not be opened. Local cash operations are blocked until support resolves storage access.",
                "The local encrypted operational database could not be opened.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public LocalPersistenceReadiness GetReadiness()
    {
        try
        {
            using var connection = OpenEncryptedConnection();
            var schemaReady = SchemaLooksReady(connection);
            var integrityValidated = IntegrityCheck(connection);
            return Ready(schemaReady, integrityValidated);
        }
        catch (LocalPersistenceUnavailableException exception)
        {
            return Blocked(exception.SafeStatus, exception.SafeAction);
        }
    }

    public bool HasPlainSqliteHeader() => File.Exists(DatabasePath) && HasPlainSqliteHeader(DatabasePath);

    private byte[] PrepareAndUnprotectKey()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);
        LocalOperationsDirectorySecurity.ApplyBestEffort(directory);

        var databaseExists = File.Exists(DatabasePath);
        var envelopeExists = File.Exists(EnvelopePath);

        if (databaseExists && HasPlainSqliteHeader(DatabasePath))
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired,
                "A legacy plaintext local database requires an approved migration before local cash operations can continue.",
                "A legacy plaintext local database was detected.");
        }

        if (databaseExists && !envelopeExists)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMissing,
                "The protected local storage key is missing. Local cash operations are blocked to preserve existing database evidence.",
                "The encrypted local database exists but the protected key envelope is missing.");
        }

        if (!databaseExists && !envelopeExists)
        {
            return CreateEnvelopeAndReturnPlaintextKey(directory);
        }

        return LoadAndUnprotectEnvelope();
    }

    private byte[] CreateEnvelopeAndReturnPlaintextKey(string directory)
    {
        var plaintextKey = LocalDatabaseKeyGenerator.Generate();
        try
        {
            var protectedKey = _keyProtector.Protect(plaintextKey, LocalDatabaseKeyEnvelope.EntropyBytes);
            try
            {
                var envelope = LocalDatabaseKeyEnvelope.Create(DatabaseIdentity, protectedKey, _utcNow());
                SaveEnvelopeAtomically(directory, envelope);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }

            return plaintextKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintextKey);
            throw;
        }
    }

    private byte[] LoadAndUnprotectEnvelope()
    {
        if (!File.Exists(EnvelopePath))
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMissing,
                "The protected local storage key is missing. Local cash operations are blocked.",
                "The local protected storage key envelope is missing.");
        }

        var envelope = LocalDatabaseKeyEnvelope.Parse(File.ReadAllText(EnvelopePath, Encoding.UTF8));
        envelope.Validate(DatabaseIdentity);
        var protectedKey = envelope.DecodeProtectedKey();
        if (protectedKey.Length == 0)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMalformed,
                "Contact support. The local protected storage key envelope is malformed.",
                "The local protected storage key envelope has empty protected key material.");
        }

        try
        {
            return _keyProtector.Unprotect(protectedKey, LocalDatabaseKeyEnvelope.EntropyBytes);
        }
        catch (CryptographicException exception)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.ProtectedKeyUnavailable,
                "The protected local storage key cannot be opened by this Windows user profile. Local cash operations are blocked.",
                "DPAPI CurrentUser could not unprotect the local database key.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private void SaveEnvelopeAtomically(string directory, LocalDatabaseKeyEnvelope envelope)
    {
        var tempPath = Path.Combine(directory, $"{LocalDatabaseKeyEnvelope.EnvelopeFileName}.{Guid.NewGuid():N}.tmp");
        var payload = Encoding.UTF8.GetBytes(envelope.ToJson());
        using (var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(EnvelopePath))
        {
            File.Delete(tempPath);
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMalformed,
                "The protected local storage key envelope already exists. Startup will retry using the existing envelope.",
                "A protected storage key envelope already exists.");
        }

        File.Move(tempPath, EnvelopePath);
    }

    private static void ApplySqlCipherKey(SqliteConnection connection, byte[] key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(SqlCipherPragmaPrefix, SqlCipherKeyName, ToSqlCipherRawKeyLiteral(key), ";");
        command.ExecuteNonQuery();
    }

    private static string ToSqlCipherRawKeyLiteral(byte[] key) =>
        $"\"x'{Convert.ToHexString(key)}'\"";

    private static void ValidateConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        _ = command.ExecuteScalar();
    }

    private static bool SchemaLooksReady(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'cash_custody_sessions';";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool IntegrityCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
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

    private LocalPersistenceReadiness Ready(bool schemaReady, bool integrityValidated) =>
        new(
            EncryptionConfigured: true,
            DpapiScope: LocalDatabaseKeyEnvelope.CurrentUserScope,
            KeyEnvelopeExists: File.Exists(EnvelopePath),
            KeyAvailable: true,
            DatabaseExists: File.Exists(DatabasePath),
            DatabaseEncrypted: !HasPlainSqliteHeader(),
            LegacyPlaintextDetected: false,
            MigrationRequired: false,
            IntegrityValidated: integrityValidated,
            SchemaReady: schemaReady,
            PersistenceReady: schemaReady && integrityValidated,
            RecoveryAllowed: schemaReady && integrityValidated,
            CashOperationsAllowed: schemaReady && integrityValidated,
            SafeStatus: schemaReady && integrityValidated
                ? LocalPersistenceSafeStatus.Ready
                : LocalPersistenceSafeStatus.InitializingEncryptedStorage,
            SafeAction: schemaReady && integrityValidated
                ? "Local encrypted persistence is ready."
                : "Local encrypted storage is initializing.",
            DatabasePath: DatabasePath,
            KeyEnvelopePath: EnvelopePath);

    private LocalPersistenceReadiness Blocked(LocalPersistenceSafeStatus status, string safeAction) =>
        new(
            EncryptionConfigured: true,
            DpapiScope: LocalDatabaseKeyEnvelope.CurrentUserScope,
            KeyEnvelopeExists: File.Exists(EnvelopePath),
            KeyAvailable: false,
            DatabaseExists: File.Exists(DatabasePath),
            DatabaseEncrypted: File.Exists(DatabasePath) && !HasPlainSqliteHeader(DatabasePath),
            LegacyPlaintextDetected: File.Exists(DatabasePath) && HasPlainSqliteHeader(DatabasePath),
            MigrationRequired: status == LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired,
            IntegrityValidated: false,
            SchemaReady: false,
            PersistenceReady: false,
            RecoveryAllowed: false,
            CashOperationsAllowed: false,
            SafeStatus: status,
            SafeAction: safeAction,
            DatabasePath: DatabasePath,
            KeyEnvelopePath: EnvelopePath);

    private static string CreateDatabaseIdentity(string databasePath) =>
        "ExitPass.APT.LocalOperations:" + Path.GetFullPath(databasePath).ToUpperInvariant();
}

internal static class LocalOperationsDirectorySecurity
{
    public static void ApplyBestEffort(string directory)
    {
        _ = directory;
        // DPAPI CurrentUser is the primary key boundary. The LocalAppData profile
        // directory is already scoped to the dedicated Windows account; installer
        // ACL hardening remains a separate deployment validation item.
    }
}
