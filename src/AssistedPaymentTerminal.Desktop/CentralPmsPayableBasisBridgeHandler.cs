using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public static class CentralPmsPayableBasisBridgeCommand
{
    public const string Source = "apt-central-pms-payable-basis";
    public const string Resolve = "payableBasis.resolve";
    public const string Revalidate = "payableBasis.revalidate";
    public const string StatutoryReceiptPresentation = "statutoryReceiptPresentation.get";
    public const string StatutoryReceiptPresentationPrint = "statutoryReceiptPresentation.print";
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
    private readonly bool _receiptPrintingEnabled;
    private readonly string? _receiptPrinterName;
    private readonly ReceiptPreviewPaperProfile _receiptPaperProfile;
    private readonly IReceiptPrinter _receiptPrinter;

    public CentralPmsPayableBasisBridgeHandler(
        HttpClient httpClient,
        string? baseUrl,
        ICentralPmsRequestAuthority authority,
        bool receiptPrintingEnabled = false,
        string? receiptPrinterName = null,
        string? receiptPaperWidthMm = null,
        IReceiptPrinter? receiptPrinter = null)
    {
        _httpClient = httpClient;
        _baseUri = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;
        _authority = authority;
        _receiptPrintingEnabled = receiptPrintingEnabled;
        _receiptPrinterName = receiptPrinterName?.Trim();
        _receiptPaperProfile = ReceiptPreviewPaperProfiles.Select(receiptPaperWidthMm).Profile;
        _receiptPrinter = receiptPrinter ?? new WindowsReceiptPrinter();
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
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentation =>
                TryBuildStatutoryReceiptPresentationPath(request.Body, out var receiptPath) ? receiptPath : null,
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentationPrint =>
                TryBuildStatutoryReceiptPresentationPath(request.Body, out var printPath) ? printPath : null,
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

        var isStatutoryReceiptRead = request.Command is
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentation or
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentationPrint;
        var method = isStatutoryReceiptRead
            ? HttpMethod.Get
            : HttpMethod.Post;
        using var outbound = new HttpRequestMessage(method, new Uri(_baseUri, path));
        outbound.Headers.Authorization = new AuthenticationHeaderValue(AptAuthorizationScheme, credential.SessionToken);
        outbound.Headers.TryAddWithoutValidation("X-ExitPass-Service-Identity-Id", credential.DeviceServiceIdentityId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString("D"));
        outbound.Headers.TryAddWithoutValidation("X-Site-Id", siteId.ToString("D"));
        if (method == HttpMethod.Post)
        {
            outbound.Content = JsonContent.Create(request.Body, options: JsonOptions);
        }

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

            if (request.Command == CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentationPrint)
            {
                if (!response.IsSuccessStatusCode || body is null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        command = request.Command,
                        correlationId = request.CorrelationId,
                        payload = new { statusCode = (int)response.StatusCode, body }
                    }, JsonOptions);
                }

                return await PrintStatutoryReceiptAsync(
                    request.Command,
                    request.CorrelationId,
                    body.Value,
                    cancellationToken).ConfigureAwait(false);
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

    private static bool TryBuildStatutoryReceiptPresentationPath(JsonElement body, out string? path)
    {
        path = null;
        if (body.ValueKind != JsonValueKind.Object ||
            !TryReadGuid(body, "applicationCommandId", out var applicationCommandId) ||
            !TryReadGuid(body, "decisionCommandId", out var decisionCommandId) ||
            !TryReadGuid(body, "parkingSessionId", out var parkingSessionId))
        {
            return false;
        }

        path = $"/v1/webpay/statutory-applications/{applicationCommandId:D}/receipt-presentation" +
            $"?decisionCommandId={decisionCommandId:D}&parkingSessionId={parkingSessionId:D}";
        return true;
    }

    private async Task<string> PrintStatutoryReceiptAsync(
        string command,
        string correlationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!_receiptPrintingEnabled || string.IsNullOrWhiteSpace(_receiptPrinterName))
        {
            return Failure(command, correlationId, "RECEIPT_PRINTING_DISABLED", "Sales Invoice printing is not configured on this terminal.");
        }

        if (!TryBuildZeroPayablePrintDocument(payload, out var document))
        {
            return Failure(command, correlationId, "STATUTORY_RECEIPT_NOT_AUTHORITATIVE", "The authoritative zero-payable Sales Invoice could not be validated for printing.");
        }

        var availability = await _receiptPrinter
            .CheckAvailabilityAsync(_receiptPrinterName, cancellationToken)
            .ConfigureAwait(false);
        if (!availability.Available)
        {
            return Failure(
                command,
                correlationId,
                availability.FailureClassification ?? "PRINTER_UNAVAILABLE",
                availability.SafeMessage);
        }

        var submission = await _receiptPrinter
            .SubmitAsync(document, _receiptPrinterName, cancellationToken)
            .ConfigureAwait(false);
        if (!submission.Submitted)
        {
            return Failure(
                command,
                correlationId,
                submission.FailureClassification ?? "SPOOLER_SUBMISSION_FAILED",
                submission.SafeMessage);
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            command,
            correlationId,
            payload = new
            {
                statusCode = 200,
                body = new
                {
                    submitted = true,
                    printerName = _receiptPrinterName,
                    fiscalDocumentId = document.FiscalDocumentId,
                    fiscalDocumentNumber = document.FiscalDocumentNumber,
                    safeMessage = submission.SafeMessage
                }
            }
        }, JsonOptions);
    }

    private bool TryBuildZeroPayablePrintDocument(JsonElement payload, out ReceiptPrintDocument document)
    {
        document = null!;
        if (payload.ValueKind != JsonValueKind.Object ||
            HasNonNullValue(payload, "paymentAttemptId") ||
            HasNonNullValue(payload, "paymentConfirmationId") ||
            !TryReadString(payload, "receiptAvailabilityState", out var availabilityState) ||
            !string.Equals(availabilityState, "AVAILABLE", StringComparison.Ordinal) ||
            !TryReadGuid(payload, "posFiscalDocumentId", out var fiscalDocumentId) ||
            !TryReadString(payload, "fiscalDocumentNumber", out var fiscalDocumentNumber) ||
            !payload.TryGetProperty("authoritativePresentation", out var presentation) ||
            presentation.ValueKind != JsonValueKind.Object ||
            !TryReadString(presentation, "canonicalTextAuthority", out var authority) ||
            !string.Equals(authority, "persisted_original_electronic_journal", StringComparison.Ordinal) ||
            !TryReadString(presentation, "canonicalText", out var canonicalText) ||
            !TryReadString(presentation, "canonicalTextHash", out var canonicalTextHash) ||
            !HasMatchingCanonicalTextHash(canonicalText, canonicalTextHash) ||
            !TryReadGuid(presentation, "fiscalDocumentId", out var presentationFiscalDocumentId) ||
            presentationFiscalDocumentId != fiscalDocumentId ||
            !TryReadString(presentation, "fiscalDocumentNumber", out var presentationFiscalDocumentNumber) ||
            !string.Equals(presentationFiscalDocumentNumber, fiscalDocumentNumber, StringComparison.Ordinal) ||
            !TryReadTicketNumber(presentation, out var ticketNumber))
        {
            return false;
        }

        try
        {
            document = ReceiptPrintDocumentBuilder.BuildZeroPayableStatutory(
                fiscalDocumentId,
                fiscalDocumentNumber,
                canonicalTextHash,
                _receiptPaperProfile,
                canonicalText,
                ticketNumber);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasNonNullValue(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static bool HasMatchingCanonicalTextHash(string canonicalText, string canonicalTextHash)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText));
        var expected = $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
        return string.Equals(canonicalTextHash, expected, StringComparison.Ordinal);
    }

    private static bool TryReadTicketNumber(JsonElement authoritativePresentation, out string ticketNumber)
    {
        ticketNumber = string.Empty;
        if (!authoritativePresentation.TryGetProperty("presentation", out var presentation) ||
            presentation.ValueKind != JsonValueKind.Object ||
            !presentation.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (TryReadString(row, "key", out var key) &&
                    string.Equals(key, "parkingPaymentReferences.ticketNumber", StringComparison.Ordinal) &&
                    TryReadString(row, "displayValue", out ticketNumber))
                {
                    ticketNumber = ticketNumber.Trim();
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadGuid(JsonElement body, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return body.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out value) &&
            value != Guid.Empty;
    }

    private static bool TryReadString(JsonElement body, string propertyName, out string value)
    {
        value = string.Empty;
        if (!body.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private sealed record PayableBasisBridgeRequest(
        string Source,
        string Command,
        string CorrelationId,
        string SiteId,
        JsonElement Body);
}
