using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AssistedPaymentTerminal.Desktop;

public sealed class GovernedCentralPmsHttpClientFactory
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private readonly GovernedDeviceTrustState _state;
    private readonly IGovernedDeviceCertificateStore _certificateStore;
    private readonly Func<DateTimeOffset> _utcNow;

    public GovernedCentralPmsHttpClientFactory(
        GovernedDeviceTrustState state,
        IGovernedDeviceCertificateStore certificateStore,
        Func<DateTimeOffset>? utcNow = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _state.Validate();
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public HttpClient Create(TimeSpan timeout)
    {
        var certificate = LoadValidatedCertificate();
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    internal X509Certificate2 LoadValidatedCertificate()
    {
        var certificate = _certificateStore.Load(_state.CertificateStoreThumbprint);
        try
        {
            var fingerprint = WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate);
            var now = _utcNow().UtcDateTime;
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(fingerprint),
                    Convert.FromHexString(_state.CertificateSha256Fingerprint)) ||
                !certificate.HasPrivateKey ||
                certificate.NotBefore.ToUniversalTime() > now ||
                certificate.NotAfter.ToUniversalTime() <= now ||
                !HasClientAuthenticationUsage(certificate))
            {
                throw new InvalidOperationException(
                    "The governed APT client certificate does not match the current enrolled credential or is not usable.");
            }

            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static bool HasClientAuthenticationUsage(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => string.Equals(oid.Value, ClientAuthenticationOid, StringComparison.Ordinal));
}
