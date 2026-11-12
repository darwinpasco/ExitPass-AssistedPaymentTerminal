using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AssistedPaymentTerminal.Desktop;

public interface IGovernedDeviceCertificateStore
{
    X509Certificate2 Load(string storeThumbprint);
}

public sealed class WindowsCurrentUserGovernedDeviceCertificateStore : IGovernedDeviceCertificateStore
{
    public X509Certificate2 Load(string storeThumbprint)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Governed APT device certificates require the Windows CurrentUser certificate store.");

        var normalized = Normalize(storeThumbprint);
        if (normalized.Length != 40 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The governed APT certificate-store reference is invalid.");

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (matches.Count != 1)
            throw new InvalidOperationException("The governed APT client certificate is missing or ambiguous.");

        return matches[0];
    }

    public static string Sha256(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

    private static string Normalize(string? thumbprint) =>
        (thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
