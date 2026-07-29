using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop;

public sealed class LocalPersistenceStartupException : Exception
{
    public LocalPersistenceStartupException(LocalPersistenceReadiness readiness, Exception? innerException = null)
        : base(readiness.SafeAction, innerException)
    {
        Readiness = readiness;
    }

    public LocalPersistenceReadiness Readiness { get; }
}

public static class LocalPersistenceStartupInitializer
{
    public static async Task<LocalPersistenceReadiness> InitializeAsync(
        CashJournalService journal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LocalPersistenceUnavailableException exception)
        {
            throw new LocalPersistenceStartupException(journal.GetLocalPersistenceReadiness(), exception);
        }

        var readiness = journal.GetLocalPersistenceReadiness();
        if (!readiness.PersistenceReady || !readiness.RecoveryAllowed || !readiness.CashOperationsAllowed)
        {
            throw new LocalPersistenceStartupException(readiness);
        }

        return readiness;
    }
}
