using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class GovernedDeviceTrustTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "exitpass-apt-trust-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnrolledClientCertificate_IsAcceptedWhenFingerprintAndValidityMatch()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var state = ValidState(certificate);
        var factory = new GovernedCentralPmsHttpClientFactory(state, new FixedCertificateStore(certificate));

        using var loaded = factory.LoadValidatedCertificate();

        Assert.Equal(state.CertificateSha256Fingerprint, WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(loaded));
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void MissingClientCertificate_IsRejected()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var factory = new GovernedCentralPmsHttpClientFactory(
            ValidState(certificate),
            new ThrowingCertificateStore());

        Assert.Throws<InvalidOperationException>(() => factory.LoadValidatedCertificate());
    }

    [Fact]
    public void CorruptedDpapiTrustState_IsRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "state.bin");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(128));

        var store = new DpapiCurrentUserGovernedDeviceTrustStateStore(path);

        Assert.Throws<InvalidOperationException>(() => store.Load());
    }

    [Fact]
    public void CertificateFingerprintMismatch_IsRejected()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var state = ValidState(certificate) with { CertificateSha256Fingerprint = new string('0', 64) };
        var factory = new GovernedCentralPmsHttpClientFactory(state, new FixedCertificateStore(certificate));

        Assert.Throws<InvalidOperationException>(() => factory.LoadValidatedCertificate());
    }

    [Fact]
    public void ExpiredClientCertificate_IsRejected()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        var factory = new GovernedCentralPmsHttpClientFactory(
            ValidState(certificate),
            new FixedCertificateStore(certificate));

        Assert.Throws<InvalidOperationException>(() => factory.LoadValidatedCertificate());
    }

    [Fact]
    public void CertificateWithoutPrivateKey_IsRejected()
    {
        using var enrolled = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var publicOnly = new X509Certificate2(enrolled.Export(X509ContentType.Cert));
        var factory = new GovernedCentralPmsHttpClientFactory(
            ValidState(publicOnly),
            new FixedCertificateStore(publicOnly));

        Assert.Throws<InvalidOperationException>(() => factory.LoadValidatedCertificate());
    }

    [Fact]
    public async Task RevokedCredential_IsRejectedByCanonicalAuthority()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var state = ValidState(certificate);
        var correlationId = Guid.NewGuid();
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(state.ServiceIdentityId.ToString("D"), request.Headers.GetValues("X-ExitPass-Service-Identity-Id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new AptHumanAuthenticationResponse(
                    "DENIED",
                    false,
                    null,
                    null,
                    "DEVICE_CREDENTIAL_REVOKED",
                    false,
                    correlationId))
            });
        });
        var client = new CentralPmsHumanSessionClient(
            new HttpClient(handler),
            "https://central-pms.test",
            state.ServiceIdentityId.ToString("D"));

        var result = await client.LoginAsync("cashier.test", "not-a-real-password", state.SiteId, correlationId, default);

        Assert.False(result.Ok);
        Assert.Equal("DEVICE_CREDENTIAL_REVOKED", result.ErrorCode);
    }

    [Fact]
    public async Task IncorrectServiceIdentity_IsRejectedByCanonicalAuthority()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var state = ValidState(certificate);
        var correlationId = Guid.NewGuid();
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(state.ServiceIdentityId.ToString("D"), request.Headers.GetValues("X-ExitPass-Service-Identity-Id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new AptHumanAuthenticationResponse(
                    "DENIED",
                    false,
                    null,
                    null,
                    "AUDIENCE_DEVICE_MISMATCH",
                    false,
                    correlationId))
            });
        });
        var client = new CentralPmsHumanSessionClient(
            new HttpClient(handler),
            "https://central-pms.test",
            state.ServiceIdentityId.ToString("D"));

        var result = await client.LoginAsync("cashier.test", "not-a-real-password", state.SiteId, correlationId, default);

        Assert.False(result.Ok);
        Assert.Equal("AUDIENCE_DEVICE_MISMATCH", result.ErrorCode);
    }

    [Fact]
    public void RuntimeContext_MustMatchEnrolledTerminalSiteAndHttpsAuthority()
    {
        using var certificate = CreateClientCertificate(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var state = ValidState(certificate);
        var options = Options(state);

        MainWindow.ValidateConfiguredDeviceContext(options, state);
        var humanOptions = MainWindow.CreateHumanSessionOptions(options, state);

        Assert.Equal(state.ServiceIdentityId, humanOptions!.DeviceServiceIdentityId);
        Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateConfiguredDeviceContext(options with { TerminalId = "WRONG" }, state));
        Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateConfiguredDeviceContext(options with { SiteId = Guid.NewGuid().ToString("D") }, state));
        Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateConfiguredDeviceContext(options with { CentralPmsBaseUrl = "http://central-pms.test" }, state));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static StartupOptions Options(GovernedDeviceTrustState state) => new(
        "CASHIER_ASSISTED_TERMINAL",
        null,
        AppContext.BaseDirectory,
        PreferPackagedAssets: true,
        SmokeCheckOnly: false,
        CentralPmsBaseUrl: "https://central-pms.test",
        TerminalId: state.TerminalIdentifier,
        SiteId: state.SiteId.ToString("D"),
        SiteGroupId: state.SiteGroupId.ToString("D"),
        PosServerId: "POS-TEST");

    private static GovernedDeviceTrustState ValidState(X509Certificate2 certificate) => new(
        GovernedDeviceTrustState.CurrentSchemaVersion,
        Guid.NewGuid(),
        "DEV-APT-TEST",
        Guid.NewGuid(),
        GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType,
        Guid.Parse("2d1dcdf8-f563-537c-8542-0bde7cc9da97"),
        Guid.Parse("a6dbadf6-68b5-5bed-a7e0-a75faee70841"),
        "PITX-APT-01",
        1,
        GovernedDeviceTrustState.MtlsCertificateCredentialType,
        new string('A', 40),
        WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);

    private static X509Certificate2 CreateClientCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ExitPass APT test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") },
            true));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private sealed class FixedCertificateStore(X509Certificate2 certificate) : IGovernedDeviceCertificateStore
    {
        public X509Certificate2 Load(string storeThumbprint) => new(certificate);
    }

    private sealed class ThrowingCertificateStore : IGovernedDeviceCertificateStore
    {
        public X509Certificate2 Load(string storeThumbprint) =>
            throw new InvalidOperationException("Certificate missing.");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
