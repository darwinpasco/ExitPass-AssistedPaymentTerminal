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

    public LocalOperationsDatabaseOptions Options =>
        new(DatabasePath, CentralPmsBaseUrl: "https://central-pms.example.invalid");

    public static TestDatabase Create()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "ExitPass.APT.LocalOperations.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new TestDatabase(directoryPath);
    }

    public CashJournalService CreateService() =>
        new(Options);

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
