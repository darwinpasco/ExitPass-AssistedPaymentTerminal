using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.PlaintextDatabaseMigration;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        var service = new LocalDatabasePlaintextMigrationService(options);
        var result = options.DryRun && !options.Rollback
            ? await service.ClassifyAsync().ConfigureAwait(false)
            : await service.MigrateAsync().ConfigureAwait(false);

        Console.WriteLine("APT plaintext database migration maintenance result");
        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine($"Phase: {result.Phase}");
        Console.WriteLine($"Operation ID: {result.OperationId ?? "None"}");
        Console.WriteLine($"Support reference: {result.SupportReference}");
        Console.WriteLine($"Database path: {result.DatabasePath}");
        Console.WriteLine($"Envelope path: {result.EnvelopePath}");
        Console.WriteLine($"Message: {result.SafeMessage}");
        Console.WriteLine($"Action: {result.SafeAction}");
        Console.WriteLine($"Source rows: {JsonSerializer.Serialize(result.SourceRowCounts)}");
        Console.WriteLine($"Target rows: {JsonSerializer.Serialize(result.TargetRowCounts)}");
        Console.WriteLine($"Source hash present: {(!string.IsNullOrWhiteSpace(result.SourceHash))}");
        Console.WriteLine($"Backup hash present: {(!string.IsNullOrWhiteSpace(result.BackupHash))}");
        Console.WriteLine($"Target hash present: {(!string.IsNullOrWhiteSpace(result.TargetHash))}");
        Console.WriteLine($"Envelope hash present: {(!string.IsNullOrWhiteSpace(result.EnvelopeHash))}");

        return result.Succeeded
            || result.Status is LocalDatabasePlaintextMigrationStatus.MigrationRequired
                or LocalDatabasePlaintextMigrationStatus.AlreadyEncrypted
                or LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted
                or LocalDatabasePlaintextMigrationStatus.NoDatabase
            ? 0
            : 2;
    }

    private static LocalDatabasePlaintextMigrationOptions Parse(IReadOnlyList<string> args)
    {
        string? databasePath = null;
        var authorized = false;
        var dryRun = false;
        var rollback = false;
        string? operationId = null;
        string? identity = null;

        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            if (value.StartsWith("--local-db-path=", StringComparison.OrdinalIgnoreCase))
            {
                databasePath = value["--local-db-path=".Length..];
                continue;
            }

            if (string.Equals(value, "--local-db-path", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                databasePath = args[++index];
                continue;
            }

            if (string.Equals(value, "--authorize-offline-migration", StringComparison.OrdinalIgnoreCase))
            {
                authorized = true;
                continue;
            }

            if (string.Equals(value, "--dry-classify", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (string.Equals(value, "--rollback", StringComparison.OrdinalIgnoreCase))
            {
                rollback = true;
                authorized = true;
                continue;
            }

            if (string.Equals(value, "--operation-id", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                operationId = args[++index];
                continue;
            }

            if (string.Equals(value, "--expected-windows-identity", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                identity = args[++index];
            }
        }

        return new LocalDatabasePlaintextMigrationOptions(
            databasePath,
            authorized,
            dryRun,
            rollback,
            operationId,
            identity);
    }
}
