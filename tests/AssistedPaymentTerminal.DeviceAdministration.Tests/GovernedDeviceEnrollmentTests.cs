using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.DeviceAdministration.Tests;

public sealed class GovernedDeviceEnrollmentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 4, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "exitpass-apt-enrollment-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RotationEnrollment_PreservesIdentityAndWritesValidatedState()
    {
        var context = CreateContext();
        GovernedDeviceEnrollmentRequest? submitted = null;
        var handler = new DelegateHandler(async request =>
        {
            submitted = await request.Content!.ReadFromJsonAsync<GovernedDeviceEnrollmentRequest>();
            return JsonResponse(HttpStatusCode.OK, ValidResponse(context));
        });
        var service = context.CreateService(handler);

        var outcome = await service.EnrollRotationAsync(
            new Uri("https://central-pms.test"),
            context.PackagePath,
            context.Expectations);

        Assert.NotNull(submitted);
        Assert.Equal(context.Package.ProvisioningCredential, submitted!.ProvisioningCredential);
        Assert.Equal(context.Expectations.PhysicalTerminalSerialNumber, submitted.TerminalSerialNumber);
        Assert.Equal(context.Expectations.DeviceId, outcome.DeviceId);
        Assert.Equal(context.Expectations.ServiceIdentityId, outcome.ServiceIdentityId);
        Assert.Equal(context.Expectations.ExpectedCredentialVersion, outcome.CredentialVersion);
        Assert.Equal(context.Expectations.DeviceId, context.StateStore.State!.DeviceId);
        Assert.Equal(context.Expectations.ServiceIdentityId, context.StateStore.State.ServiceIdentityId);
        Assert.Equal(context.CertificateStore.GeneratedFingerprint, context.StateStore.State.CertificateSha256Fingerprint);
        Assert.False(File.Exists(context.PackagePath));
        Assert.Empty(context.CertificateStore.DeletedThumbprints);
    }

    [Fact]
    public async Task ExpiredProvisioningPackage_IsRejectedBeforeCertificateCreation()
    {
        var context = CreateContext(package => package with { ExpiresAt = Now.AddSeconds(-1) });

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_PROVISIONING_PACKAGE_INVALID", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task IdentityMismatch_IsRejectedBeforeCertificateCreation()
    {
        var context = CreateContext(package => package with { DeviceId = Guid.NewGuid() });

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_PROVISIONING_PACKAGE_INVALID", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
    }

    [Fact]
    public async Task IncompleteProvisioningPackage_IsRejectedBeforeCertificateCreation()
    {
        var context = CreateContext(package => package with { ProvisioningCredential = string.Empty });

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_PROVISIONING_PACKAGE_INVALID", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
    }

    [Fact]
    public async Task FailedDpapiReadiness_IsRejectedBeforeCertificateCreation()
    {
        var context = CreateContext();
        context.StateStore.ReadinessException = new InvalidOperationException("DPAPI unavailable.");

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_DEVICE_TRUST_STATE_WRITE_UNAVAILABLE", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task InvalidGeneratedCertificate_IsDeletedBeforeEnrollment()
    {
        var context = CreateContext();
        context.CertificateStore.CertificateFactory = () => CreateCertificate(includeClientAuthentication: false);

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_GENERATED_CERTIFICATE_INVALID", exception.ErrorCode);
        Assert.Single(context.CertificateStore.DeletedThumbprints);
        Assert.Null(context.StateStore.State);
    }

    [Fact]
    public async Task ReusedProvisioningCredential_IsRejectedWithoutReplacingTrustState()
    {
        var context = CreateContext();
        var previousState = ValidState(context, new string('B', 40), new string('C', 64));
        context.StateStore.State = previousState;
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.Conflict,
            new { errorCode = "PROVISIONING_CREDENTIAL_ALREADY_USED" })));

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(handler).EnrollRotationAsync(
                new Uri("https://central-pms.test"),
                context.PackagePath,
                context.Expectations));

        Assert.Equal("PROVISIONING_CREDENTIAL_ALREADY_USED", exception.ErrorCode);
        Assert.False(exception.ServerEnrollmentMayHaveCommitted);
        Assert.Equal(previousState, context.StateStore.State);
        Assert.Single(context.CertificateStore.DeletedThumbprints);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task ResponseIdentityMismatch_PreservesCertificateAndPackageForReconciliation()
    {
        var context = CreateContext();
        var response = ValidResponse(context) with { ServiceIdentityId = Guid.NewGuid() };
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, response)));

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(handler).EnrollRotationAsync(
                new Uri("https://central-pms.test"),
                context.PackagePath,
                context.Expectations));

        Assert.Equal("APT_DEVICE_ENROLLMENT_RESPONSE_MISMATCH", exception.ErrorCode);
        Assert.True(exception.ServerEnrollmentMayHaveCommitted);
        Assert.Empty(context.CertificateStore.DeletedThumbprints);
        Assert.Null(context.StateStore.State);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task TransportFailureAfterDispatch_PreservesCertificateForAuthoritativeReconciliation()
    {
        var context = CreateContext();
        var handler = new DelegateHandler(_ => throw new HttpRequestException("Connection ended without a response."));

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(handler).EnrollRotationAsync(
                new Uri("https://central-pms.test"),
                context.PackagePath,
                context.Expectations));

        Assert.Equal("APT_DEVICE_ENROLLMENT_OUTCOME_UNCERTAIN", exception.ErrorCode);
        Assert.True(exception.ServerEnrollmentMayHaveCommitted);
        Assert.Empty(context.CertificateStore.DeletedThumbprints);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task DpapiSaveFailureAfterEnrollment_PreservesCertificateForReconciliation()
    {
        var context = CreateContext();
        context.StateStore.SaveException = new InvalidOperationException("DPAPI protection failed.");
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, ValidResponse(context))));

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(handler).EnrollRotationAsync(
                new Uri("https://central-pms.test"),
                context.PackagePath,
                context.Expectations));

        Assert.Equal("APT_DEVICE_TRUST_STATE_PERSIST_FAILED_AFTER_ENROLLMENT", exception.ErrorCode);
        Assert.True(exception.ServerEnrollmentMayHaveCommitted);
        Assert.Empty(context.CertificateStore.DeletedThumbprints);
        Assert.True(File.Exists(context.PackagePath));
    }

    [Fact]
    public async Task CorruptTrustState_RequiresExplicitEvidenceBackup()
    {
        var context = CreateContext();
        context.StateStore.LoadException = new InvalidOperationException("Unreadable state.");

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("https://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_DEVICE_TRUST_STATE_INVALID", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
    }

    [Fact]
    public async Task CorruptTrustState_IsPreservedBeforeGovernedRotation()
    {
        var backupPath = Path.Combine(_directory, "evidence", "state.bin.corrupt");
        var context = CreateContext(
            expectationsTransform: expectations => expectations with { UnreadableStateBackupPath = backupPath });
        var corruptBytes = RandomNumberGenerator.GetBytes(128);
        File.WriteAllBytes(context.StateStore.Path, corruptBytes);
        context.StateStore.LoadException = new InvalidOperationException("Unreadable state.");
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, ValidResponse(context))));

        var outcome = await context.CreateService(handler).EnrollRotationAsync(
            new Uri("https://central-pms.test"),
            context.PackagePath,
            context.Expectations);

        Assert.Equal(corruptBytes, File.ReadAllBytes(backupPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(corruptBytes)), outcome.UnreadableStateBackupSha256);
        Assert.Equal(Path.GetFullPath(backupPath), outcome.UnreadableStateBackupPath);
    }

    [Fact]
    public async Task HttpEnrollmentAuthority_IsRejected()
    {
        var context = CreateContext();

        var exception = await Assert.ThrowsAsync<GovernedDeviceEnrollmentException>(() =>
            context.CreateService(new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run.")))
                .EnrollRotationAsync(new Uri("http://central-pms.test"), context.PackagePath, context.Expectations));

        Assert.Equal("APT_DEVICE_ENROLLMENT_REQUIRES_HTTPS", exception.ErrorCode);
        Assert.Equal(0, context.CertificateStore.CreateCount);
    }

    [Fact]
    public void DpapiState_RoundTripsWithoutPlaintextIdentifiers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = CreateContext();
        using var certificate = CreateCertificate(includeClientAuthentication: true);
        var state = ValidState(
            context,
            certificate.Thumbprint,
            WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate));
        var path = Path.Combine(_directory, "dpapi", "state.bin");
        var store = new DpapiCurrentUserGovernedDeviceTrustStateStore(path);

        store.ValidateWriteReadiness();
        store.Save(state);

        Assert.Equal(state, store.Load());
        var protectedBytes = File.ReadAllBytes(path);
        Assert.Equal(-1, protectedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(state.DeviceReference)));
        Assert.Equal(-1, protectedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(state.ServiceIdentityId.ToString("D"))));
    }

    [Fact]
    public void DpapiProtectionFailure_IsRejectedBeforeWritingTrustState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_directory, "dpapi-failure", "state.bin");
        var store = new DpapiCurrentUserGovernedDeviceTrustStateStore(path, new ThrowingProtector());

        Assert.Throws<InvalidOperationException>(() => store.ValidateWriteReadiness());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RuntimeTrust_AcceptsExactValidClientCertificate()
    {
        var context = CreateContext();
        using var certificate = CreateCertificate(includeClientAuthentication: true);
        var state = ValidState(
            context,
            certificate.Thumbprint,
            WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate));

        using var client = new GovernedCentralPmsHttpClientFactory(
            state,
            new FixedCertificateStore(certificate),
            () => Now).Create(TimeSpan.FromSeconds(1));

        Assert.NotNull(client);
    }

    [Fact]
    public void RuntimeTrust_RejectsMissingClientCertificate()
    {
        var context = CreateContext();
        var state = ValidState(context, new string('B', 40), new string('C', 64));
        var factory = new GovernedCentralPmsHttpClientFactory(
            state,
            new ThrowingCertificateStore(),
            () => Now);

        Assert.Throws<InvalidOperationException>(() => factory.Create(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RuntimeTrust_RejectsCertificateFingerprintMismatch()
    {
        var context = CreateContext();
        using var certificate = CreateCertificate(includeClientAuthentication: true);
        var state = ValidState(context, certificate.Thumbprint, new string('C', 64));
        var factory = new GovernedCentralPmsHttpClientFactory(
            state,
            new FixedCertificateStore(certificate),
            () => Now);

        Assert.Throws<InvalidOperationException>(() => factory.Create(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RuntimeTrust_RejectsExpiredClientCertificate()
    {
        var context = CreateContext();
        using var certificate = CreateCertificate(
            includeClientAuthentication: true,
            notBefore: Now.AddHours(-2),
            notAfter: Now.AddHours(-1));
        var state = ValidState(
            context,
            certificate.Thumbprint,
            WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate));
        var factory = new GovernedCentralPmsHttpClientFactory(
            state,
            new FixedCertificateStore(certificate),
            () => Now);

        Assert.Throws<InvalidOperationException>(() => factory.Create(TimeSpan.FromSeconds(1)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private EnrollmentContext CreateContext(
        Func<GovernedDeviceProvisioningPackage, GovernedDeviceProvisioningPackage>? packageTransform = null,
        Func<GovernedDeviceEnrollmentExpectations, GovernedDeviceEnrollmentExpectations>? expectationsTransform = null)
    {
        Directory.CreateDirectory(_directory);
        var deviceId = Guid.Parse("e84cf0bf-e100-4beb-9d42-787b449bf0cc");
        var serviceIdentityId = Guid.Parse("337609f0-efe7-46a0-a7e9-3fe59feca401");
        var siteId = Guid.Parse("2d1dcdf8-f563-537c-8542-0bde7cc9da97");
        var siteGroupId = Guid.Parse("a6dbadf6-68b5-5bed-a7e0-a75faee70841");
        var package = new GovernedDeviceProvisioningPackage(
            deviceId,
            "DEV-APT-DEMO",
            GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType,
            siteId,
            siteGroupId,
            "PITX-APT-01",
            null,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            Now.AddMinutes(10),
            "ROTATION",
            "ONE_TIME_SECRET",
            Guid.NewGuid());
        package = packageTransform?.Invoke(package) ?? package;
        var expectations = new GovernedDeviceEnrollmentExpectations(
            deviceId,
            serviceIdentityId,
            siteId,
            siteGroupId,
            "PITX-APT-01",
            "ACE4_2E00_653A_D952",
            2,
            new string('A', 64));
        expectations = expectationsTransform?.Invoke(expectations) ?? expectations;
        var packagePath = Path.Combine(_directory, $"package-{Guid.NewGuid():N}.json");
        File.WriteAllText(packagePath, JsonSerializer.Serialize(package, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var stateStore = new MemoryStateStore(Path.Combine(_directory, $"state-{Guid.NewGuid():N}.bin"));
        return new EnrollmentContext(package, expectations, packagePath, stateStore, new MemoryCertificateStore());
    }

    private static GovernedDeviceEnrollmentResponse ValidResponse(EnrollmentContext context) => new(
        context.Expectations.DeviceId,
        context.Package.DeviceReference,
        context.Expectations.ServiceIdentityId,
        GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType,
        context.Expectations.SiteId,
        context.Expectations.SiteGroupId,
        context.Expectations.ExpectedCredentialVersion,
        null,
        GovernedDeviceTrustState.MtlsCertificateCredentialType,
        "CERTIFICATE_REFERENCE",
        Guid.NewGuid());

    private static GovernedDeviceTrustState ValidState(
        EnrollmentContext context,
        string thumbprint,
        string fingerprint) => new(
        GovernedDeviceTrustState.CurrentSchemaVersion,
        context.Expectations.DeviceId,
        context.Package.DeviceReference,
        context.Expectations.ServiceIdentityId,
        GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType,
        context.Expectations.SiteId,
        context.Expectations.SiteGroupId,
        context.Expectations.TerminalIdentifier,
        1,
        GovernedDeviceTrustState.MtlsCertificateCredentialType,
        thumbprint,
        fingerprint,
        Guid.NewGuid(),
        Now.AddDays(-1));

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T payload) => new(status)
    {
        Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private static X509Certificate2 CreateCertificate(
        bool includeClientAuthentication,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ExitPass APT test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (includeClientAuthentication)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2") },
                true));
        }
        return request.CreateSelfSigned(notBefore ?? Now.AddMinutes(-5), notAfter ?? Now.AddHours(1));
    }

    private sealed record EnrollmentContext(
        GovernedDeviceProvisioningPackage Package,
        GovernedDeviceEnrollmentExpectations Expectations,
        string PackagePath,
        MemoryStateStore StateStore,
        MemoryCertificateStore CertificateStore)
    {
        public GovernedDeviceEnrollmentService CreateService(HttpMessageHandler handler) => new(
            CertificateStore,
            StateStore,
            _ => handler,
            () => Now);
    }

    private sealed class MemoryStateStore(string path) : IGovernedDeviceTrustStateWriter
    {
        public string Path { get; } = path;
        public bool Exists => File.Exists(Path);
        public GovernedDeviceTrustState? State { get; set; }
        public Exception? ReadinessException { get; set; }
        public Exception? LoadException { get; set; }
        public Exception? SaveException { get; set; }

        public void ValidateWriteReadiness()
        {
            if (ReadinessException is not null) throw ReadinessException;
        }

        public GovernedDeviceTrustState? Load()
        {
            if (LoadException is not null) throw LoadException;
            return State;
        }

        public void Save(GovernedDeviceTrustState state)
        {
            if (SaveException is not null) throw SaveException;
            State = state;
            LoadException = null;
        }
    }

    private sealed class MemoryCertificateStore : IGovernedDeviceEnrollmentCertificateStore
    {
        public Func<X509Certificate2> CertificateFactory { get; set; } = () => CreateCertificate(includeClientAuthentication: true);
        public int CreateCount { get; private set; }
        public List<string> DeletedThumbprints { get; } = [];
        public string? GeneratedFingerprint { get; private set; }

        public GeneratedGovernedDeviceCertificate Create(string deviceReference)
        {
            CreateCount++;
            var certificate = CertificateFactory();
            GeneratedFingerprint = WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate);
            return new GeneratedGovernedDeviceCertificate(
                certificate.Thumbprint,
                GeneratedFingerprint,
                certificate);
        }

        public void Delete(string storeThumbprint) => DeletedThumbprints.Add(storeThumbprint);

        public X509Certificate2 Load(string storeThumbprint) =>
            throw new NotSupportedException("The enrollment service does not load through this fake.");
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

    private sealed class ThrowingProtector : IGovernedDeviceTrustProtector
    {
        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new CryptographicException("Protection failed.");

        public byte[] Unprotect(byte[] protectedPayload, byte[] entropy) =>
            throw new Xunit.Sdk.XunitException("Unprotect must not run after protection fails.");
    }
}
