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

public sealed class DpapiCurrentUserGovernedDeviceTrustStateStore : IGovernedDeviceTrustStateStore
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("ExitPass.APT.GovernedDeviceTrustState.v1"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public DpapiCurrentUserGovernedDeviceTrustStateStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : Path.GetFullPath(path);
    }

    public GovernedDeviceTrustState? Load()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Governed APT device trust requires Windows DPAPI CurrentUser.");
        if (!File.Exists(_path)) return null;

        try
        {
            var protectedPayload = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(protectedPayload, Entropy, DataProtectionScope.CurrentUser);
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

    private static string DefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ExitPass", "AssistedPaymentTerminal", "DeviceTrust", "state.bin");
    }
}
