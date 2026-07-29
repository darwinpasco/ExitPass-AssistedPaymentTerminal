namespace AssistedPaymentTerminal.LocalOperations;

public enum LocalPersistenceSafeStatus
{
    Ready = 1,
    InitializingEncryptedStorage = 2,
    LegacyPlaintextMigrationRequired = 3,
    KeyEnvelopeMissing = 4,
    KeyEnvelopeMalformed = 5,
    KeyEnvelopeUnsupportedVersion = 6,
    KeyEnvelopeWrongIdentity = 7,
    KeyEnvelopeWrongScope = 8,
    ProtectedKeyUnavailable = 9,
    EncryptedDatabaseUnreadable = 10,
    CorruptDatabase = 11,
    ConfigurationInvalid = 12
}

public sealed record LocalPersistenceReadiness(
    bool EncryptionConfigured,
    string DpapiScope,
    bool KeyEnvelopeExists,
    bool KeyAvailable,
    bool DatabaseExists,
    bool DatabaseEncrypted,
    bool LegacyPlaintextDetected,
    bool MigrationRequired,
    bool IntegrityValidated,
    bool SchemaReady,
    bool PersistenceReady,
    bool RecoveryAllowed,
    bool CashOperationsAllowed,
    LocalPersistenceSafeStatus SafeStatus,
    string SafeAction,
    string DatabasePath,
    string KeyEnvelopePath);

public sealed class LocalPersistenceUnavailableException : Exception
{
    public LocalPersistenceUnavailableException(
        LocalPersistenceSafeStatus safeStatus,
        string safeAction,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SafeStatus = safeStatus;
        SafeAction = safeAction;
    }

    public LocalPersistenceSafeStatus SafeStatus { get; }

    public string SafeAction { get; }
}
