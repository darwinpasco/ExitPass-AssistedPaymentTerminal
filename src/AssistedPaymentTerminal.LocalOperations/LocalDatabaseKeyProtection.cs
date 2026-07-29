using System.Security.Cryptography;

namespace AssistedPaymentTerminal.LocalOperations;

public interface ILocalDatabaseKeyProtector
{
    string Scope { get; }

    byte[] Protect(byte[] plaintextKey, byte[] entropy);

    byte[] Unprotect(byte[] protectedKey, byte[] entropy);
}

public sealed class DpapiCurrentUserLocalDatabaseKeyProtector : ILocalDatabaseKeyProtector
{
    public string Scope => LocalDatabaseKeyEnvelope.CurrentUserScope;

    public byte[] Protect(byte[] plaintextKey, byte[] entropy) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plaintextKey, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("DPAPI CurrentUser local database key protection requires Windows.");

    public byte[] Unprotect(byte[] protectedKey, byte[] entropy) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("DPAPI CurrentUser local database key protection requires Windows.");
}

public static class LocalDatabaseKeyGenerator
{
    public const int KeyLengthBytes = 32;

    public static byte[] Generate() => RandomNumberGenerator.GetBytes(KeyLengthBytes);
}
