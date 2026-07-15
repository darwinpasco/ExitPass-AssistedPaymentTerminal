namespace AssistedPaymentTerminal.LocalOperations.Tests;

internal static class RepositoryPath
{
    public static string Find()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ExitPass.AssistedPaymentTerminal.sln")))
            {
                return Path.GetFullPath(current.FullName);
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
