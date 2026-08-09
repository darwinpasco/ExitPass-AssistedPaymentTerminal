using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public interface ICentralPmsHumanSessionClient
{
    Task<HumanSessionClientResult> LoginAsync(string username, string password, Guid siteId, Guid correlationId, CancellationToken cancellationToken);
    Task<HumanSessionClientResult> GetAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken);
    Task<HumanSessionClientResult> ContinueAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken);
    Task<HumanSessionClientResult> ReauthenticateAsync(Guid sessionReference, string sessionToken, string password, Guid correlationId, CancellationToken cancellationToken);
    Task<HumanSessionClientResult> LogoutAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken);
}

public sealed class CentralPmsHumanSessionClient : ICentralPmsHumanSessionClient
{
    private const string AptAuthorizationScheme = "ExitPass-HumanSession";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseUri;
    private readonly Guid? _deviceServiceIdentityId;
    private readonly HumanAuthenticationTrace _trace;

    public CentralPmsHumanSessionClient(HttpClient httpClient, string? baseUrl, string? deviceServiceIdentityId, HumanAuthenticationTrace? trace = null)
    {
        _httpClient = httpClient;
        _baseUri = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;
        _deviceServiceIdentityId = Guid.TryParse(deviceServiceIdentityId, out var deviceId) && deviceId != Guid.Empty
            ? deviceId
            : null;
        _trace = trace ?? new HumanAuthenticationTrace();
    }

    public bool DeviceTrustConfigured => _baseUri is not null && _deviceServiceIdentityId.HasValue;

    public Task<HumanSessionClientResult> LoginAsync(
        string username,
        string password,
        Guid siteId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            "/v1/apt/human-sessions",
            correlationId,
            sessionToken: null,
            new { username, password, siteId, totpCode = (string?)null },
            cancellationToken);

    public Task<HumanSessionClientResult> GetAsync(
        Guid sessionReference,
        string sessionToken,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/v1/apt/human-sessions/{sessionReference:D}", correlationId, sessionToken, null, cancellationToken);

    public Task<HumanSessionClientResult> ContinueAsync(
        Guid sessionReference,
        string sessionToken,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/v1/apt/human-sessions/{sessionReference:D}/continue", correlationId, sessionToken, new { }, cancellationToken);

    public Task<HumanSessionClientResult> ReauthenticateAsync(
        Guid sessionReference,
        string sessionToken,
        string password,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            $"/v1/apt/human-sessions/{sessionReference:D}/reauthenticate",
            correlationId,
            sessionToken,
            new { password, totpCode = (string?)null },
            cancellationToken);

    public Task<HumanSessionClientResult> LogoutAsync(
        Guid sessionReference,
        string sessionToken,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/v1/apt/human-sessions/{sessionReference:D}/logout", correlationId, sessionToken, new { }, cancellationToken);

    private async Task<HumanSessionClientResult> SendAsync(
        HttpMethod method,
        string path,
        Guid correlationId,
        string? sessionToken,
        object? body,
        CancellationToken cancellationToken)
    {
        if (_baseUri is null || !_deviceServiceIdentityId.HasValue)
        {
            return HumanSessionClientResult.Failure(
                "APT_DEVICE_TRUST_UNAVAILABLE",
                "This terminal has not established the approved Central PMS device trust boundary.",
                correlationId,
                retryable: false);
        }

        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", _deviceServiceIdentityId.Value.ToString("D"));
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(AptAuthorizationScheme, sessionToken);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        try
        {
            _trace.Record(
                "central-pms.request-starting",
                method == HttpMethod.Post && path == "/v1/apt/human-sessions" ? "LOGIN" : path.EndsWith("/reauthenticate", StringComparison.Ordinal) ? "REAUTHENTICATE" : "SESSION",
                nameof(CentralPmsHumanSessionClient),
                path,
                centralPmsCorrelationId: correlationId);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            AptHumanAuthenticationResponse? payload = null;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<AptHumanAuthenticationResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Safe mapping below intentionally suppresses the malformed response body.
            }

            if (payload is not null && IsSafeResponse(payload))
            {
                _trace.Record("central-pms.request-completed", sourceMethod: nameof(CentralPmsHumanSessionClient), sourceTrigger: path, centralPmsCorrelationId: correlationId, outcome: payload.Outcome);
                return response.IsSuccessStatusCode && payload.Authenticated && payload.Session is not null
                    ? HumanSessionClientResult.Success(payload)
                    : HumanSessionClientResult.Failure(
                        payload.ErrorCode ?? payload.Outcome,
                        SafeMessage(payload.ErrorCode, response.StatusCode),
                        payload.CorrelationId == Guid.Empty ? correlationId : payload.CorrelationId,
                        payload.Retryable,
                        payload);
            }

            return HumanSessionClientResult.Failure(
                response.IsSuccessStatusCode ? "MALFORMED_HUMAN_SESSION_RESPONSE" : MapHttpError(response.StatusCode, sessionToken),
                response.IsSuccessStatusCode
                    ? "Central PMS returned a human-session response that could not be used safely."
                    : SafeMessage(response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(sessionToken) ? "SESSION_INVALID" : null, response.StatusCode),
                correlationId,
                IsRetryable(response.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HumanSessionClientResult.Failure("CENTRAL_PMS_TIMEOUT", "Central PMS did not respond before the login timeout.", correlationId, true);
        }
        catch (HttpRequestException)
        {
            return HumanSessionClientResult.Failure("CENTRAL_PMS_UNAVAILABLE", "Central PMS is unavailable. Online cashier authority cannot be established.", correlationId, true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            return HumanSessionClientResult.Failure("MALFORMED_HUMAN_SESSION_RESPONSE", "Central PMS returned a human-session response that could not be used safely.", correlationId, false);
        }
    }

    private static bool IsSafeResponse(AptHumanAuthenticationResponse response) =>
        !string.IsNullOrWhiteSpace(response.Outcome)
        && (!response.Authenticated || response.Session is not null);

    private static string MapHttpError(HttpStatusCode statusCode, string? sessionToken) => statusCode switch
    {
        HttpStatusCode.Unauthorized when !string.IsNullOrWhiteSpace(sessionToken) => "SESSION_INVALID",
        HttpStatusCode.Unauthorized => "INVALID_CREDENTIALS",
        HttpStatusCode.Forbidden => "ACCESS_DENIED",
        HttpStatusCode.NotFound => "SESSION_NOT_FOUND",
        HttpStatusCode.TooManyRequests => "AUTHENTICATION_THROTTLED",
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => "CENTRAL_PMS_UNAVAILABLE",
        _ => "HUMAN_SESSION_REQUEST_REJECTED"
    };

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private static string SafeMessage(string? errorCode, HttpStatusCode statusCode) => errorCode switch
    {
        "INVALID_CREDENTIALS" => "The username or password is incorrect.",
        "ACCOUNT_UNAVAILABLE" => "This account is not available for cashier work.",
        "AUTHENTICATION_THROTTLED" => "Login is temporarily limited. Wait before trying again.",
        "SESSION_EXPIRED" => "The cashier session expired. Sign in again to restore authority.",
        "SESSION_REVOKED" or "SESSION_INVALID" or "SESSION_NOT_FOUND" => "The cashier session is no longer valid. Sign in again.",
        "APT_DEVICE_TRUST_REQUIRED" or "AUDIENCE_DEVICE_MISMATCH" => "Central PMS rejected this terminal or session binding.",
        "FORBIDDEN" or "ACCESS_DENIED" => "This account is not authorized for this terminal and Site.",
        _ when statusCode == HttpStatusCode.Unauthorized => "The username or password is incorrect.",
        _ when statusCode == HttpStatusCode.Forbidden => "Central PMS denied this cashier operation.",
        _ when IsRetryable(statusCode) => "Central PMS is temporarily unavailable. New cash authority is blocked.",
        _ => "Central PMS rejected the cashier-session operation."
    };
}

public sealed record AptHumanAuthenticationResponse(
    string Outcome,
    bool Authenticated,
    AptHumanSessionDto? Session,
    string? AptSessionToken,
    string? ErrorCode,
    bool Retryable,
    Guid CorrelationId);

public sealed record AptHumanSessionDto(
    Guid SessionReference,
    Guid UserReference,
    string Username,
    string DisplayName,
    string Audience,
    string Assurance,
    bool PrivilegedAccount,
    bool PasswordChangeRequired,
    bool MfaRequired,
    bool MfaSatisfied,
    DateTimeOffset AuthenticatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> SiteReferences,
    IReadOnlyList<Guid> SiteGroupReferences,
    bool HasGlobalScope,
    Guid? DeviceServiceIdentityReference,
    Guid CorrelationId);

public sealed record HumanSessionClientResult(
    bool Ok,
    AptHumanAuthenticationResponse? Response,
    string? ErrorCode,
    string? SafeMessage,
    Guid CorrelationId,
    bool Retryable)
{
    public static HumanSessionClientResult Success(AptHumanAuthenticationResponse response) =>
        new(true, response, null, null, response.CorrelationId, false);

    public static HumanSessionClientResult Failure(
        string errorCode,
        string safeMessage,
        Guid correlationId,
        bool retryable,
        AptHumanAuthenticationResponse? response = null) =>
        new(false, response, errorCode, safeMessage, correlationId, retryable);
}
