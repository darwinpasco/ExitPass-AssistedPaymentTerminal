using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed record LocalDatabaseKeyEnvelope(
    int SchemaVersion,
    string DatabaseIdentity,
    string KeyId,
    string ProtectionScope,
    string EntropyId,
    string KeyAlgorithm,
    string ProtectedKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastProtectedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentUserScope = "CurrentUser";
    public const string CurrentEntropyId = "ExitPass.APT.LocalOperations.SqlCipherKeyEnvelope.v1";
    public const string CurrentKeyAlgorithm = "SQLCipher-raw-256-bit";
    public const string EnvelopeFileName = "cash-journal.key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static LocalDatabaseKeyEnvelope Create(
        string databaseIdentity,
        byte[] protectedKey,
        DateTimeOffset now) =>
        new(
            CurrentSchemaVersion,
            databaseIdentity,
            Guid.NewGuid().ToString("D"),
            CurrentUserScope,
            CurrentEntropyId,
            CurrentKeyAlgorithm,
            Convert.ToBase64String(protectedKey),
            now,
            now);

    public static LocalDatabaseKeyEnvelope Parse(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<LocalDatabaseKeyEnvelope>(json, JsonOptions);
            return envelope ?? throw new JsonException("Envelope payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMalformed,
                "Contact support. The local protected storage key envelope is malformed.",
                "The local protected storage key envelope is malformed.",
                exception);
        }
    }

    public byte[] DecodeProtectedKey()
    {
        try
        {
            return Convert.FromBase64String(ProtectedKey);
        }
        catch (FormatException exception)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMalformed,
                "Contact support. The local protected storage key envelope is malformed.",
                "The local protected storage key envelope is malformed.",
                exception);
        }
    }

    public void Validate(string expectedDatabaseIdentity)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeUnsupportedVersion,
                "Update or support intervention is required before local cash operations can continue.",
                "The local protected storage key envelope version is unsupported.");
        }

        if (!string.Equals(DatabaseIdentity, expectedDatabaseIdentity, StringComparison.Ordinal))
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeWrongIdentity,
                "Contact support. The local protected storage key envelope does not match this database.",
                "The local protected storage key envelope does not match this database.");
        }

        if (!string.Equals(ProtectionScope, CurrentUserScope, StringComparison.Ordinal))
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeWrongScope,
                "Contact support. This terminal requires a CurrentUser-protected storage key envelope.",
                "The local protected storage key envelope does not use the required CurrentUser scope.");
        }

        if (string.IsNullOrWhiteSpace(ProtectedKey))
        {
            throw new LocalPersistenceUnavailableException(
                LocalPersistenceSafeStatus.KeyEnvelopeMalformed,
                "Contact support. The local protected storage key envelope does not contain protected key material.",
                "The local protected storage key envelope has an empty protected key.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static byte[] EntropyBytes => Encoding.UTF8.GetBytes(CurrentEntropyId);
}
