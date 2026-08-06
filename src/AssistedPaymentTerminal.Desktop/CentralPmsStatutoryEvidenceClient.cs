using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public interface ICentralPmsStatutoryEvidenceClient
{
    Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> BootstrapAsync(
        Guid decisionCommandId,
        string? clientOperationKey,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> GetStatusAsync(
        Guid decisionCommandId,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> RevalidateAsync(
        Guid decisionCommandId,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> CreateUploadSessionAsync(
        StatutoryEvidenceUploadSessionRequest request,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> UploadAsync(
        Guid opaqueUploadSessionReference,
        Stream content,
        string contentType,
        long contentLength,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> FinalizeAsync(
        Guid opaqueUploadSessionReference,
        string? clientOperationKey,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class CentralPmsStatutoryEvidenceClient : ICentralPmsStatutoryEvidenceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseUri;
    private readonly Guid? _serviceIdentityId;

    public CentralPmsStatutoryEvidenceClient(HttpClient httpClient, string? baseUrl, string? serviceIdentityId)
    {
        _httpClient = httpClient;
        _baseUri = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed) &&
                   (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? parsed
            : null;
        _serviceIdentityId = Guid.TryParse(serviceIdentityId, out var identity) && identity != Guid.Empty
            ? identity
            : null;
    }

    public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> BootstrapAsync(
        Guid decisionCommandId,
        string? clientOperationKey,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendJsonAsync<StatutoryEvidenceChannelResponse>(
            HttpMethod.Post,
            "/v1/apt/statutory-discounts/evidence/bootstrap",
            new { statutoryDiscountDecisionCommandId = decisionCommandId, clientOperationKey },
            correlationId,
            cancellationToken);

    public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> GetStatusAsync(
        Guid decisionCommandId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendJsonAsync<StatutoryEvidenceChannelResponse>(
            HttpMethod.Get,
            $"/v1/apt/statutory-discounts/evidence/status?statutoryDiscountDecisionCommandId={decisionCommandId:D}",
            body: null,
            correlationId,
            cancellationToken);

    public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> RevalidateAsync(
        Guid decisionCommandId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendJsonAsync<StatutoryEvidenceChannelResponse>(
            HttpMethod.Post,
            "/v1/apt/statutory-discounts/evidence/revalidate",
            new { statutoryDiscountDecisionCommandId = decisionCommandId, clientOperationKey = (string?)null },
            correlationId,
            cancellationToken);

    public Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> CreateUploadSessionAsync(
        StatutoryEvidenceUploadSessionRequest request,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendJsonAsync<StatutoryEvidenceUploadSessionResponse>(
            HttpMethod.Post,
            "/v1/apt/statutory-discounts/evidence/upload-sessions",
            request,
            correlationId,
            cancellationToken);

    public async Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> UploadAsync(
        Guid opaqueUploadSessionReference,
        Stream content,
        string contentType,
        long contentLength,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"/v1/apt/statutory-discounts/evidence/upload-sessions/{opaqueUploadSessionReference:D}",
            correlationId);
        if (request is null)
        {
            return ConfigurationFailure<StatutoryEvidenceUploadSessionResponse>(correlationId);
        }

        var streamContent = new StreamContent(content, 64 * 1024);
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        streamContent.Headers.ContentLength = contentLength;
        request.Content = streamContent;
        return await SendAsync<StatutoryEvidenceUploadSessionResponse>(request, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> FinalizeAsync(
        Guid opaqueUploadSessionReference,
        string? clientOperationKey,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendJsonAsync<StatutoryEvidenceChannelResponse>(
            HttpMethod.Post,
            $"/v1/apt/statutory-discounts/evidence/upload-sessions/{opaqueUploadSessionReference:D}/finalize",
            new { clientOperationKey },
            correlationId,
            cancellationToken);

    private async Task<StatutoryEvidenceClientResult<T>> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, correlationId);
        if (request is null)
        {
            return ConfigurationFailure<T>(correlationId);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await SendAsync<T>(request, correlationId, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage? CreateRequest(HttpMethod method, string path, Guid correlationId)
    {
        if (_baseUri is null || _serviceIdentityId is null)
        {
            return null;
        }

        var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", _serviceIdentityId.Value.ToString("D"));
        return request;
    }

    private async Task<StatutoryEvidenceClientResult<T>> SendAsync<T>(
        HttpRequestMessage request,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            {
                var accessDenied = response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized;
                var unavailable = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;
                return StatutoryEvidenceClientResult<T>.Failure(
                    accessDenied ? "ACCESS_DENIED" : unavailable ? "SOURCE_UNAVAILABLE" : "REQUEST_REJECTED",
                    accessDenied
                        ? "Central PMS denied the secure APT evidence operation."
                        : unavailable
                            ? "Central PMS evidence service is temporarily unavailable."
                            : "Central PMS rejected the evidence operation.",
                    correlationId,
                    unavailable);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasClassification = document.RootElement.ValueKind == JsonValueKind.Object &&
                                    document.RootElement.TryGetProperty("classification", out _);
            var payload = hasClassification ? document.RootElement.Deserialize<T>(JsonOptions) : default;
            if (payload is not null && IsValidPayload(payload) && response.IsSuccessStatusCode)
            {
                return StatutoryEvidenceClientResult<T>.Success(payload);
            }

            if (payload is not null && IsValidPayload(payload) && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return StatutoryEvidenceClientResult<T>.Success(payload);
            }

            return StatutoryEvidenceClientResult<T>.Failure(
                response.IsSuccessStatusCode ? "MALFORMED_AUTHORITATIVE_STATE" : "REQUEST_REJECTED",
                response.IsSuccessStatusCode
                    ? "Central PMS returned an evidence response that could not be used safely."
                    : "Central PMS rejected the evidence operation.",
                correlationId,
                retryable: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatutoryEvidenceClientResult<T>.Failure(
                "SOURCE_UNAVAILABLE",
                "Central PMS evidence service did not respond in time.",
                correlationId,
                retryable: true);
        }
        catch (OperationCanceledException)
        {
            return StatutoryEvidenceClientResult<T>.Failure(
                "UPLOAD_CANCELLED",
                "The local evidence operation was cancelled. Reconcile with Central PMS before retrying.",
                correlationId,
                retryable: true);
        }
        catch (HttpRequestException)
        {
            return StatutoryEvidenceClientResult<T>.Failure(
                "SOURCE_UNAVAILABLE",
                "Central PMS evidence service is temporarily unavailable.",
                correlationId,
                retryable: true);
        }
        catch (JsonException)
        {
            return StatutoryEvidenceClientResult<T>.Failure(
                "MALFORMED_AUTHORITATIVE_STATE",
                "Central PMS returned an evidence response that could not be used safely.",
                correlationId,
                retryable: false);
        }
    }

    private static StatutoryEvidenceClientResult<T> ConfigurationFailure<T>(Guid correlationId) =>
        StatutoryEvidenceClientResult<T>.Failure(
            "APT_EVIDENCE_SERVICE_AUTH_UNAVAILABLE",
            "The secure APT evidence channel is not configured.",
            correlationId,
            retryable: false);

    private static bool IsValidPayload<T>(T payload) => payload switch
    {
        StatutoryEvidenceChannelResponse channel =>
            !string.IsNullOrWhiteSpace(channel.Classification) &&
            !string.IsNullOrWhiteSpace(channel.SourceChannel) &&
            channel.AllowedContentTypes is not null &&
            !string.IsNullOrWhiteSpace(channel.ReplacementPosture),
        StatutoryEvidenceUploadSessionResponse session =>
            !string.IsNullOrWhiteSpace(session.Classification) &&
            !string.IsNullOrWhiteSpace(session.Method) &&
            !string.IsNullOrWhiteSpace(session.AcceptedContentType),
        _ => false
    };
}

public sealed record StatutoryEvidenceClientResult<T>(
    bool Ok,
    T? Payload,
    string? ErrorCode,
    string? SafeMessage,
    Guid CorrelationId,
    bool Retryable)
{
    public static StatutoryEvidenceClientResult<T> Success(T payload) =>
        new(true, payload, null, null, Guid.Empty, false);

    public static StatutoryEvidenceClientResult<T> Failure(
        string errorCode,
        string safeMessage,
        Guid correlationId,
        bool retryable) =>
        new(false, default, errorCode, safeMessage, correlationId, retryable);
}

public sealed record StatutoryEvidenceChannelResponse(
    string Classification,
    bool Retryable,
    string? ErrorCode,
    Guid CorrelationId,
    string SourceChannel,
    bool EvidenceRequired,
    Guid? EvidenceSetReference,
    Guid? EvidenceItemReference,
    IReadOnlyList<string> AllowedContentTypes,
    long MaximumContentLengthBytes,
    int? MaximumImageWidth,
    int? MaximumImageHeight,
    long? MaximumImagePixelCount,
    string? RequiredDocumentType,
    string? RequiredItemRole,
    string? LifecycleClassification,
    string ReplacementPosture,
    bool ReadyForReview,
    bool ReadyForAptPreCash,
    string? BlockingReasonCode,
    DateTimeOffset EvaluatedAt);

public sealed record StatutoryEvidenceUploadSessionRequest(
    Guid EvidenceSetReference,
    Guid EvidenceItemReference,
    string DeclaredContentType,
    long DeclaredContentLength,
    string DeclaredChecksumSha256,
    string? ClientOperationKey);

public sealed record StatutoryEvidenceUploadSessionResponse(
    string Classification,
    bool Retryable,
    string? ErrorCode,
    Guid CorrelationId,
    Guid? OpaqueUploadSessionReference,
    string Method,
    DateTimeOffset? ExpiresAt,
    string AcceptedContentType,
    long MaximumContentLengthBytes);
