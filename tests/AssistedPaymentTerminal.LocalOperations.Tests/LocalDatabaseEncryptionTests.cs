using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class LocalDatabaseEncryptionTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        "ExitPass.APT.LocalOperations.Encryption.Tests",
        Guid.NewGuid().ToString("N"));

    public LocalDatabaseEncryptionTests()
    {
        Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void RandomKeyGenerationProducesDifferentKeys()
    {
        var first = LocalDatabaseKeyGenerator.Generate();
        var second = LocalDatabaseKeyGenerator.Generate();

        Assert.Equal(LocalDatabaseKeyGenerator.KeyLengthBytes, first.Length);
        Assert.Equal(LocalDatabaseKeyGenerator.KeyLengthBytes, second.Length);
        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(second));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task NewDatabaseCreatesCurrentUserEnvelopeAndEncryptedSchemaFromFirstCreation()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);

        await service.InitializeAsync();
        await service.CreateCashCustodySessionAsync(TestRequests.CreateSession());

        var envelopePath = EnvelopePath(databasePath);
        var envelope = ReadEnvelope(envelopePath);

        Assert.True(File.Exists(databasePath));
        Assert.True(File.Exists(envelopePath));
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal(LocalDatabaseKeyEnvelope.CurrentUserScope, envelope.ProtectionScope);
        Assert.Equal(LocalDatabaseKeyEnvelope.CurrentKeyAlgorithm, envelope.KeyAlgorithm);
        Assert.False(HasPlainSqliteHeader(databasePath));
        Assert.False(FileContains(databasePath, "cashier-001"));
        Assert.False(File.ReadAllText(envelopePath).Contains(Convert.ToBase64String(LocalDatabaseKeyGenerator.Generate()), StringComparison.Ordinal));
        Assert.True(await CanOpenWithEnvelopeAsync(databasePath));
        Assert.False(await CanOpenWithoutKeyAsync(databasePath));
        Assert.False(await CanOpenWithWrongKeyAsync(databasePath));

        var readiness = service.GetLocalPersistenceReadiness();
        Assert.True(readiness.PersistenceReady);
        Assert.True(readiness.CashOperationsAllowed);
        Assert.True(readiness.DatabaseEncrypted);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FreshEncryptedDatabaseHasNoActiveShiftOrCashCustodySession()
    {
        var service = CreateService(DatabasePath());

        await service.InitializeAsync();
        var state = await service.GetLocalOperationalStateAsync();

        Assert.Equal(0, state.ActiveShiftRecordCount);
        Assert.Equal(0, state.ActiveCashCustodySessionRecordCount);
        Assert.Null(state.ActiveShift);
        Assert.Null(state.ActiveCashCustodySession);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ActiveShiftWithoutCashCustodySessionRecoversAsShiftOnly()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await RequireSuccess(service.OpenCashierShiftAsync(TestRequests.OpenShift()));

        var restarted = CreateService(databasePath);
        var state = await restarted.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());

        Assert.Equal(1, state.ActiveShiftRecordCount);
        Assert.Equal(0, state.ActiveCashCustodySessionRecordCount);
        Assert.Equal("shift-001", state.ActiveShift?.Id);
        Assert.Null(state.ActiveCashCustodySession);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ActiveShiftRecoversWhenNoConfiguredShiftFilterIsSupplied()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await RequireSuccess(service.OpenCashierShiftAsync(TestRequests.OpenShift()));

        var restarted = CreateService(databasePath);
        var state = await restarted.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: "cashier-001",
            TerminalId: "terminal-001",
            SiteId: "11111111-1111-4111-8111-111111111111",
            SiteGroupId: "22222222-2222-4222-8222-222222222222",
            PosServerId: "pos-server-001"));

        Assert.Equal(1, state.ActiveShiftRecordCount);
        Assert.Equal(0, state.ActiveCashCustodySessionRecordCount);
        Assert.Equal("shift-001", state.ActiveShift?.Id);
        Assert.Equal(CashierShiftStatus.Open, state.ActiveShift?.Status);
        Assert.Null(state.ActiveCashCustodySession);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ActiveShiftAndCashCustodySessionRecoverTogetherAfterRestart()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await RequireSuccess(service.OpenCashierShiftAsync(TestRequests.OpenShift()));
        var session = await RequireSuccess(service.CreateCashCustodySessionAsync(TestRequests.CreateSession()));

        var restarted = CreateService(databasePath);
        var state = await restarted.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());

        Assert.Equal(1, state.ActiveShiftRecordCount);
        Assert.Equal(1, state.ActiveCashCustodySessionRecordCount);
        Assert.Equal("shift-001", state.ActiveShift?.Id);
        Assert.Equal(session.Id, state.ActiveCashCustodySession?.Id);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ClosedShiftDoesNotRecoverAsOpen()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await RequireSuccess(service.OpenCashierShiftAsync(TestRequests.OpenShift()));
        await RequireSuccess(service.CreateCashCustodySessionAsync(TestRequests.CreateSession()));
        await RequireSuccess(service.CloseCashierShiftAsync(new CloseCashierShiftRequest("shift-001", DateTimeOffset.Parse("2026-07-15T08:00:00Z"))));

        var restarted = CreateService(databasePath);
        var state = await restarted.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());

        Assert.Equal(0, state.ActiveShiftRecordCount);
        Assert.Equal(0, state.ActiveCashCustodySessionRecordCount);
        Assert.Null(state.ActiveShift);
        Assert.Null(state.ActiveCashCustodySession);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RepeatedOperationalStateRecoveryDoesNotDuplicateShiftOrCashCustody()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await RequireSuccess(service.OpenCashierShiftAsync(TestRequests.OpenShift()));
        await RequireSuccess(service.CreateCashCustodySessionAsync(TestRequests.CreateSession()));

        var firstRestart = CreateService(databasePath);
        var firstState = await firstRestart.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());
        var secondRestart = CreateService(databasePath);
        var secondState = await secondRestart.GetLocalOperationalStateAsync(TestRequests.LocalOperationalState());

        Assert.Equal(1, firstState.ActiveShiftRecordCount);
        Assert.Equal(1, firstState.ActiveCashCustodySessionRecordCount);
        Assert.Equal(1, secondState.ActiveShiftRecordCount);
        Assert.Equal(1, secondState.ActiveCashCustodySessionRecordCount);
        Assert.Equal(firstState.ActiveShift?.Id, secondState.ActiveShift?.Id);
        Assert.Equal(firstState.ActiveCashCustodySession?.Id, secondState.ActiveCashCustodySession?.Id);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task RestartReusesSameEnvelopeAndPreservesState()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        var session = await service.CreateCashCustodySessionAsync(TestRequests.CreateSession());
        Assert.True(session.IsSuccess);
        var originalEnvelope = ReadEnvelope(EnvelopePath(databasePath));

        var restarted = CreateService(databasePath);
        var readback = await restarted.CreateOrGetCashCustodySessionAsync(TestRequests.CreateSession());
        var restartedEnvelope = ReadEnvelope(EnvelopePath(databasePath));

        Assert.True(readback.IsSuccess);
        Assert.Equal(session.Value!.Id, readback.Value!.Id);
        Assert.Equal(originalEnvelope.KeyId, restartedEnvelope.KeyId);
        Assert.Equal(originalEnvelope.ProtectedKey, restartedEnvelope.ProtectedKey);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task EnvelopeWithoutDatabaseCreatesEncryptedDatabaseUsingExistingEnvelope()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await service.InitializeAsync();
        var envelope = ReadEnvelope(EnvelopePath(databasePath));
        File.Delete(databasePath);

        var restarted = CreateService(databasePath);
        await restarted.InitializeAsync();

        Assert.True(File.Exists(databasePath));
        Assert.False(HasPlainSqliteHeader(databasePath));
        Assert.Equal(envelope.KeyId, ReadEnvelope(EnvelopePath(databasePath)).KeyId);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task DatabaseWithoutEnvelopeFailsClosedAndDoesNotCreateReplacementEnvelope()
    {
        var databasePath = DatabasePath();
        var service = CreateService(databasePath);
        await service.InitializeAsync();
        File.Delete(EnvelopePath(databasePath));

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeMissing, exception.SafeStatus);
        Assert.True(File.Exists(databasePath));
        Assert.False(File.Exists(EnvelopePath(databasePath)));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void MalformedEnvelopeFailsClosed()
    {
        var databasePath = DatabasePath();
        File.WriteAllText(EnvelopePath(databasePath), "{ not-json", Encoding.UTF8);

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeMalformed, exception.SafeStatus);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void UnsupportedEnvelopeVersionFailsClosed()
    {
        var databasePath = DatabasePath();
        WriteEnvelope(databasePath, envelope => envelope with { SchemaVersion = 2 });

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeUnsupportedVersion, exception.SafeStatus);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void WrongDatabaseIdentityFailsClosed()
    {
        var databasePath = DatabasePath();
        WriteEnvelope(databasePath, envelope => envelope with { DatabaseIdentity = "different-database" });

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeWrongIdentity, exception.SafeStatus);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void EmptyProtectedKeyFailsClosed()
    {
        var databasePath = DatabasePath();
        WriteEnvelope(databasePath, envelope => envelope with { ProtectedKey = string.Empty });

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeMalformed, exception.SafeStatus);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task WrongProtectedKeyFailsClosedWithoutDeletingDatabase()
    {
        var databasePath = DatabasePath();
        await CreateService(databasePath).InitializeAsync();
        WriteEnvelope(databasePath, envelope => envelope with
        {
            ProtectedKey = Convert.ToBase64String(new TestLocalDatabaseKeyProtector().Protect(LocalDatabaseKeyGenerator.Generate(), LocalDatabaseKeyEnvelope.EntropyBytes))
        });

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.EncryptedDatabaseUnreadable, exception.SafeStatus);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task DpapiUnprotectFailureFailsClosed()
    {
        var databasePath = DatabasePath();
        await CreateService(databasePath).InitializeAsync();

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(
            () => new CashJournalService(new LocalOperationsDatabaseOptions(
                databasePath,
                DatabaseKeyProtector: new ThrowingKeyProtector())).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.ProtectedKeyUnavailable, exception.SafeStatus);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task LegacyPlaintextDatabaseIsDetectedAndPreserved()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextDatabaseAsync(databasePath);
        var before = File.ReadAllBytes(databasePath);

        var service = CreateService(databasePath);
        var readiness = service.GetLocalPersistenceReadiness();
        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => service.CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired, exception.SafeStatus);
        Assert.True(readiness.MigrationRequired);
        Assert.True(readiness.LegacyPlaintextDetected);
        Assert.False(readiness.CashOperationsAllowed);
        Assert.False(File.Exists(EnvelopePath(databasePath)));
        Assert.Equal(before, File.ReadAllBytes(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CorruptEncryptedDatabaseFailsClosed()
    {
        var databasePath = DatabasePath();
        await CreateService(databasePath).InitializeAsync();
        await File.WriteAllBytesAsync(databasePath, RandomNumberGenerator.GetBytes(128));

        var exception = Assert.Throws<LocalPersistenceUnavailableException>(() => CreateService(databasePath).CreateDbContext());

        Assert.Equal(LocalPersistenceSafeStatus.EncryptedDatabaseUnreadable, exception.SafeStatus);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task CashOperationsAreBlockedWhenPersistenceIsUnsafe()
    {
        var databasePath = DatabasePath();
        await CreatePlaintextDatabaseAsync(databasePath);
        var service = CreateService(databasePath);

        var exception = await Assert.ThrowsAsync<LocalPersistenceUnavailableException>(
            () => service.CreateCashCustodySessionAsync(TestRequests.CreateSession()));

        Assert.Equal(LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired, exception.SafeStatus);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public void LocalPersistenceSafeActionsDoNotExposeSecretOrDiagnosticInternals()
    {
        var statuses = Enum.GetValues<LocalPersistenceSafeStatus>()
            .Where(status => status != LocalPersistenceSafeStatus.Ready)
            .Select(status => new LocalPersistenceReadiness(
                EncryptionConfigured: true,
                DpapiScope: LocalDatabaseKeyEnvelope.CurrentUserScope,
                KeyEnvelopeExists: false,
                KeyAvailable: false,
                DatabaseExists: false,
                DatabaseEncrypted: false,
                LegacyPlaintextDetected: false,
                MigrationRequired: false,
                IntegrityValidated: false,
                SchemaReady: false,
                PersistenceReady: false,
                RecoveryAllowed: false,
                CashOperationsAllowed: false,
                SafeStatus: status,
                SafeAction: status.ToString(),
                DatabasePath: "safe-path",
                KeyEnvelopePath: "safe-envelope"));

        var forbidden = new[]
        {
            "SQLCipher",
            "DPAPI",
            "stack trace",
            "connection string",
            "raw key",
            "protected key",
            ("password" + "="),
            ("PRAGMA " + "key")
        };

        foreach (var readiness in statuses)
        {
            foreach (var pattern in forbidden)
            {
                Assert.DoesNotContain(pattern, readiness.SafeAction, StringComparison.OrdinalIgnoreCase);
            }
        }
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

    private string DatabasePath() => Path.Combine(_directoryPath, $"{Guid.NewGuid():N}.db");

    private static string EnvelopePath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath)!, LocalDatabaseKeyEnvelope.EnvelopeFileName);

    private static CashJournalService CreateService(string databasePath) =>
        new(new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "https://central-pms.example.invalid",
            DatabaseKeyProtector: new TestLocalDatabaseKeyProtector()));

    private static LocalDatabaseKeyEnvelope ReadEnvelope(string envelopePath) =>
        LocalDatabaseKeyEnvelope.Parse(File.ReadAllText(envelopePath, Encoding.UTF8));

    private static void WriteEnvelope(
        string databasePath,
        Func<LocalDatabaseKeyEnvelope, LocalDatabaseKeyEnvelope> mutate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var manager = new LocalDatabaseEncryptionManager(databasePath, new TestLocalDatabaseKeyProtector());
        var key = LocalDatabaseKeyGenerator.Generate();
        var protectedKey = new TestLocalDatabaseKeyProtector().Protect(key, LocalDatabaseKeyEnvelope.EntropyBytes);
        try
        {
            var envelope = LocalDatabaseKeyEnvelope.Create(manager.DatabaseIdentity, protectedKey, DateTimeOffset.UtcNow);
            File.WriteAllText(EnvelopePath(databasePath), mutate(envelope).ToJson(), Encoding.UTF8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static async Task<bool> CanOpenWithEnvelopeAsync(string databasePath)
    {
        await using var dbContext = CreateService(databasePath).CreateDbContext();
        return await dbContext.CashCustodySessions.AnyAsync();
    }

    private static async Task<bool> CanOpenWithoutKeyAsync(string databasePath)
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM cash_custody_sessions;");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<bool> CanOpenWithWrongKeyAsync(string databasePath)
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = ("PRAGMA " + "key = ") + ToSqlCipherRawKeyLiteral(LocalDatabaseKeyGenerator.Generate()) + ";";
            await command.ExecuteNonQueryAsync();
            await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM cash_custody_sessions;");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task CreatePlaintextDatabaseAsync(string databasePath)
    {
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_values (Value TEXT NOT NULL); INSERT INTO legacy_values VALUES ('legacy-cashier-value');";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
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

    private static string ToSqlCipherRawKeyLiteral(byte[] key) =>
        $"\"x'{Convert.ToHexString(key)}'\"";

    private static async Task<T> RequireSuccess<T>(Task<CashJournalResult<T>> operation)
    {
        var result = await operation.ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}

internal sealed class TestLocalDatabaseKeyProtector : ILocalDatabaseKeyProtector
{
    public string Scope => LocalDatabaseKeyEnvelope.CurrentUserScope;

    public byte[] Protect(byte[] plaintextKey, byte[] entropy) => Transform(plaintextKey, entropy);

    public byte[] Unprotect(byte[] protectedKey, byte[] entropy) => Transform(protectedKey, entropy);

    private static byte[] Transform(byte[] source, byte[] entropy)
    {
        var result = new byte[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            result[index] = (byte)(source[index] ^ entropy[index % entropy.Length] ^ 0xA5);
        }

        return result;
    }
}

internal sealed class ThrowingKeyProtector : ILocalDatabaseKeyProtector
{
    public string Scope => LocalDatabaseKeyEnvelope.CurrentUserScope;

    public byte[] Protect(byte[] plaintextKey, byte[] entropy) =>
        new TestLocalDatabaseKeyProtector().Protect(plaintextKey, entropy);

    public byte[] Unprotect(byte[] protectedKey, byte[] entropy) =>
        throw new CryptographicException("Simulated DPAPI failure.");
}
