using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public sealed record GovernedDeviceTrustState(
    int SchemaVersion,
    Guid DeviceId,
    string DeviceReference,
    Guid ServiceIdentityId,
    string DeviceType,
    Guid SiteId,
    Guid SiteGroupId,
    string TerminalIdentifier,
    long CredentialVersion,
    string RuntimeCredentialType,
    string CertificateStoreThumbprint,
    string CertificateSha256Fingerprint,
    Guid EnrollmentCorrelationId,
    DateTimeOffset EnrolledAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string AssistedPaymentTerminalDeviceType = "ASSISTED_PAYMENT_TERMINAL";
    public const string MtlsCertificateCredentialType = "MTLS_CERTIFICATE_REFERENCE";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            DeviceId == Guid.Empty ||
            ServiceIdentityId == Guid.Empty ||
            SiteId == Guid.Empty ||
            SiteGroupId == Guid.Empty ||
            CredentialVersion <= 0 ||
            EnrollmentCorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(DeviceReference) ||
            !string.Equals(DeviceType, AssistedPaymentTerminalDeviceType, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(TerminalIdentifier) ||
            !string.Equals(RuntimeCredentialType, MtlsCertificateCredentialType, StringComparison.Ordinal) ||
            !IsHex(CertificateStoreThumbprint, 40) ||
            !IsHex(CertificateSha256Fingerprint, 64))
        {
            throw new InvalidOperationException("The governed APT device-trust state is incomplete or invalid.");
        }
    }

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}

public interface IGovernedDeviceTrustStateStore
{
    GovernedDeviceTrustState? Load();
}

public interface IGovernedDeviceTrustStateWriter : IGovernedDeviceTrustStateStore
{
    string Path { get; }
    bool Exists { get; }
    void ValidateWriteReadiness();
    void Save(GovernedDeviceTrustState state);
}

public interface IGovernedDeviceTrustProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] protectedPayload, byte[] entropy);
}

public sealed class WindowsCurrentUserGovernedDeviceTrustProtector : IGovernedDeviceTrustProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy) =>
        ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedPayload, byte[] entropy) =>
        ProtectedData.Unprotect(protectedPayload, entropy, DataProtectionScope.CurrentUser);
}

public sealed class DpapiCurrentUserGovernedDeviceTrustStateStore : IGovernedDeviceTrustStateWriter
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("ExitPass.APT.GovernedDeviceTrustState.v1"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly IGovernedDeviceTrustProtector _protector;

    public DpapiCurrentUserGovernedDeviceTrustStateStore(
        string? path = null,
        IGovernedDeviceTrustProtector? protector = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : System.IO.Path.GetFullPath(path);
        _protector = protector ?? new WindowsCurrentUserGovernedDeviceTrustProtector();
    }

    public string Path => _path;
    public bool Exists => File.Exists(_path);

    public GovernedDeviceTrustState? Load()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Governed APT device trust requires Windows DPAPI CurrentUser.");
        if (!File.Exists(_path)) return null;

        try
        {
            var protectedPayload = File.ReadAllBytes(_path);
            var plaintext = _protector.Unprotect(protectedPayload, Entropy);
            try
            {
                var state = JsonSerializer.Deserialize<GovernedDeviceTrustState>(plaintext, JsonOptions);
                state?.Validate();
                return state;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The governed APT device-trust state cannot be read safely. Re-provision this terminal through Device Administration.",
                exception);
        }
    }

    public void ValidateWriteReadiness()
    {
        EnsureWindows();
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var plaintext = RandomNumberGenerator.GetBytes(32);
        byte[]? protectedPayload = null;
        byte[]? roundTrip = null;
        var probePath = _path + $".{Guid.NewGuid():N}.write-probe";
        try
        {
            protectedPayload = _protector.Protect(plaintext, Entropy);
            roundTrip = _protector.Unprotect(protectedPayload, Entropy);
            if (!CryptographicOperations.FixedTimeEquals(plaintext, roundTrip))
                throw new CryptographicException("The DPAPI readiness round trip did not preserve the payload.");

            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            stream.Write(protectedPayload);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The governed APT device-trust state cannot be protected and persisted by this Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedPayload is not null) CryptographicOperations.ZeroMemory(protectedPayload);
            if (roundTrip is not null) CryptographicOperations.ZeroMemory(roundTrip);
            TryDelete(probePath);
        }
    }

    public void Save(GovernedDeviceTrustState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        EnsureWindows();

        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        byte[]? protectedPayload = null;
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            protectedPayload = _protector.Protect(plaintext, Entropy);
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedPayload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The governed APT device-trust state could not be persisted safely.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedPayload is not null) CryptographicOperations.ZeroMemory(protectedPayload);
            TryDelete(temporary);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Governed APT device trust requires Windows DPAPI CurrentUser.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the primary DPAPI or persistence failure for the operator.
        }
    }

    private static string DefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(localAppData, "ExitPass", "AssistedPaymentTerminal", "DeviceTrust", "state.bin");
    }
}
