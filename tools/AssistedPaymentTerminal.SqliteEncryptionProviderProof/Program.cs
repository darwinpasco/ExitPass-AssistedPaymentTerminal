using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.SqliteEncryptionProviderProof;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var result = await SqliteEncryptionProviderProofRunner.RunAsync(ProofOptions.Parse(args)).ConfigureAwait(false);
            Console.WriteLine("APT SQLite encryption provider proof completed successfully.");
            Console.WriteLine($"Provider: {result.Provider}");
            Console.WriteLine($"Package: {result.SelectedPackage}");
            Console.WriteLine($"Database: {result.DatabasePath}");
            Console.WriteLine($"Correct key opened: {result.CorrectKeyOpened}");
            Console.WriteLine($"No key failed: {result.NoKeyFailed}");
            Console.WriteLine($"Wrong key failed: {result.WrongKeyFailed}");
            Console.WriteLine($"Encrypted header: {result.EncryptedHeaderConfirmed}");
            Console.WriteLine($"Known value hidden: {result.KnownValueHidden}");
            Console.WriteLine($"Rekey succeeded: {result.RekeySucceeded}");
            Console.WriteLine($"Old key rejected: {result.OldKeyRejected}");
            Console.WriteLine($"New key opened: {result.NewKeyOpened}");
            Console.WriteLine($"Plaintext migration feasible: {result.PlaintextMigrationFeasible}");
            Console.WriteLine("Native dependencies:");
            foreach (var dependency in result.NativeDependencies)
            {
                Console.WriteLine($"- {dependency}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"APT SQLite encryption provider proof failed: {exception.Message}");
            return 1;
        }
    }
}

public sealed record ProofOptions(string? DatabasePath = null, bool KeepArtifacts = false)
{
    public static ProofOptions Parse(IReadOnlyList<string> args)
    {
        string? databasePath = null;
        var keepArtifacts = false;
        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            if (string.Equals(value, "--database-path", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                databasePath = args[++index];
                continue;
            }

            if (string.Equals(value, "--keep-artifacts", StringComparison.OrdinalIgnoreCase))
            {
                keepArtifacts = true;
            }
        }

        return new ProofOptions(databasePath, keepArtifacts);
    }
}

public sealed record SqliteEncryptionProofResult(
    string Provider,
    string SelectedPackage,
    string DatabasePath,
    bool CorrectKeyOpened,
    bool NoKeyFailed,
    bool WrongKeyFailed,
    bool EncryptedHeaderConfirmed,
    bool KnownValueHidden,
    bool RekeySucceeded,
    bool OldKeyRejected,
    bool NewKeyOpened,
    bool PlaintextMigrationFeasible,
    IReadOnlyList<string> NativeDependencies);

public static class SqliteEncryptionProviderProofRunner
{
    public const string ProviderName = "SQLCipher through SQLitePCLRaw bundle_e_sqlcipher";
    public const string SelectedPackage = "SQLitePCLRaw.bundle_e_sqlcipher 2.1.11";
    public const string KnownValue = "APT-SQLCIPHER-PROOF-KNOWN-VALUE-20260729";

    public static async Task<SqliteEncryptionProofResult> RunAsync(ProofOptions options, CancellationToken cancellationToken = default)
    {
        SQLitePCL.Batteries_V2.Init();

        var workingDirectory = Path.Combine(Path.GetTempPath(), "ExitPass", "AptSqliteEncryptionProviderProof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var databasePath = Path.GetFullPath(options.DatabasePath ?? Path.Combine(workingDirectory, "apt-encrypted-proof.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        DeleteDatabaseFiles(databasePath);

        var firstKey = DatabaseKey.Generate();
        var secondKey = DatabaseKey.Generate();
        var migratedKey = DatabaseKey.Generate();

        await CreateEncryptedDatabaseAsync(databasePath, firstKey, cancellationToken).ConfigureAwait(false);
        var encryptedHeaderConfirmed = !HasPlainSqliteHeader(databasePath);
        var knownValueHidden = !FileContains(databasePath, KnownValue);
        var correctKeyOpened = await CanReadKnownValueAsync(databasePath, firstKey, cancellationToken).ConfigureAwait(false);
        var noKeyFailed = !await CanReadKnownValueAsync(databasePath, null, cancellationToken).ConfigureAwait(false);
        var wrongKeyFailed = !await CanReadKnownValueAsync(databasePath, secondKey, cancellationToken).ConfigureAwait(false);
        var rekeySucceeded = await RekeyAsync(databasePath, firstKey, secondKey, cancellationToken).ConfigureAwait(false);
        var oldKeyRejected = !await CanReadKnownValueAsync(databasePath, firstKey, cancellationToken).ConfigureAwait(false);
        var newKeyOpened = await CanReadKnownValueAsync(databasePath, secondKey, cancellationToken).ConfigureAwait(false);
        var plaintextMigrationFeasible = await ProvePlaintextMigrationAsync(workingDirectory, migratedKey, cancellationToken).ConfigureAwait(false);
        var nativeDependencies = FindNativeDependencies(AppContext.BaseDirectory);

        if (!correctKeyOpened || !noKeyFailed || !wrongKeyFailed || !encryptedHeaderConfirmed || !knownValueHidden
            || !rekeySucceeded || !oldKeyRejected || !newKeyOpened || !plaintextMigrationFeasible)
        {
            throw new InvalidOperationException("One or more SQLite encryption proof checks failed.");
        }

        if (!options.KeepArtifacts && options.DatabasePath is null)
        {
            TryDeleteDirectory(workingDirectory);
        }

        return new SqliteEncryptionProofResult(
            ProviderName,
            SelectedPackage,
            databasePath,
            correctKeyOpened,
            noKeyFailed,
            wrongKeyFailed,
            encryptedHeaderConfirmed,
            knownValueHidden,
            rekeySucceeded,
            oldKeyRejected,
            newKeyOpened,
            plaintextMigrationFeasible,
            nativeDependencies);
    }

    private static async Task CreateEncryptedDatabaseAsync(string databasePath, DatabaseKey key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, key, create: true, cancellationToken).ConfigureAwait(false);
        var dbContextOptions = new DbContextOptionsBuilder<ProofDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ProofDbContext(dbContextOptions);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ProofRecords.Add(new ProofRecord
        {
            Id = Guid.NewGuid(),
            Reference = "APT-ENCRYPTION-PROOF",
            SafeValue = KnownValue,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CanReadKnownValueAsync(string databasePath, DatabaseKey? key, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(databasePath, key, create: false, cancellationToken).ConfigureAwait(false);
            var dbContextOptions = new DbContextOptionsBuilder<ProofDbContext>().UseSqlite(connection).Options;
            await using var dbContext = new ProofDbContext(dbContextOptions);
            return await dbContext.ProofRecords.AnyAsync(record => record.SafeValue == KnownValue, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<bool> RekeyAsync(string databasePath, DatabaseKey oldKey, DatabaseKey newKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(databasePath, oldKey, create: false, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, $"PRAGMA rekey = {newKey.SqlLiteral};", cancellationToken).ConfigureAwait(false);
            return await CanReadKnownValueAsync(databasePath, newKey, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<bool> ProvePlaintextMigrationAsync(string workingDirectory, DatabaseKey key, CancellationToken cancellationToken)
    {
        var plaintextPath = Path.Combine(workingDirectory, "legacy-plain.db");
        var encryptedPath = Path.Combine(workingDirectory, "legacy-migrated-encrypted.db");
        DeleteDatabaseFiles(plaintextPath);
        DeleteDatabaseFiles(encryptedPath);

        await using (var plaintext = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = plaintextPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await plaintext.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(plaintext, "CREATE TABLE proof_records (Id TEXT NOT NULL PRIMARY KEY, Reference TEXT NOT NULL, SafeValue TEXT NOT NULL, CreatedAt TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(plaintext, $"INSERT INTO proof_records VALUES ('{Guid.NewGuid()}', 'LEGACY', '{KnownValue}', '{DateTimeOffset.UtcNow:O}');", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(plaintext, $"ATTACH DATABASE '{EscapeSqlPath(encryptedPath)}' AS encrypted KEY {key.SqlLiteral};", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(plaintext, "SELECT sqlcipher_export('encrypted');", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(plaintext, "DETACH DATABASE encrypted;", cancellationToken).ConfigureAwait(false);
        }

        return !HasPlainSqliteHeader(encryptedPath)
            && !FileContains(encryptedPath, KnownValue)
            && await CanReadKnownValueAsync(encryptedPath, key, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath, DatabaseKey? key, bool create, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (key is not null)
        {
            await ExecuteNonQueryAsync(connection, $"PRAGMA key = {key.SqlLiteral};", cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasPlainSqliteHeader(string databasePath)
    {
        var expected = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Span<byte> header = stackalloc byte[expected.Length];
        using var stream = File.OpenRead(databasePath);
        var read = stream.Read(header);
        return read == expected.Length && header.SequenceEqual(expected);
    }

    private static bool FileContains(string path, string value)
    {
        var haystack = File.ReadAllBytes(path);
        var needle = Encoding.UTF8.GetBytes(value);
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static IReadOnlyList<string> FindNativeDependencies(string baseDirectory) =>
        Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "e_sqlcipher.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), "SQLitePCLRaw.core.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), "SQLitePCLRaw.batteries_v2.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(baseDirectory, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string EscapeSqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class ProofDbContext(DbContextOptions<ProofDbContext> options) : DbContext(options)
{
    public DbSet<ProofRecord> ProofRecords => Set<ProofRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProofRecord>(entity =>
        {
            entity.ToTable("proof_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Reference).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SafeValue).HasMaxLength(256).IsRequired();
            entity.Property(record => record.CreatedAt).IsRequired();
        });
    }
}

public sealed class ProofRecord
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string SafeValue { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record DatabaseKey(byte[] Bytes)
{
    public string Hex => Convert.ToHexString(Bytes);
    public string SqlLiteral => $"\"x'{Hex}'\"";

    public static DatabaseKey Generate() => new(RandomNumberGenerator.GetBytes(32));
}
