using System.Globalization;
using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop;

public static class ReceiptPreviewContract
{
    public const string PresentationVersion = "digital-sales-invoice-presentation-json-v1";
    public const string TemplateVersion = "digital-sales-invoice-json-v1";
    public const string ContentType = "application/json";
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
    private static readonly HashSet<string> CashierSections = new(StringComparer.Ordinal)
    {
        "header",
        "salesInvoiceHeaderSnapshot",
        "documentIdentity",
        "fiscalNumbering",
        "parkingPaymentReferences",
        "customerInformation",
        "lineItems",
        "discounts",
        "taxes",
        "vatBreakdown",
        "appliedStatutoryFiscalFacts",
        "tenders",
        "totals",
        "footerDisclaimers"
    };

    private static readonly HashSet<string> RequiredCashierFacts = new(StringComparer.Ordinal)
    {
        "header.documentTitle",
        "salesInvoiceHeaderSnapshot.registeredBusinessName",
        "salesInvoiceHeaderSnapshot.registeredBusinessAddress",
        "salesInvoiceHeaderSnapshot.tin",
        "salesInvoiceHeaderSnapshot.posSerialNumber",
        "salesInvoiceHeaderSnapshot.machineIdentificationNumber",
        "salesInvoiceHeaderSnapshot.parkingLocationDisplay",
        "salesInvoiceHeaderSnapshot.supplierDeveloperRegisteredName",
        "salesInvoiceHeaderSnapshot.supplierDeveloperAddress",
        "salesInvoiceHeaderSnapshot.supplierDeveloperTin",
        "salesInvoiceHeaderSnapshot.birAccreditationNumber",
        "salesInvoiceHeaderSnapshot.birAccreditationIssuedDate",
        "salesInvoiceHeaderSnapshot.birAccreditationValidUntil",
        "salesInvoiceHeaderSnapshot.ptuNumber",
        "salesInvoiceHeaderSnapshot.ptuIssuedDate",
        "salesInvoiceHeaderSnapshot.salesInvoiceLegalStatement",
        "fiscalNumbering.fiscalDocumentNumber",
        "parkingPaymentReferences.branchOrSite",
        "parkingPaymentReferences.ticketNumber",
        "parkingPaymentReferences.plateNumber",
        "parkingPaymentReferences.entryTime",
        "parkingPaymentReferences.paymentTime",
        "parkingPaymentReferences.parkingDuration",
        "parkingPaymentReferences.paymentMethod",
        "totals.vatableSales",
        "totals.vatAmount",
        "totals.vatExemptSales",
        "totals.zeroRatedSales"
    };

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

        if (!string.Equals(command.PresentationVersion, ReceiptPreviewContract.PresentationVersion, StringComparison.Ordinal)
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

            var sections = ReadCanonicalSections(presentation);
            if (sections is null || sections.Count == 0)
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "The POS-owned canonical Sales Invoice presentation is incomplete.");
            }

            if (!ReadString(json.RootElement, "canonicalText", out var canonicalPrintableText)
                || canonicalPrintableText.Contains('\0'))
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "The POS-owned canonical printable Sales Invoice is incomplete.");
            }

            var availableKeys = sections
                .SelectMany(section => section.Rows)
                .Select(row => row.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (!RequiredCashierFacts.IsSubsetOf(availableKeys))
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "The POS-owned canonical Sales Invoice presentation is incomplete.");
            }

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
                sections);

            return ReceiptPreviewBuildResult.Ok(document);
        }
        catch (JsonException)
        {
            return ReceiptPreviewBuildResult.Fail(
                "receipt_preview_decode_failed",
                "Receipt presentation could not be safely decoded. Support review is required.");
        }
    }

    private static IReadOnlyList<ReceiptPreviewSection>? ReadCanonicalSections(JsonElement presentation)
    {
        if (!presentation.TryGetProperty("sections", out var sectionsElement)
            || sectionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sections = new List<(int SortOrder, ReceiptPreviewSection Section)>();
        foreach (var sectionElement in sectionsElement.EnumerateArray())
        {
            if (sectionElement.ValueKind != JsonValueKind.Object
                || !ReadString(sectionElement, "name", out var name)
                || !ReadString(sectionElement, "label", out var label)
                || !CashierSections.Contains(name)
                || !sectionElement.TryGetProperty("rows", out var rowsElement)
                || rowsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var rows = new List<ReceiptPreviewField>();
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                if (!TryReadCashierRow(rowElement, out var row))
                {
                    continue;
                }

                rows.Add(row);
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var sortOrder = sectionElement.TryGetProperty("sortOrder", out var sortOrderElement)
                && sortOrderElement.TryGetInt32(out var parsedSortOrder)
                    ? parsedSortOrder
                    : int.MaxValue;
            sections.Add((sortOrder, new ReceiptPreviewSection(name, label, rows)));
        }

        return sections
            .OrderBy(section => section.SortOrder)
            .Select(section => section.Section)
            .ToArray();
    }

    private static bool TryReadCashierRow(JsonElement rowElement, out ReceiptPreviewField row)
    {
        row = default!;
        if (rowElement.ValueKind != JsonValueKind.Object
            || !ReadString(rowElement, "key", out var key)
            || !ReadString(rowElement, "label", out var label)
            || !ReadString(rowElement, "posture", out var posture)
            || posture is "placeholder" or "deferred" or "not_available"
            || IsInternalKey(key)
            || !rowElement.TryGetProperty("displayValue", out var displayValueElement)
            || displayValueElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(displayValueElement.GetString()))
        {
            return false;
        }

        row = new ReceiptPreviewField(key, label, displayValueElement.GetString()!, posture);
        return true;
    }

    private static bool IsInternalKey(string key)
    {
        var fieldName = key[(key.LastIndexOf('.') + 1)..];
        return fieldName.EndsWith("Id", StringComparison.Ordinal)
            || fieldName.EndsWith("Ref", StringComparison.Ordinal)
            || fieldName.Contains("Hash", StringComparison.Ordinal)
            || fieldName.EndsWith("Version", StringComparison.Ordinal);
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
    IReadOnlyList<ReceiptPreviewSection> Sections);

public sealed record ReceiptPreviewSection(
    string Name,
    string Label,
    IReadOnlyList<ReceiptPreviewField> Rows);

public sealed record ReceiptPreviewField(
    string Key,
    string Label,
    string DisplayValue,
    string Posture);
