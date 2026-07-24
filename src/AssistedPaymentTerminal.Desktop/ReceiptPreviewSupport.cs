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
    private static readonly HashSet<string> KnownPresentationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "registeredBusinessName",
        "registeredBusinessAddress",
        "tin",
        "vatRegTin",
        "vatRegisteredTin",
        "posSerialNumber",
        "serialNumber",
        "machineIdentificationNumber",
        "min",
        "merchantName",
        "siteName",
        "merchantAddress",
        "siteAddress",
        "address",
        "registrationNumber",
        "taxpayerIdentificationNumber",
        "documentType",
        "fiscalDocumentType",
        "fiscalDocumentNumber",
        "fiscalSeries",
        "series",
        "fiscalPrefix",
        "fiscalSuffix",
        "issuedAt",
        "issuanceTimestamp",
        "issuedDate",
        "ticketReference",
        "parkingReference",
        "transactionReference",
        "paymentReference",
        "parkingLocation",
        "terminalId",
        "terminalReference",
        "plateNumber",
        "entryTime",
        "exitTime",
        "duration",
        "durationDisplay",
        "buyerName",
        "customerName",
        "buyerTin",
        "customerTin",
        "buyerAddress",
        "customerAddress",
        "lines",
        "lineItems",
        "discounts",
        "taxes",
        "taxBreakdown",
        "totals",
        "subtotalDisplay",
        "subtotal",
        "outputVatDisplay",
        "vatableSalesDisplay",
        "vatExemptSalesDisplay",
        "zeroRatedSalesDisplay",
        "tenders",
        "payments",
        "salesInvoiceStatement",
        "legalStatement",
        "birAccreditationNumber",
        "birAccreditationIssuedDateDisplay",
        "birAccreditationDateIssued",
        "birAccreditationValidUntilDisplay",
        "birAccreditationValidUntil",
        "accreditationNumber",
        "accreditationDateIssued",
        "ptuNumber",
        "ptuIssuedDateDisplay",
        "ptuDateIssued",
        "ptuDate",
        "footer",
        "fiscalValidation",
        "qr",
        "qrMetadata"
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

            var sectionsResult = BuildSections(presentation, command);
            if (!sectionsResult.Success)
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_decode_failed",
                    "Receipt presentation could not be safely decoded. Support review is required.");
            }

            var sections = sectionsResult.Sections;
            var hasPlaceholders = ContainsPlaceholder(sections);
            if (hasPlaceholders)
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_incomplete_authoritative_payload",
                    "Receipt presentation is missing required authoritative display fields. No local placeholders were rendered.");
            }

            if (sections.Count == 0)
            {
                return ReceiptPreviewBuildResult.Fail(
                    "receipt_preview_decode_failed",
                    "Receipt presentation could not be safely decoded. Support review is required.");
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
                hasPlaceholders,
                hasPlaceholders ? "Incomplete" : "Complete",
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

    private static ReceiptPreviewSectionsResult BuildSections(
        JsonElement presentation,
        TerminalCashReceiptRetrievalCommand command)
    {
        var sections = new List<ReceiptPreviewSection>();

        foreach (var property in presentation.EnumerateObject())
        {
            if (!KnownPresentationKeys.Contains(property.Name))
            {
                return ReceiptPreviewSectionsResult.Fail();
            }
        }

        sections.Add(new ReceiptPreviewSection("Sales Invoice Title", [Actual("title", "Title", "SALES INVOICE")], []));
        sections.Add(new ReceiptPreviewSection("Registered business and statutory header", [
            ValueOrPlaceholder(presentation, ["registeredBusinessName", "merchantName"], "registeredBusinessName", "Registered business name", "[REGISTERED BUSINESS NAME]"),
            ValueOrPlaceholder(presentation, ["registeredBusinessAddress", "merchantAddress", "address"], "registeredBusinessAddress", "Registered business address", "[REGISTERED BUSINESS ADDRESS]"),
            ValueOrPlaceholder(presentation, ["tin", "vatRegTin", "vatRegisteredTin", "taxpayerIdentificationNumber"], "tin", "TIN", "[TIN]"),
            ValueOrPlaceholder(presentation, ["posSerialNumber", "serialNumber"], "posSerialNumber", "S/N", "[POS SERIAL NUMBER]"),
            ValueOrPlaceholder(presentation, ["machineIdentificationNumber", "min"], "machineIdentificationNumber", "MIN", "[MACHINE IDENTIFICATION NUMBER]")
        ], []));

        sections.Add(new ReceiptPreviewSection("SITE AND TERMINAL INFORMATION", [
            ValueOrPlaceholder(presentation, ["parkingLocation", "siteName"], "parkingLocation", "PARKING LOCATION", "[PARKING LOCATION]"),
            ValueOrPlaceholder(presentation, ["terminalId", "terminalReference"], "terminalId", "TERMINAL ID", "[TERMINAL ID]")
        ], []));

        sections.Add(new ReceiptPreviewSection("SALES INVOICE", [
            ValueOrPlaceholder(presentation, ["fiscalDocumentNumber"], "fiscalDocumentNumber", "Sales Invoice No.", "[SALES INVOICE NO.]", command.FiscalDocumentNumber),
            ValueOrPlaceholder(presentation, ["issuedAt", "issuanceTimestamp", "issuedDate"], "issuedDate", "Issued Date", "[ISSUED DATE]")
        ], []));

        sections.Add(new ReceiptPreviewSection("PARKING DETAILS", [
            ValueOrPlaceholder(presentation, ["plateNumber"], "plateNumber", "Plate Number", "[PLATE NUMBER]"),
            ValueOrPlaceholder(presentation, ["entryTime"], "entryTime", "Entry Time", "[ENTRY TIME]"),
            ValueOrPlaceholder(presentation, ["exitTime"], "exitTime", "Exit Time", "[EXIT TIME]"),
            ValueOrPlaceholder(presentation, ["durationDisplay", "duration"], "durationDisplay", "Duration", "[DURATION]")
        ], []));

        sections.Add(new ReceiptPreviewSection("ITEMS", [], BuildItemRows(presentation)));
        sections.Add(new ReceiptPreviewSection("SUBTOTAL", [
            ValueOrPlaceholder(
                ReadFirstString(presentation, ["subtotalDisplay", "subtotal"]) ?? FindTotal(presentation, "subtotal"),
                "subtotal",
                "Subtotal",
                "[SUBTOTAL]")
        ], []));
        sections.Add(new ReceiptPreviewSection("DISCOUNTS", [], BuildDiscountRows(presentation)));
        sections.Add(new ReceiptPreviewSection("VAT BREAKDOWN", [
            ValueOrPlaceholder(
                ReadFirstString(presentation, ["vatableSalesDisplay"]) ?? FindTotal(presentation, "vatable_sales"),
                "vatableSales",
                "VATable Sales",
                "[VATABLE SALES]"),
            ValueOrPlaceholder(
                ReadFirstString(presentation, ["outputVatDisplay"]) ?? FindTotal(presentation, "vat_amount", "output_vat") ?? FindTaxAmount(presentation, "VAT", "output_vat"),
                "outputVat",
                "Output VAT",
                "[OUTPUT VAT]"),
            ValueOrPlaceholder(
                ReadFirstString(presentation, ["vatExemptSalesDisplay"]) ?? FindTotal(presentation, "vat_exempt_sales"),
                "vatExemptSales",
                "VAT Exempt",
                "[VAT EXEMPT SALES]"),
            ValueOrPlaceholder(
                ReadFirstString(presentation, ["zeroRatedSalesDisplay"]) ?? FindTotal(presentation, "zero_rated_sales"),
                "zeroRatedSales",
                "Zero Rated",
                "[ZERO-RATED SALES]")
        ], []));

        var firstTender = FirstArrayObject(presentation, ["tenders", "payments"]);
        sections.Add(new ReceiptPreviewSection("PAYMENT DETAILS", [
            ValueOrPlaceholder(firstTender, ["tenderType", "paymentMethod"], "paymentMethod", "Payment method", "[PAYMENT METHOD]"),
            ProviderField(firstTender),
            ValueOrPlaceholder(firstTender, ["displayAmount", "amountDisplay", "amountTenderedDisplay"], "amountPaid", "Amount", "[AMOUNT PAID]")
        ], []));
        sections.Add(new ReceiptPreviewSection("TOTAL PAID AND CHANGE", [
            ValueOrPlaceholder(firstTender, ["displayAmount", "amountDisplay", "amountTenderedDisplay"], "totalPaid", "Total Paid", "[TOTAL PAID]"),
            ValueOrPlaceholder(firstTender, ["changeDisplay", "changeDueDisplay"], "change", "Change", "[CHANGE]")
        ], []));
        sections.Add(new ReceiptPreviewSection("Sales Invoice legal statement", [
            ValueOrPlaceholder(presentation, ["salesInvoiceStatement", "legalStatement"], "salesInvoiceStatement", "Statement", "[SALES INVOICE LEGAL STATEMENT]")
        ], []));
        sections.Add(new ReceiptPreviewSection("Customer-service footer", [
            FooterField(presentation)
        ], []));
        sections.Add(new ReceiptPreviewSection("BIR ACCREDITATION AND PTU INFORMATION", [
            ValueOrPlaceholder(presentation, ["birAccreditationNumber", "accreditationNumber"], "birAccreditationNumber", "BIR Accr. No.", "[BIR ACCREDITATION NO.]"),
            ValueOrPlaceholder(presentation, ["birAccreditationIssuedDateDisplay", "birAccreditationDateIssued", "accreditationDateIssued"], "birAccreditationIssuedDateDisplay", "Date Issued", "[BIR ACCREDITATION DATE ISSUED]"),
            ValueOrPlaceholder(presentation, ["birAccreditationValidUntilDisplay", "birAccreditationValidUntil"], "birAccreditationValidUntilDisplay", "Valid Until", "[BIR ACCREDITATION VALID UNTIL]"),
            ValueOrPlaceholder(presentation, ["ptuNumber"], "ptuNumber", "PTU No.", "[PTU NO.]"),
            ValueOrPlaceholder(presentation, ["ptuIssuedDateDisplay", "ptuDateIssued", "ptuDate"], "ptuIssuedDateDisplay", "Date Issued", "[PTU DATE ISSUED]")
        ], []));

        return ReceiptPreviewSectionsResult.Ok(sections);
    }

    private static IReadOnlyList<ReceiptPreviewRow> BuildItemRows(JsonElement presentation)
    {
        if (FirstArray(presentation, ["lines", "lineItems"]) is not { } lines)
        {
            return [new ReceiptPreviewRow([
                Placeholder("description", "Description", "[DESCRIPTION]"),
                Placeholder("quantity", "Qty", "[QTY]"),
                Placeholder("unitPrice", "Unit price", "[UNIT PRICE]"),
                Placeholder("amount", "Amount", "[AMOUNT]")
            ])];
        }

        var rows = new List<ReceiptPreviewRow>();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            rows.Add(new ReceiptPreviewRow([
                ValueOrPlaceholder(line, ["description"], "description", "Description", "[DESCRIPTION]"),
                ValueOrPlaceholder(line, ["quantity"], "quantity", "Qty", "[QTY]"),
                ValueOrPlaceholder(line, ["unitPriceDisplay", "unitPrice"], "unitPrice", "Unit price", "[UNIT PRICE]"),
                ValueOrPlaceholder(line, ["lineAmountDisplay", "amountDisplay", "displayAmount", "lineAmount"], "amount", "Amount", "[AMOUNT]")
            ]));
        }

        return rows.Count == 0
            ? [new ReceiptPreviewRow([Placeholder("description", "Description", "[DESCRIPTION]"), Placeholder("quantity", "Qty", "[QTY]"), Placeholder("unitPrice", "Unit price", "[UNIT PRICE]"), Placeholder("amount", "Amount", "[AMOUNT]")])]
            : rows;
    }

    private static IReadOnlyList<ReceiptPreviewRow> BuildDiscountRows(JsonElement presentation)
    {
        if (FirstArray(presentation, ["discounts"]) is not { } discounts)
        {
            return [new ReceiptPreviewRow([
                Placeholder("discountReason", "Discount Reason", "[DISCOUNT REASON]"),
                Placeholder("discountAmount", "Discount Amount", "[DISCOUNT AMOUNT]")
            ])];
        }

        var rows = new List<ReceiptPreviewRow>();
        foreach (var discount in discounts.EnumerateArray())
        {
            if (discount.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            rows.Add(new ReceiptPreviewRow([
                ValueOrPlaceholder(discount, ["description", "discountType", "discountLabel"], "discountReason", "Discount Reason", "[DISCOUNT REASON]"),
                ValueOrPlaceholder(discount, ["displayAmount", "amountDisplay"], "discountAmount", "Discount Amount", "[DISCOUNT AMOUNT]")
            ]));
        }

        return rows.Count == 0
            ? [new ReceiptPreviewRow([Placeholder("discountReason", "Discount Reason", "[DISCOUNT REASON]"), Placeholder("discountAmount", "Discount Amount", "[DISCOUNT AMOUNT]")])]
            : rows;
    }

    private static ReceiptPreviewField ProviderField(JsonElement? tender)
    {
        if (tender is { ValueKind: JsonValueKind.Object }
            && ReadFirstString(tender.Value, ["provider", "providerDisplay"]) is { } provider
            && !string.IsNullOrWhiteSpace(provider))
        {
            if (provider.Equals("not_applicable", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("not applicable", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return Actual("paymentProvider", "Provider", "Not applicable");
            }

            return Actual("paymentProvider", "Provider", provider);
        }

        return Placeholder("paymentProvider", "Provider", "[PAYMENT PROVIDER]");
    }

    private static ReceiptPreviewField FooterField(JsonElement presentation)
    {
        if (presentation.TryGetProperty("footer", out var footer))
        {
            if (footer.ValueKind == JsonValueKind.String)
            {
                var text = Display(footer);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Actual("footer", "Footer", text);
                }
            }

            if (footer.ValueKind == JsonValueKind.Object
                && ReadFirstString(footer, ["message", "text", "footerText"]) is { } footerText
                && !string.IsNullOrWhiteSpace(footerText))
            {
                return Actual("footer", "Footer", footerText);
            }
        }

        return Placeholder("footer", "Footer", "[SALES INVOICE FOOTER]");
    }

    private static ReceiptPreviewField ValueOrPlaceholder(
        JsonElement source,
        IReadOnlyCollection<string> keys,
        string key,
        string label,
        string placeholder,
        string? fallbackValue = null)
    {
        if (ReadFirstString(source, keys) is { } value && !string.IsNullOrWhiteSpace(value))
        {
            return Actual(key, label, value);
        }

        return string.IsNullOrWhiteSpace(fallbackValue) ? Placeholder(key, label, placeholder) : Actual(key, label, fallbackValue);
    }

    private static ReceiptPreviewField ValueOrPlaceholder(
        JsonElement? source,
        IReadOnlyCollection<string> keys,
        string key,
        string label,
        string placeholder)
    {
        return source is { ValueKind: JsonValueKind.Object }
            ? ValueOrPlaceholder(source.Value, keys, key, label, placeholder)
            : Placeholder(key, label, placeholder);
    }

    private static ReceiptPreviewField ValueOrPlaceholder(string? value, string key, string label, string placeholder) =>
        string.IsNullOrWhiteSpace(value) ? Placeholder(key, label, placeholder) : Actual(key, label, value);

    private static ReceiptPreviewField Actual(string key, string label, string value) =>
        new(key, label, value, false);

    private static ReceiptPreviewField Placeholder(string key, string label, string value) =>
        new(key, label, value, true);

    private static JsonElement? FirstArray(JsonElement source, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    private static JsonElement? FirstArrayObject(JsonElement source, IReadOnlyCollection<string> keys)
    {
        if (FirstArray(source, keys) is not { } array)
        {
            return null;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                return item;
            }
        }

        return null;
    }

    private static string? FindTotal(JsonElement presentation, params string[] totalTypes)
    {
        if (!presentation.TryGetProperty("totals", out var totals) || totals.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in totals.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var totalType = ReadFirstString(item, ["totalType", "type"]);
            if (totalType is null || !totalTypes.Any(value => value.Equals(totalType, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return ReadFirstString(item, ["displayAmount", "amountDisplay", "totalDisplay", "valueDisplay"]);
        }

        return null;
    }

    private static string? FindTaxAmount(JsonElement presentation, params string[] taxTypes)
    {
        if (FirstArray(presentation, ["taxes", "taxBreakdown"]) is not { } taxes)
        {
            return null;
        }

        foreach (var item in taxes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var taxType = ReadFirstString(item, ["taxType", "type"]);
            if (taxType is null || !taxTypes.Any(value => value.Equals(taxType, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return ReadFirstString(item, ["displayAmount", "amountDisplay", "taxAmountDisplay", "valueDisplay"]);
        }

        return null;
    }

    private static string? ReadFirstString(JsonElement source, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var display = Display(value);
            if (!string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }

        return null;
    }

    private static string Display(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(value),
            _ => ""
        };

    private static bool ContainsPlaceholder(IReadOnlyList<ReceiptPreviewSection> sections) =>
        sections.Any(section =>
            section.Fields.Any(field => field.IsPlaceholder)
            || section.Rows.Any(row => row.Fields.Any(field => field.IsPlaceholder)));

    private sealed record ReceiptPreviewSectionsResult(bool Success, IReadOnlyList<ReceiptPreviewSection> Sections)
    {
        public static ReceiptPreviewSectionsResult Ok(IReadOnlyList<ReceiptPreviewSection> sections) => new(true, sections);

        public static ReceiptPreviewSectionsResult Fail() => new(false, []);
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
    IReadOnlyList<ReceiptPreviewSection> Sections);

public sealed record ReceiptPreviewSection(
    string Title,
    IReadOnlyList<ReceiptPreviewField> Fields,
    IReadOnlyList<ReceiptPreviewRow> Rows);

public sealed record ReceiptPreviewRow(IReadOnlyList<ReceiptPreviewField> Fields);

public sealed record ReceiptPreviewField(string Key, string Label, string Value, bool IsPlaceholder);
