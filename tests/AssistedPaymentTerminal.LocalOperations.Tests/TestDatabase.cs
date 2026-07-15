using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

internal sealed class TestDatabase : IDisposable
{
    private TestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "cash-journal-test.db");
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public static TestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.LocalOperations.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new TestDatabase(directoryPath);
    }

    public CashJournalService CreateService() =>
        new(new LocalOperationsDatabaseOptions(DatabasePath));

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
