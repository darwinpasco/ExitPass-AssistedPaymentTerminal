namespace AssistedPaymentTerminal.LocalOperations;

public sealed class LocalOperationsDatabaseConfigurationException : Exception
{
    public LocalOperationsDatabaseConfigurationException(string message)
        : base(message)
    {
    }
}
