using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public static class CentralPmsPayableBasisBridgeCommand
{
    public const string Source = "apt-central-pms-payable-basis";
    public const string Resolve = "payableBasis.resolve";
    public const string Revalidate = "payableBasis.revalidate";
}

public sealed class CentralPmsPayableBasisBridgeHandler
{
    private const string AptAuthorizationScheme = "ExitPass-HumanSession";
    private const string ResolvePath = "/v1/terminal-cash-payments/payable-basis/resolve";
    private const string RevalidatePath = "/v1/terminal-cash-payments/payable-basis/revalidate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseUri;
    private readonly ICentralPmsRequestAuthority _authority;

    public CentralPmsPayableBasisBridgeHandler(
        HttpClient httpClient,
        string? baseUrl,
        ICentralPmsRequestAuthority authority)
    {
        _httpClient = httpClient;
        _baseUri = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;
        _authority = authority;
    }

    public async Task<string?> HandleWebMessageAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        PayableBasisBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PayableBasisBridgeRequest>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null ||
            !string.Equals(request.Source, CentralPmsPayableBasisBridgeCommand.Source, StringComparison.Ordinal))
        {
            return null;
        }

        var path = request.Command switch
        {
            CentralPmsPayableBasisBridgeCommand.Resolve => ResolvePath,
            CentralPmsPayableBasisBridgeCommand.Revalidate => RevalidatePath,
            _ => null
        };
        if (path is null ||
            _baseUri is null ||
            !Guid.TryParse(request.CorrelationId, out var correlationId) ||
            correlationId == Guid.Empty ||
            !Guid.TryParse(request.SiteId, out var siteId) ||
            siteId == Guid.Empty)
        {
            return Failure(request.Command, request.CorrelationId, "INVALID_PAYABLE_BASIS_REQUEST", "The payable-basis request is invalid.");
        }

        var credential = await _authority.GetCurrentRequestCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return Failure(request.Command, request.CorrelationId, "HUMAN_SESSION_REQUIRED", "Cashier sign-in is required before ticket or plate lookup.");
        }
        if (credential.SiteId != siteId)
        {
            return Failure(request.Command, request.CorrelationId, "FORBIDDEN_SITE", "The payable-basis request does not match this terminal Site.");
        }

        using var outbound = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
        outbound.Headers.Authorization = new AuthenticationHeaderValue(AptAuthorizationScheme, credential.SessionToken);
        outbound.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", credential.DeviceServiceIdentityId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Site-Id", siteId.ToString("D"));
        outbound.Content = JsonContent.Create(request.Body, options: JsonOptions);

        try
        {
            using var response = await _httpClient.SendAsync(
                outbound,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            JsonElement? body = null;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Existing frontend validation maps an absent body to a safe malformed response.
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                command = request.Command,
                correlationId = request.CorrelationId,
                payload = new { statusCode = (int)response.StatusCode, body }
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request.Command, request.CorrelationId, "CENTRAL_PMS_TIMEOUT", "Central PMS did not respond before the terminal timeout.");
        }
        catch (HttpRequestException)
        {
            return Failure(request.Command, request.CorrelationId, "CENTRAL_PMS_UNAVAILABLE", "Central PMS is unavailable from this terminal.");
        }
    }

    private static string Failure(string command, string correlationId, string code, string message) =>
        JsonSerializer.Serialize(new
        {
            ok = false,
            command,
            correlationId,
            error = new { code, message }
        }, JsonOptions);

    private sealed record PayableBasisBridgeRequest(
        string Source,
        string Command,
        string CorrelationId,
        string SiteId,
        JsonElement Body);
}
