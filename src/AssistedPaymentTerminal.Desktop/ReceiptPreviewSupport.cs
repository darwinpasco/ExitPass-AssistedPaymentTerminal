using System.Globalization;
using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using QRCoder;

namespace AssistedPaymentTerminal.Desktop;

public static class ReceiptPreviewContract
{
    public const string PresentationVersion = "digital-sales-invoice-presentation-json-v1";
    public const string PersistedOriginalPresentationVersion = "digital-sales-invoice-presentation-json-v2-persisted-original";
    public const string TemplateVersion = "digital-sales-invoice-json-v1";
    public const string ContentType = "application/json";

    public static bool SupportsPresentationVersion(string? value) =>
        string.Equals(value, PresentationVersion, StringComparison.Ordinal)
        || string.Equals(value, PersistedOriginalPresentationVersion, StringComparison.Ordinal);
}

public sealed record ReceiptPreviewPaperProfile(
    string Id,
    int PaperWidthMm,
    int PrintableWidthMm,
    int InnerMarginMm,
    decimal FontScale,
    string MonetaryColumnBehavior,
    string MetadataDensity);

public sealed record ReceiptPreviewPaperSelection(
    ReceiptPreviewPaperProfile Profile,
    string? Warning);

public static class ReceiptPreviewPaperProfiles
{
    private static readonly Dictionary<int, ReceiptPreviewPaperProfile> Profiles = new()
    {
        [57] = new("receipt-paper-57", 57, 48, 4, 0.92m, "compact-right-aligned", "compact"),
        [58] = new("receipt-paper-58", 58, 49, 4, 0.94m, "compact-right-aligned", "compact"),
        [80] = new("receipt-paper-80", 80, 70, 5, 1.00m, "wide-right-aligned", "standard")
    };

    public static ReceiptPreviewPaperSelection Select(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new ReceiptPreviewPaperSelection(Profiles[57], null);
        }

        if (int.TryParse(rawValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && Profiles.TryGetValue(width, out var profile))
        {
            return new ReceiptPreviewPaperSelection(profile, null);
        }

        return new ReceiptPreviewPaperSelection(
            Profiles[57],
            $"Unsupported APT_RECEIPT_PAPER_WIDTH_MM value '{rawValue}'. Falling back to 57 mm.");
    }
}

public sealed record ReceiptPreviewBuildResult(
    bool Success,
    ReceiptPreviewDocument? Document,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ReceiptPreviewBuildResult Ok(ReceiptPreviewDocument document) =>
        new(true, document, null, null);

    public static ReceiptPreviewBuildResult Fail(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}

public static class ReceiptPreviewBuilder
{
    public static ReceiptPreviewBuildResult Build(
        TerminalCashReceiptRetrievalCommand command,
        ReceiptPreviewPaperProfile paperProfile)
    {
        if (command.Status is not (TerminalCashReceiptRetrievalStatus.Available or TerminalCashReceiptRetrievalStatus.Voided))
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_not_available",
                "Receipt preview is available only after an authoritative presentation is available.");
        }

        if (string.IsNullOrWhiteSpace(command.AuthoritativePresentationJson))
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_missing_payload",
                "Receipt preview cannot start because the authoritative presentation payload is missing.");
        }

        if (string.IsNullOrWhiteSpace(command.AuthoritativePayloadHash))
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_missing_payload_hash",
                "Receipt preview cannot start because the authoritative payload hash is missing.");
        }

        var computedHash = TerminalCashReceiptPayloadFactory.ComputeHash(command.AuthoritativePresentationJson);
        if (!string.Equals(computedHash, command.AuthoritativePayloadHash, StringComparison.Ordinal))
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_integrity_failed",
                "Receipt payload integrity check failed. Support review is required.");
        }

        if (!ReceiptPreviewContract.SupportsPresentationVersion(command.PresentationVersion)
            || !string.Equals(command.TemplateVersion, ReceiptPreviewContract.TemplateVersion, StringComparison.Ordinal)
            || !string.Equals(command.ContentType, ReceiptPreviewContract.ContentType, StringComparison.Ordinal))
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_unsupported_version",
                "Unsupported receipt presentation version. An application upgrade or support review is required.");
        }

        try
        {
            using var json = JsonDocument.Parse(command.AuthoritativePresentationJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("presentation", out var presentation)
                || presentation.ValueKind != JsonValueKind.Object)
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_decode_failed",
                    "Receipt presentation could not be safely decoded. Support review is required.");
            }

            if (!ReadString(json.RootElement, "canonicalText", out var canonicalPrintableText)
                || canonicalPrintableText.Contains('\0'))
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "The POS-owned canonical printable Sales Invoice is incomplete.");
            }

            if (!TryReadTicketNumber(presentation, out var ticketNumber))
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "The POS-owned canonical Sales Invoice ticket reference is incomplete.");
            }

            var aptTicketQrCodeDataUrl = AptTicketQrCode.CreateDataUrl(ticketNumber);

            var document = new ReceiptPreviewDocument(
                command.TerminalCashTenderId,
                command.LocalReceiptRetrievalId(),
                command.FiscalIssuanceReferenceId,
                command.PosFiscalDocumentId,
                command.FiscalDocumentNumber,
                command.FiscalDocumentStatus,
                command.ReceiptAvailabilityState,
                command.PresentationVersion,
                command.TemplateVersion,
                command.ContentType,
                command.AuthoritativePayloadHash,
                command.SemanticRequestHash,
                command.SemanticRequestHashVersion,
                command.SemanticRequestHashStatus,
                command.RetrievedAt,
                command.RetrievalCorrelationId,
                command.LastCentralPmsCorrelationId,
                command.Status == TerminalCashReceiptRetrievalStatus.Voided,
                command.VoidStatus,
                command.VoidReasonCode,
                command.VoidedAt,
                paperProfile,
                false,
                "Complete",
                canonicalPrintableText,
                ticketNumber,
                aptTicketQrCodeDataUrl);

            return ReceiptPreviewBuildResult.Ok(document);
        }
        catch (JsonException)
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_decode_failed",
                "Receipt presentation could not be safely decoded. Support review is required.");
        }
    }

    private static bool ReadString(JsonElement source, string propertyName, out string value)
    {
        value = string.Empty;
        if (!source.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private static bool TryReadTicketNumber(JsonElement presentation, out string ticketNumber)
    {
        ticketNumber = string.Empty;
        if (!presentation.TryGetProperty("sections", out var sections)
            || sections.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (ReadString(row, "key", out var key)
                    && string.Equals(key, "parkingPaymentReferences.ticketNumber", StringComparison.Ordinal)
                    && ReadString(row, "displayValue", out ticketNumber))
                {
                    ticketNumber = ticketNumber.Trim();
                    return true;
                }
            }
        }

        return false;
    }
}

internal static class AptTicketQrCode
{
    private const string DataUrlPrefix = "data:image/png;base64,";

    public static string CreateDataUrl(string ticketNumber)
    {
        if (string.IsNullOrWhiteSpace(ticketNumber))
            throw new ArgumentException("A ticket number is required for the APT QR code.", nameof(ticketNumber));

        using var data = QRCodeGenerator.GenerateQrCode(ticketNumber.Trim(), QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12, drawQuietZones: true);
        return DataUrlPrefix + Convert.ToBase64String(png);
    }

    public static byte[] DecodeDataUrl(string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The APT ticket QR code is invalid.");
        return Convert.FromBase64String(dataUrl[DataUrlPrefix.Length..]);
    }
}

internal static class ReceiptRetrievalCommandExtensions
{
    public static Guid LocalReceiptRetrievalId(this TerminalCashReceiptRetrievalCommand command) => command.Id;
}

public sealed record ReceiptPreviewDocument(
    Guid TerminalCashTenderId,
    Guid LocalReceiptRetrievalId,
    Guid FiscalIssuanceReferenceId,
    Guid PosFiscalDocumentId,
    string? FiscalDocumentNumber,
    string? FiscalDocumentStatus,
    string? ReceiptAvailabilityState,
    string? PresentationVersion,
    string? TemplateVersion,
    string? ContentType,
    string? AuthoritativePayloadHash,
    string? SemanticRequestHash,
    string? SemanticRequestHashVersion,
    string? SemanticRequestHashStatus,
    DateTimeOffset? RetrievedAt,
    string RetrievalCorrelationId,
    string? CentralPmsCorrelationId,
    bool Voided,
    string? VoidStatus,
    string? VoidReasonCode,
    DateTimeOffset? VoidedAt,
    ReceiptPreviewPaperProfile PaperProfile,
    bool HasPlaceholders,
    string ConfigurationCompleteness,
    string CanonicalPrintableText,
    string AptTicketNumber,
    string AptTicketQrCodeDataUrl);
