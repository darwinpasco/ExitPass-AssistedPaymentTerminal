using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public interface ICentralPmsCashAdmissionRevalidator
{
    Task<CashAdmissionRevalidationResult> RevalidateAsync(
        PayableBasisRevalidationPayload request,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed record CashAdmissionRevalidationResult(bool Allowed, string Code, string SafeMessage)
{
    public static CashAdmissionRevalidationResult Success() =>
        new(true, "READY", "Central PMS confirmed cash admission readiness.");

    public static CashAdmissionRevalidationResult Blocked(string code, string message) =>
        new(false, code, message);
}

public sealed class CentralPmsCashAdmissionRevalidator : ICentralPmsCashAdmissionRevalidator
{
    private const string AuthorizationScheme = "ExitPass-HumanSession";
    private const string RevalidatePath = "/v1/terminal-cash-payments/payable-basis/revalidate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseUri;
    private readonly ICentralPmsRequestAuthority _authority;

    public CentralPmsCashAdmissionRevalidator(
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

    public async Task<CashAdmissionRevalidationResult> RevalidateAsync(
        PayableBasisRevalidationPayload request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (_baseUri is null ||
            !Guid.TryParse(correlationId, out var parsedCorrelationId) ||
            parsedCorrelationId == Guid.Empty ||
            request.SiteId == Guid.Empty ||
            request.SiteGroupId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ParkingSessionId) ||
            string.IsNullOrWhiteSpace(request.TariffSnapshotId) ||
            string.IsNullOrWhiteSpace(request.TerminalId) ||
            request.ExpectedAmountMinorUnits < 0 ||
            string.IsNullOrWhiteSpace(request.ExpectedCurrency))
        {
            return CashAdmissionRevalidationResult.Blocked(
                "INVALID_PAYABLE_BASIS_REQUEST",
                "The cash admission revalidation request is invalid.");
        }

        var credential = await _authority.GetCurrentRequestCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return CashAdmissionRevalidationResult.Blocked(
                "HUMAN_SESSION_REQUIRED",
                "Cashier sign-in is required before cash can be accepted.");
        }

        if (credential.SiteId != request.SiteId)
        {
            return CashAdmissionRevalidationResult.Blocked(
                "FORBIDDEN_SITE",
                "The cash admission request does not match this terminal Site.");
        }

        using var outbound = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, RevalidatePath));
        outbound.Headers.Authorization = new AuthenticationHeaderValue(AuthorizationScheme, credential.SessionToken);
        outbound.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", credential.DeviceServiceIdentityId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Correlation-Id", parsedCorrelationId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Site-Id", request.SiteId.ToString("D"));
        outbound.Content = JsonContent.Create(new
        {
            request.ParkingSessionId,
            request.TariffSnapshotId,
            siteGroupId = request.SiteGroupId,
            siteId = request.SiteId,
            request.SitePosServerId,
            request.TerminalId,
            request.VendorSystemId,
            request.TicketReference,
            request.PlateNumber,
            request.ExpectedAmountMinorUnits,
            request.ExpectedCurrency,
            request.StatutoryDiscountDecisionCommandId,
            correlationId = parsedCorrelationId
        }, options: JsonOptions);

        try
        {
            using var response = await _httpClient.SendAsync(
                outbound,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var upstreamCode = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                return CashAdmissionRevalidationResult.Blocked(
                    string.Equals(upstreamCode, "FISCAL_BUSINESS_DATE_NOT_OPEN", StringComparison.Ordinal)
                        ? "FISCAL_BUSINESS_DATE_NOT_OPEN"
                        : "FISCAL_BUSINESS_DATE_READINESS_UNAVAILABLE",
                    "Central PMS did not authorize cash acceptance.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!MatchesAuthoritativeResponse(root, request))
            {
                return CashAdmissionRevalidationResult.Blocked(
                    "PAYABLE_BASIS_REVALIDATION_FAILED",
                    "Central PMS did not confirm an unchanged cash-ready payable basis.");
            }

            return CashAdmissionRevalidationResult.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CashAdmissionRevalidationResult.Blocked(
                "FISCAL_BUSINESS_DATE_READINESS_UNAVAILABLE",
                "Central PMS cash admission readiness timed out.");
        }
        catch (HttpRequestException)
        {
            return CashAdmissionRevalidationResult.Blocked(
                "FISCAL_BUSINESS_DATE_READINESS_UNAVAILABLE",
                "Central PMS cash admission readiness is unavailable.");
        }
        catch (JsonException)
        {
            return CashAdmissionRevalidationResult.Blocked(
                "FISCAL_BUSINESS_DATE_READINESS_UNAVAILABLE",
                "Central PMS returned an invalid cash admission response.");
        }
    }

    private static bool MatchesAuthoritativeResponse(JsonElement root, PayableBasisRevalidationPayload request) =>
        ReadBoolean(root, "readyForCashAcceptance") is true &&
        string.Equals(ReadString(root, "revalidationOutcome"), "PASSED_UNCHANGED", StringComparison.Ordinal) &&
        string.Equals(ReadString(root, "parkingSessionId"), request.ParkingSessionId, StringComparison.Ordinal) &&
        string.Equals(ReadString(root, "tariffSnapshotId"), request.TariffSnapshotId, StringComparison.Ordinal) &&
        ReadInt64(root, "authoritativeAmountMinorUnits") == request.ExpectedAmountMinorUnits &&
        string.Equals(ReadString(root, "currency"), request.ExpectedCurrency, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long? ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ReadString(document.RootElement, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
