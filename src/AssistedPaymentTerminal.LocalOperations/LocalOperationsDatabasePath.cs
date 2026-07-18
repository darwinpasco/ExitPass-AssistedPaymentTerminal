namespace AssistedPaymentTerminal.LocalOperations;

public static class LocalOperationsDatabasePath
{
    public const string DefaultDatabaseFileName = "cash-journal.db";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (ContainsUnresolvedPlaceholder(overridePath))
            {
                throw new LocalOperationsDatabaseConfigurationException(
                    "APT_LOCAL_DB_PATH contains an unresolved placeholder value.");
            }

            return Path.GetFullPath(overridePath);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.Combine(Path.GetTempPath(), "ExitPass", "AssistedPaymentTerminal")
            : Path.Combine(localApplicationData, "ExitPass", "AssistedPaymentTerminal");

        return Path.Combine(root, "LocalOperations", DefaultDatabaseFileName);
    }

    private static bool ContainsUnresolvedPlaceholder(string path) =>
        path.Contains('<', StringComparison.Ordinal)
        || path.Contains('>', StringComparison.Ordinal)
        || path.Contains("${", StringComparison.Ordinal)
        || path.Contains("$env:", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%APT_", StringComparison.OrdinalIgnoreCase);
}
