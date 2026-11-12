using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AssistedPaymentTerminal.Desktop;

public sealed record GeneratedGovernedDeviceCertificate(
    string StoreThumbprint,
    string Sha256Fingerprint,
    X509Certificate2 Certificate) : IDisposable
{
    public void Dispose() => Certificate.Dispose();
}

public interface IGovernedDeviceCertificateStore
{
    X509Certificate2 Load(string storeThumbprint);
}

public interface IGovernedDeviceEnrollmentCertificateStore : IGovernedDeviceCertificateStore
{
    GeneratedGovernedDeviceCertificate Create(string deviceReference);
    void Delete(string storeThumbprint);
}

public sealed class WindowsCurrentUserGovernedDeviceCertificateStore : IGovernedDeviceEnrollmentCertificateStore
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    public GeneratedGovernedDeviceCertificate Create(string deviceReference)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Governed APT device certificates require the Windows CurrentUser certificate store.");
        if (string.IsNullOrWhiteSpace(deviceReference))
            throw new ArgumentException("A canonical device reference is required.", nameof(deviceReference));

        using var rsa = RSA.Create(3072);
        var subjectReference = new string(deviceReference
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(64)
            .ToArray());
        if (subjectReference.Length == 0)
            throw new ArgumentException("The canonical device reference cannot form a certificate subject.", nameof(deviceReference));
        var request = new CertificateRequest(
            $"CN=ExitPass APT {subjectReference}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ClientAuthenticationOid) },
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var transient = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pfx = transient.Export(X509ContentType.Pfx, password);
        string? addedThumbprint = null;
        try
        {
            using var persisted = new X509Certificate2(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(persisted);
            addedThumbprint = Normalize(persisted.Thumbprint);
            var loaded = Load(addedThumbprint);
            return new GeneratedGovernedDeviceCertificate(
                addedThumbprint,
                Sha256(loaded),
                loaded);
        }
        catch
        {
            if (addedThumbprint is not null)
            {
                try
                {
                    Delete(addedThumbprint);
                }
                catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // Preserve the certificate-creation failure; the exact thumbprint remains known to the operator.
                }
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

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

    public void Delete(string storeThumbprint)
    {
        var normalized = Normalize(storeThumbprint);
        if (normalized.Length != 40) return;

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        foreach (var certificate in matches)
        {
            try
            {
                using var privateKey = certificate.GetRSAPrivateKey();
                if (privateKey is RSACng cng && !cng.Key.IsEphemeral) cng.Key.Delete();
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Removing the certificate still prevents this exact thumbprint from being selected.
            }
            store.Remove(certificate);
            certificate.Dispose();
        }
    }

    public static string Sha256(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

    internal static string Normalize(string? thumbprint) =>
        (thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
