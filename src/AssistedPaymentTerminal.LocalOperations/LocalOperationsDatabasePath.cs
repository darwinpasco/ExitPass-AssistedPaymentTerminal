namespace AssistedPaymentTerminal.LocalOperations;

public static class LocalOperationsDatabasePath
{
    public const string DefaultDatabaseFileName = "cash-journal.db";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.Combine(Path.GetTempPath(), "ExitPass", "AssistedPaymentTerminal")
            : Path.Combine(localApplicationData, "ExitPass", "AssistedPaymentTerminal");

        return Path.Combine(root, "LocalOperations", DefaultDatabaseFileName);
    }
}
