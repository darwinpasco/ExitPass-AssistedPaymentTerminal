using System.Text;
using AssistedPaymentTerminal.SqliteEncryptionProviderProof;
using Xunit;

namespace AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests;

public sealed class SqliteEncryptionProviderProofTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        "exitpass-apt-sqlite-encryption-tests",
        Guid.NewGuid().ToString("N"));

    public SqliteEncryptionProviderProofTests()
    {
        Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    public async Task SqlcipherProviderEncryptsReopensFailsWrongKeyAndRekeys()
    {
        var databasePath = Path.Combine(_directoryPath, "proof.db");

        var result = await SqliteEncryptionProviderProofRunner.RunAsync(
            new ProofOptions(databasePath, KeepArtifacts: true));

        Assert.Equal(SqliteEncryptionProviderProofRunner.ProviderName, result.Provider);
        Assert.True(result.CorrectKeyOpened);
        Assert.True(result.NoKeyFailed);
        Assert.True(result.WrongKeyFailed);
        Assert.True(result.EncryptedHeaderConfirmed);
        Assert.True(result.KnownValueHidden);
        Assert.True(result.RekeySucceeded);
        Assert.True(result.OldKeyRejected);
        Assert.True(result.NewKeyOpened);
        Assert.True(result.PlaintextMigrationFeasible);
        var databaseBytes = File.ReadAllBytes(databasePath);

        Assert.False(ContainsBytes(databaseBytes, Encoding.ASCII.GetBytes("SQLite format 3\0")));
        Assert.False(ContainsBytes(databaseBytes, Encoding.UTF8.GetBytes(SqliteEncryptionProviderProofRunner.KnownValue)));
    }

    [Fact]
    public void ProofOptionsParseDatabasePathAndKeepArtifacts()
    {
        var options = ProofOptions.Parse(["--database-path", "proof.db", "--keep-artifacts"]);

        Assert.Equal("proof.db", options.DatabasePath);
        Assert.True(options.KeepArtifacts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }
}
