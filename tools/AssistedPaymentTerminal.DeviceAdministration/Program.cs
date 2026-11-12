using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;

return await GovernedDeviceAdministrationProgram.RunAsync(args).ConfigureAwait(false);

internal static class GovernedDeviceAdministrationProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());
            return command switch
            {
                "enroll-rotation" => await EnrollRotationAsync(options).ConfigureAwait(false),
                "inspect" => Inspect(options),
                "probe" => await ProbeAsync(options, expectRejection: false).ConfigureAwait(false),
                "probe-isolation" => await ProbeAsync(options, expectRejection: true).ConfigureAwait(false),
                _ => Usage()
            };
        }
        catch (GovernedDeviceEnrollmentException exception)
        {
            WriteSafeResult(new
            {
                ok = false,
                exception.ErrorCode,
                exception.HttpStatus,
                exception.ServerEnrollmentMayHaveCommitted
            });
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            WriteSafeResult(new { ok = false, errorCode = "APT_DEVICE_ADMINISTRATION_FAILED", errorType = exception.GetType().Name });
            return 2;
        }
    }

    private static async Task<int> EnrollRotationAsync(IReadOnlyDictionary<string, string> options)
    {
        RequireOnly(options,
            "central-pms-base-url",
            "package",
            "state",
            "result",
            "expected-device-id",
            "expected-service-identity-id",
            "expected-site-id",
            "expected-site-group-id",
            "expected-terminal-id",
            "physical-terminal-serial",
            "expected-credential-version",
            "prior-certificate-sha256",
            "unreadable-state-backup");
        var packagePath = RequiredPath(options, "package");
        var statePath = RequiredPath(options, "state");
        var outputPath = RequiredPath(options, "result");
        var expectations = new GovernedDeviceEnrollmentExpectations(
            RequiredGuid(options, "expected-device-id"),
            RequiredGuid(options, "expected-service-identity-id"),
            RequiredGuid(options, "expected-site-id"),
            RequiredGuid(options, "expected-site-group-id"),
            Required(options, "expected-terminal-id"),
            Required(options, "physical-terminal-serial"),
            RequiredLong(options, "expected-credential-version"),
            RequiredHex(options, "prior-certificate-sha256", 64),
            OptionalPath(options, "unreadable-state-backup"));
        var service = new GovernedDeviceEnrollmentService(
            new WindowsCurrentUserGovernedDeviceCertificateStore(),
            new DpapiCurrentUserGovernedDeviceTrustStateStore(statePath));
        var outcome = await service.EnrollRotationAsync(
            RequiredHttpsUri(options, "central-pms-base-url"),
            packagePath,
            expectations).ConfigureAwait(false);

        WriteResult(outputPath, new
        {
            ok = true,
            outcome.DeviceId,
            outcome.DeviceReference,
            outcome.ServiceIdentityId,
            outcome.SiteId,
            outcome.SiteGroupId,
            outcome.TerminalIdentifier,
            outcome.TerminalSerialNumber,
            outcome.CredentialVersion,
            outcome.CertificateStoreThumbprint,
            outcome.CertificateSha256Fingerprint,
            outcome.CorrelationId,
            outcome.EnrolledAt,
            outcome.UnreadableStateBackupPath,
            outcome.UnreadableStateBackupSha256,
            provisioningPackageDeleted = !File.Exists(packagePath)
        });
        return 0;
    }

    private static int Inspect(IReadOnlyDictionary<string, string> options)
    {
        RequireOnly(options, "state", "result");
        var state = LoadValidatedState(options);
        var certificateStore = new WindowsCurrentUserGovernedDeviceCertificateStore();
        using var validatedClient = new GovernedCentralPmsHttpClientFactory(state, certificateStore)
            .Create(TimeSpan.FromSeconds(15));
        using var certificate = certificateStore.Load(state.CertificateStoreThumbprint);
        WriteResult(RequiredPath(options, "result"), SafeState(state, certificate));
        return 0;
    }

    private static async Task<int> ProbeAsync(
        IReadOnlyDictionary<string, string> options,
        bool expectRejection)
    {
        RequireOnly(options, "central-pms-base-url", "state", "result");
        var state = LoadValidatedState(options);
        var baseUri = RequiredHttpsUri(options, "central-pms-base-url");
        using var client = new GovernedCentralPmsHttpClientFactory(
            state,
            new WindowsCurrentUserGovernedDeviceCertificateStore()).Create(TimeSpan.FromSeconds(15));

        using (var readiness = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        using (var readinessResponse = await readiness.GetAsync(new Uri(baseUri, "/health/ready")).ConfigureAwait(false))
        {
            if (!readinessResponse.IsSuccessStatusCode)
                throw new InvalidOperationException("Central PMS HTTPS readiness did not pass normal certificate validation.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri, $"/v1/apt/human-sessions/{Guid.NewGuid():D}"));
        request.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", state.ServiceIdentityId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", Guid.NewGuid().ToString("D"));
        using var authorityResponse = await client.SendAsync(request).ConfigureAwait(false);
        var errorCode = await ReadErrorCodeAsync(authorityResponse).ConfigureAwait(false);

        var accepted = authorityResponse.StatusCode == HttpStatusCode.NotFound &&
                       string.Equals(errorCode, "SESSION_NOT_FOUND", StringComparison.Ordinal);
        var rejected = authorityResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        if ((!expectRejection && !accepted) || (expectRejection && !rejected))
            throw new InvalidOperationException(expectRejection
                ? "The enrolled Demo credential was not conclusively rejected by the isolation authority."
                : "The enrolled credential was not accepted by the intended Central PMS authority.");

        WriteResult(RequiredPath(options, "result"), new
        {
            ok = true,
            mode = expectRejection ? "isolation-rejected" : "accepted",
            state.DeviceId,
            state.ServiceIdentityId,
            state.TerminalIdentifier,
            httpStatus = (int)authorityResponse.StatusCode,
            errorCode
        });
        return 0;
    }

    private static GovernedDeviceTrustState LoadValidatedState(IReadOnlyDictionary<string, string> options)
    {
        var state = new DpapiCurrentUserGovernedDeviceTrustStateStore(RequiredPath(options, "state")).Load()
            ?? throw new InvalidOperationException("The governed APT device-trust state does not exist.");
        state.Validate();
        return state;
    }

    private static object SafeState(GovernedDeviceTrustState state, X509Certificate2 certificate) => new
    {
        ok = true,
        state.SchemaVersion,
        state.DeviceId,
        state.DeviceReference,
        state.ServiceIdentityId,
        state.DeviceType,
        state.SiteId,
        state.SiteGroupId,
        state.TerminalIdentifier,
        state.CredentialVersion,
        state.RuntimeCredentialType,
        state.CertificateStoreThumbprint,
        state.CertificateSha256Fingerprint,
        state.EnrollmentCorrelationId,
        state.EnrolledAt,
        certificateHasPrivateKey = certificate.HasPrivateKey,
        certificateNotBefore = certificate.NotBefore.ToUniversalTime(),
        certificateNotAfter = certificate.NotAfter.ToUniversalTime()
    };

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Options must use '--name value' pairs.");
            var name = args[index][2..];
            if (name.Length == 0 || !options.TryAdd(name, args[index + 1]))
                throw new ArgumentException("Option names must be nonempty and unique.");
        }
        return options;
    }

    private static Uri RequiredHttpsUri(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Required(options, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"--{name} must be an absolute HTTPS URI.");
        return uri;
    }

    private static void RequireOnly(
        IReadOnlyDictionary<string, string> options,
        params string[] allowedNames)
    {
        var allowed = allowedNames.ToHashSet(StringComparer.Ordinal);
        var unknown = options.Keys.FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null)
            throw new ArgumentException($"--{unknown} is not valid for this command.");
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"--{name} is required.");

    private static Guid RequiredGuid(IReadOnlyDictionary<string, string> options, string name) =>
        Guid.TryParse(Required(options, name), out var value) && value != Guid.Empty
            ? value
            : throw new FormatException($"--{name} must be a nonempty GUID.");

    private static long RequiredLong(IReadOnlyDictionary<string, string> options, string name) =>
        long.TryParse(Required(options, name), out var value) && value > 0
            ? value
            : throw new FormatException($"--{name} must be a positive integer.");

    private static string RequiredHex(
        IReadOnlyDictionary<string, string> options,
        string name,
        int length)
    {
        var value = Required(options, name);
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException($"--{name} must be exactly {length} hexadecimal characters.");
        return value.ToLowerInvariant();
    }

    private static string RequiredPath(IReadOnlyDictionary<string, string> options, string name) =>
        Path.GetFullPath(Required(options, name));

    private static string? OptionalPath(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : null;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
            return payload.ValueKind == JsonValueKind.Object &&
                   payload.TryGetProperty("errorCode", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteResult(string path, object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, result, JsonOptions);
    }

    private static void WriteSafeResult(object result) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(result, JsonOptions));

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: enroll-rotation|inspect|probe|probe-isolation with governed --name value options. Provisioning credentials are accepted only inside the protected package file.");
        return 64;
    }
}
