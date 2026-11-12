using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public sealed record GovernedDeviceProvisioningPackage(
    Guid DeviceId,
    string DeviceReference,
    string DeviceType,
    Guid SiteId,
    Guid? SiteGroupId,
    string? TerminalIdentifier,
    string? TerminalSerialNumber,
    string ProvisioningCredential,
    DateTimeOffset ExpiresAt,
    string Purpose,
    string DisplayPolicy,
    Guid CorrelationId);

public sealed record GovernedDeviceEnrollmentRequest(
    string ProvisioningCredential,
    string DeviceReference,
    string DeviceType,
    Guid SiteId,
    Guid? SiteGroupId,
    string? TerminalIdentifier,
    string? TerminalSerialNumber);

public sealed record GovernedDeviceEnrollmentResponse(
    Guid DeviceId,
    string DeviceReference,
    Guid ServiceIdentityId,
    string DeviceType,
    Guid SiteId,
    Guid SiteGroupId,
    long CredentialVersion,
    string? RuntimeCredential,
    string RuntimeCredentialType,
    string DisplayPolicy,
    Guid CorrelationId);

public sealed record GovernedDeviceEnrollmentExpectations(
    Guid DeviceId,
    Guid ServiceIdentityId,
    Guid SiteId,
    Guid SiteGroupId,
    string TerminalIdentifier,
    string PhysicalTerminalSerialNumber,
    long ExpectedCredentialVersion,
    string PriorCertificateSha256Fingerprint,
    string? UnreadableStateBackupPath = null);

public sealed record GovernedDeviceEnrollmentOutcome(
    Guid DeviceId,
    string DeviceReference,
    Guid ServiceIdentityId,
    Guid SiteId,
    Guid SiteGroupId,
    string TerminalIdentifier,
    string TerminalSerialNumber,
    long CredentialVersion,
    string CertificateStoreThumbprint,
    string CertificateSha256Fingerprint,
    Guid CorrelationId,
    DateTimeOffset EnrolledAt,
    string? UnreadableStateBackupPath,
    string? UnreadableStateBackupSha256);

public sealed class GovernedDeviceEnrollmentException : Exception
{
    public GovernedDeviceEnrollmentException(
        string errorCode,
        int? httpStatus = null,
        bool serverEnrollmentMayHaveCommitted = false,
        Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
        HttpStatus = httpStatus;
        ServerEnrollmentMayHaveCommitted = serverEnrollmentMayHaveCommitted;
    }

    public string ErrorCode { get; }
    public int? HttpStatus { get; }
    public bool ServerEnrollmentMayHaveCommitted { get; }
}

public sealed class GovernedDeviceEnrollmentService
{
    private const string RotationPurpose = "ROTATION";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IGovernedDeviceEnrollmentCertificateStore _certificateStore;
    private readonly IGovernedDeviceTrustStateWriter _stateStore;
    private readonly Func<X509Certificate2, HttpMessageHandler> _handlerFactory;
    private readonly Func<DateTimeOffset> _utcNow;

    public GovernedDeviceEnrollmentService(
        IGovernedDeviceEnrollmentCertificateStore certificateStore,
        IGovernedDeviceTrustStateWriter stateStore,
        Func<X509Certificate2, HttpMessageHandler>? handlerFactory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _handlerFactory = handlerFactory ?? CreateHandler;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<GovernedDeviceEnrollmentOutcome> EnrollRotationAsync(
        Uri centralPmsBaseUri,
        string provisioningPackagePath,
        GovernedDeviceEnrollmentExpectations expectations,
        CancellationToken cancellationToken = default)
    {
        ValidateCentralPmsUri(centralPmsBaseUri);
        ValidateExpectations(expectations);
        var package = await ReadPackageAsync(provisioningPackagePath, cancellationToken).ConfigureAwait(false);
        ValidatePackage(package, expectations, _utcNow());

        try
        {
            _stateStore.ValidateWriteReadiness();
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            throw new GovernedDeviceEnrollmentException(
                "APT_DEVICE_TRUST_STATE_WRITE_UNAVAILABLE",
                innerException: exception);
        }

        GovernedDeviceTrustState? previousState = null;
        string? unreadableStateBackupPath = null;
        string? unreadableStateBackupSha256 = null;
        try
        {
            previousState = _stateStore.Load();
        }
        catch (InvalidOperationException exception)
        {
            if (string.IsNullOrWhiteSpace(expectations.UnreadableStateBackupPath))
                throw new GovernedDeviceEnrollmentException("APT_DEVICE_TRUST_STATE_INVALID", innerException: exception);

            (unreadableStateBackupPath, unreadableStateBackupSha256) =
                BackupUnreadableState(expectations.UnreadableStateBackupPath);
        }

        if (previousState is not null &&
            (previousState.DeviceId != expectations.DeviceId ||
             previousState.ServiceIdentityId != expectations.ServiceIdentityId))
        {
            throw new GovernedDeviceEnrollmentException("APT_EXISTING_DEVICE_IDENTITY_MISMATCH");
        }

        GeneratedGovernedDeviceCertificate? generated = null;
        var serverEnrollmentMayHaveCommitted = false;
        try
        {
            generated = _certificateStore.Create(package.DeviceReference);
            ValidateGeneratedCertificate(generated, expectations.PriorCertificateSha256Fingerprint, _utcNow());

            using var handler = _handlerFactory(generated.Certificate);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(centralPmsBaseUri, "/v1/devices/enrollment"))
            {
                Content = JsonContent.Create(new GovernedDeviceEnrollmentRequest(
                    package.ProvisioningCredential,
                    package.DeviceReference,
                    package.DeviceType,
                    package.SiteId,
                    package.SiteGroupId,
                    package.TerminalIdentifier,
                    expectations.PhysicalTerminalSerialNumber), options: JsonOptions)
            };
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", Guid.NewGuid().ToString("D"));

            serverEnrollmentMayHaveCommitted = true;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                serverEnrollmentMayHaveCommitted = false;
                var errorCode = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                throw new GovernedDeviceEnrollmentException(
                    errorCode ?? "APT_DEVICE_ENROLLMENT_REJECTED",
                    (int)response.StatusCode);
            }

            var enrollment = await response.Content.ReadFromJsonAsync<GovernedDeviceEnrollmentResponse>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new GovernedDeviceEnrollmentException(
                    "APT_DEVICE_ENROLLMENT_RESPONSE_INVALID",
                    (int)response.StatusCode,
                    serverEnrollmentMayHaveCommitted: true);
            ValidateEnrollment(package, enrollment, expectations);

            var enrolledAt = _utcNow();
            var state = new GovernedDeviceTrustState(
                GovernedDeviceTrustState.CurrentSchemaVersion,
                enrollment.DeviceId,
                enrollment.DeviceReference,
                enrollment.ServiceIdentityId,
                enrollment.DeviceType,
                enrollment.SiteId,
                enrollment.SiteGroupId,
                expectations.TerminalIdentifier.Trim(),
                enrollment.CredentialVersion,
                enrollment.RuntimeCredentialType,
                generated.StoreThumbprint,
                generated.Sha256Fingerprint,
                enrollment.CorrelationId,
                enrolledAt);

            try
            {
                _stateStore.Save(state);
                var persisted = _stateStore.Load();
                if (persisted != state)
                    throw new InvalidOperationException("The persisted governed device-trust state did not round trip exactly.");
                ValidateGeneratedCertificate(generated, expectations.PriorCertificateSha256Fingerprint, _utcNow());
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                throw new GovernedDeviceEnrollmentException(
                    "APT_DEVICE_TRUST_STATE_PERSIST_FAILED_AFTER_ENROLLMENT",
                    (int)response.StatusCode,
                    serverEnrollmentMayHaveCommitted: true,
                    innerException: exception);
            }

            TryDeleteConsumedPackage(provisioningPackagePath);
            return new GovernedDeviceEnrollmentOutcome(
                state.DeviceId,
                state.DeviceReference,
                state.ServiceIdentityId,
                state.SiteId,
                state.SiteGroupId,
                state.TerminalIdentifier,
                expectations.PhysicalTerminalSerialNumber,
                state.CredentialVersion,
                state.CertificateStoreThumbprint,
                state.CertificateSha256Fingerprint,
                state.EnrollmentCorrelationId,
                state.EnrolledAt,
                unreadableStateBackupPath,
                unreadableStateBackupSha256);
        }
        catch (GovernedDeviceEnrollmentException)
        {
            if (!serverEnrollmentMayHaveCommitted && generated is not null)
                _certificateStore.Delete(generated.StoreThumbprint);
            throw;
        }
        catch (Exception exception)
        {
            if (!serverEnrollmentMayHaveCommitted && generated is not null)
                _certificateStore.Delete(generated.StoreThumbprint);
            throw new GovernedDeviceEnrollmentException(
                serverEnrollmentMayHaveCommitted
                    ? "APT_DEVICE_ENROLLMENT_OUTCOME_UNCERTAIN"
                    : "APT_DEVICE_ENROLLMENT_FAILED",
                serverEnrollmentMayHaveCommitted: serverEnrollmentMayHaveCommitted,
                innerException: exception);
        }
        finally
        {
            generated?.Dispose();
        }
    }

    private (string Path, string Sha256) BackupUnreadableState(string requestedPath)
    {
        if (!_stateStore.Exists)
            throw new GovernedDeviceEnrollmentException("APT_DEVICE_TRUST_STATE_INVALID");

        var source = Path.GetFullPath(_stateStore.Path);
        var destination = Path.GetFullPath(requestedPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new GovernedDeviceEnrollmentException("APT_DEVICE_TRUST_BACKUP_PATH_INVALID");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Copy(source, destination, overwrite: false);
            return (destination, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GovernedDeviceEnrollmentException(
                "APT_DEVICE_TRUST_BACKUP_FAILED",
                innerException: exception);
        }
    }

    private static void ValidateExpectations(GovernedDeviceEnrollmentExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        if (expectations.DeviceId == Guid.Empty ||
            expectations.ServiceIdentityId == Guid.Empty ||
            expectations.SiteId == Guid.Empty ||
            expectations.SiteGroupId == Guid.Empty ||
            expectations.ExpectedCredentialVersion <= 0 ||
            string.IsNullOrWhiteSpace(expectations.TerminalIdentifier) ||
            string.IsNullOrWhiteSpace(expectations.PhysicalTerminalSerialNumber) ||
            expectations.PhysicalTerminalSerialNumber.Length > 128 ||
            !IsHex(expectations.PriorCertificateSha256Fingerprint, 64))
        {
            throw new GovernedDeviceEnrollmentException("APT_DEVICE_ENROLLMENT_EXPECTATIONS_INVALID");
        }
    }

    private static void ValidatePackage(
        GovernedDeviceProvisioningPackage package,
        GovernedDeviceEnrollmentExpectations expectations,
        DateTimeOffset now)
    {
        var packageSerial = package.TerminalSerialNumber?.Trim();
        if (package.DeviceId == Guid.Empty ||
            package.DeviceId != expectations.DeviceId ||
            string.IsNullOrWhiteSpace(package.ProvisioningCredential) ||
            package.ProvisioningCredential.Length < 48 ||
            string.IsNullOrWhiteSpace(package.DeviceReference) ||
            !string.Equals(package.DeviceType, GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType, StringComparison.Ordinal) ||
            package.SiteId != expectations.SiteId ||
            package.SiteGroupId != expectations.SiteGroupId ||
            !string.Equals(package.TerminalIdentifier?.Trim(), expectations.TerminalIdentifier.Trim(), StringComparison.Ordinal) ||
            (packageSerial is not null && !string.Equals(packageSerial, expectations.PhysicalTerminalSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(package.Purpose, RotationPurpose, StringComparison.Ordinal) ||
            package.ExpiresAt <= now ||
            package.CorrelationId == Guid.Empty)
        {
            throw new GovernedDeviceEnrollmentException("APT_PROVISIONING_PACKAGE_INVALID");
        }
    }

    private static void ValidateEnrollment(
        GovernedDeviceProvisioningPackage package,
        GovernedDeviceEnrollmentResponse response,
        GovernedDeviceEnrollmentExpectations expectations)
    {
        if (response.DeviceId != expectations.DeviceId ||
            response.ServiceIdentityId != expectations.ServiceIdentityId ||
            response.CorrelationId == Guid.Empty ||
            response.CredentialVersion != expectations.ExpectedCredentialVersion ||
            !string.Equals(response.DeviceReference, package.DeviceReference.Trim(), StringComparison.Ordinal) ||
            !string.Equals(response.DeviceType, GovernedDeviceTrustState.AssistedPaymentTerminalDeviceType, StringComparison.Ordinal) ||
            response.SiteId != expectations.SiteId ||
            response.SiteGroupId != expectations.SiteGroupId ||
            response.RuntimeCredential is not null ||
            !string.Equals(response.RuntimeCredentialType, GovernedDeviceTrustState.MtlsCertificateCredentialType, StringComparison.Ordinal))
        {
            throw new GovernedDeviceEnrollmentException(
                "APT_DEVICE_ENROLLMENT_RESPONSE_MISMATCH",
                serverEnrollmentMayHaveCommitted: true);
        }
    }

    private static void ValidateGeneratedCertificate(
        GeneratedGovernedDeviceCertificate generated,
        string priorFingerprint,
        DateTimeOffset now)
    {
        var certificate = generated.Certificate;
        var actualFingerprint = WindowsCurrentUserGovernedDeviceCertificateStore.Sha256(certificate);
        var actualThumbprint = WindowsCurrentUserGovernedDeviceCertificateStore.Normalize(certificate.Thumbprint);
        var hasClientAuthentication = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => string.Equals(oid.Value, ClientAuthenticationOid, StringComparison.Ordinal));
        if (!string.Equals(actualThumbprint, generated.StoreThumbprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualFingerprint, generated.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actualFingerprint, priorFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !certificate.HasPrivateKey ||
            certificate.NotBefore.ToUniversalTime() > now.UtcDateTime ||
            certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime ||
            !hasClientAuthentication)
        {
            throw new GovernedDeviceEnrollmentException("APT_GENERATED_CERTIFICATE_INVALID");
        }
    }

    private static async Task<GovernedDeviceProvisioningPackage> ReadPackageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new GovernedDeviceEnrollmentException("APT_PROVISIONING_PACKAGE_NOT_FOUND");
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<GovernedDeviceProvisioningPackage>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new GovernedDeviceEnrollmentException("APT_PROVISIONING_PACKAGE_INVALID");
        }
        catch (JsonException exception)
        {
            throw new GovernedDeviceEnrollmentException("APT_PROVISIONING_PACKAGE_INVALID", innerException: exception);
        }
    }

    private static HttpMessageHandler CreateHandler(X509Certificate2 certificate)
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        handler.ClientCertificates.Add(certificate);
        return handler;
    }

    private static void ValidateCentralPmsUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new GovernedDeviceEnrollmentException("APT_DEVICE_ENROLLMENT_REQUIRES_HTTPS");
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return payload.ValueKind == JsonValueKind.Object &&
                   payload.TryGetProperty("errorCode", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteConsumedPackage(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The result reports no secret; operators must securely remove a consumed package if cleanup fails.
        }
    }

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}
