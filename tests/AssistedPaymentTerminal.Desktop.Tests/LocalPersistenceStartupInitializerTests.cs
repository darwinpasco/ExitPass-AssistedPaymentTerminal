using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class LocalPersistenceStartupInitializerTests
{
    [Fact]
    public async Task StartupEagerlyCreatesEncryptedDatabaseAndEnvelope()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        var journal = new CashJournalService(new LocalOperationsDatabaseOptions(databasePath));

        var readiness = await LocalPersistenceStartupInitializer.InitializeAsync(journal);

        Assert.True(File.Exists(databasePath));
        Assert.True(File.Exists(Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName)));
        Assert.True(readiness.PersistenceReady);
        Assert.True(readiness.RecoveryAllowed);
        Assert.True(readiness.CashOperationsAllowed);
        Assert.False(HasPlainSqliteHeader(databasePath));
    }

    [Fact]
    public async Task ExistingEnvelopeWithAbsentDatabaseCreatesEncryptedDatabaseUsingSameEnvelope()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        var journal = new CashJournalService(new LocalOperationsDatabaseOptions(databasePath));
        await LocalPersistenceStartupInitializer.InitializeAsync(journal);
        var envelopePath = Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName);
        var envelopeHash = File.ReadAllText(envelopePath);
        File.Delete(databasePath);

        var readiness = await LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath)));

        Assert.True(readiness.PersistenceReady);
        Assert.True(File.Exists(databasePath));
        Assert.Equal(envelopeHash, File.ReadAllText(envelopePath));
        Assert.False(HasPlainSqliteHeader(databasePath));
    }

    [Fact]
    public async Task ExistingDatabaseWithMissingEnvelopeFailsClosedBeforeOperationalStartup()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        await LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath)));
        File.Delete(Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName));

        var exception = await Assert.ThrowsAsync<LocalPersistenceStartupException>(
            () => LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath))));

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeMissing, exception.Readiness.SafeStatus);
        Assert.False(exception.Readiness.PersistenceReady);
        Assert.False(exception.Readiness.CashOperationsAllowed);
        Assert.False(File.Exists(Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName)));
    }

    [Fact]
    public async Task MalformedEnvelopeFailsClosedBeforeOperationalStartup()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        await LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath)));
        var databaseHash = Convert.ToHexString(await File.ReadAllBytesAsync(databasePath));
        var envelopePath = Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName);
        await File.WriteAllTextAsync(envelopePath, "{ malformed manual validation envelope");
        var malformedEnvelope = await File.ReadAllTextAsync(envelopePath);

        var exception = await Assert.ThrowsAsync<LocalPersistenceStartupException>(
            () => LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath))));

        Assert.Equal(LocalPersistenceSafeStatus.KeyEnvelopeMalformed, exception.Readiness.SafeStatus);
        Assert.False(exception.Readiness.PersistenceReady);
        Assert.False(exception.Readiness.CashOperationsAllowed);
        Assert.Equal(databaseHash, Convert.ToHexString(await File.ReadAllBytesAsync(databasePath)));
        Assert.Equal(malformedEnvelope, await File.ReadAllTextAsync(envelopePath));
    }

    [Fact]
    public async Task LegacyPlaintextDatabaseFailsClosedBeforeOperationalStartup()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        CreatePlaintextDatabase(databasePath);

        var exception = await Assert.ThrowsAsync<LocalPersistenceStartupException>(
            () => LocalPersistenceStartupInitializer.InitializeAsync(new CashJournalService(new LocalOperationsDatabaseOptions(databasePath))));

        Assert.Equal(LocalPersistenceSafeStatus.LegacyPlaintextMigrationRequired, exception.Readiness.SafeStatus);
        Assert.False(exception.Readiness.PersistenceReady);
        Assert.False(exception.Readiness.CashOperationsAllowed);
        Assert.True(HasPlainSqliteHeader(databasePath));
        Assert.False(File.Exists(Path.Combine(directory.Path, LocalDatabaseKeyEnvelope.EnvelopeFileName)));
    }

    [Fact]
    public void StartupOptionsUseCommandLineLocalDatabasePathBeforeEnvironment()
    {
        using var env = TemporaryEnvironmentVariable.Set("APT_LOCAL_DB_PATH", @"C:\apt-env\cash-journal.db");

        var options = StartupOptions.FromEnvironmentAndArgs(["--local-db-path=C:\\apt-arg\\cash-journal.db"]);

        Assert.Equal(@"C:\apt-arg\cash-journal.db", options.LocalDatabasePath);
    }

    [Fact]
    public void StartupOptionsUseEnvironmentLocalDatabasePathWhenArgumentIsAbsent()
    {
        using var env = TemporaryEnvironmentVariable.Set("APT_LOCAL_DB_PATH", @"C:\apt-env\cash-journal.db");

        var options = StartupOptions.FromEnvironmentAndArgs([]);

        Assert.Equal(@"C:\apt-env\cash-journal.db", options.LocalDatabasePath);
    }

    [Fact]
    public void StartupOptionsUseCommandLineManualProofDiagnosticPathBeforeEnvironment()
    {
        using var env = TemporaryEnvironmentVariable.Set("APT_MANUAL_PROOF_DIAGNOSTIC_PATH", @"C:\apt-env\diagnostic.jsonl");

        var options = StartupOptions.FromEnvironmentAndArgs(["--manual-proof-diagnostic-path=C:\\apt-arg\\diagnostic.jsonl"]);

        Assert.Equal(@"C:\apt-arg\diagnostic.jsonl", options.ManualProofDiagnosticPath);
    }

    [Fact]
    public void DesktopLocalOperationsOptionsResolveOneEffectivePathForEveryService()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "cash-journal.db");
        var options = new StartupOptions(
            Profile: "CASHIER_ASSISTED_TERMINAL",
            DevelopmentWebUiUrl: null,
            BaseDirectory: AppContext.BaseDirectory,
            PreferPackagedAssets: true,
            SmokeCheckOnly: false,
            LocalDatabasePath: databasePath,
            CentralPmsBaseUrl: "https://central-pms.example.invalid");

        var localOptions = MainWindow.CreateLocalOperationsOptions(options);
        var expectedPath = Path.GetFullPath(databasePath);

        Assert.Equal(expectedPath, localOptions.DatabasePath);
        Assert.Equal(expectedPath, new CashJournalService(localOptions).DatabasePath);
        Assert.Equal(expectedPath, new TerminalCashPaymentSubmissionService(new CentralPmsTerminalCashPaymentClient(new HttpClient()), localOptions).DatabasePath);
        Assert.Equal(expectedPath, new TerminalCashFiscalSubmissionService(new CentralPmsTerminalCashFiscalClient(new HttpClient()), localOptions).DatabasePath);
        Assert.Equal(expectedPath, new TerminalCashReceiptRetrievalService(new CentralPmsTerminalCashReceiptClient(new HttpClient()), localOptions).DatabasePath);
        Assert.Equal(expectedPath, new TerminalCashReceiptPrintJobService(localOptions).DatabasePath);
    }

    private static void CreatePlaintextDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE manual_plaintext_fixture (id TEXT NOT NULL PRIMARY KEY);";
        command.ExecuteNonQuery();
    }

    private static bool HasPlainSqliteHeader(string path)
    {
        var expected = "SQLite format 3"u8.ToArray();
        var actual = File.ReadAllBytes(path).Take(expected.Length).ToArray();
        return actual.SequenceEqual(expected);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ExitPass.APT.Desktop.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        private TemporaryEnvironmentVariable(string name, string value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static TemporaryEnvironmentVariable Set(string name, string value) => new(name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
    }
}
