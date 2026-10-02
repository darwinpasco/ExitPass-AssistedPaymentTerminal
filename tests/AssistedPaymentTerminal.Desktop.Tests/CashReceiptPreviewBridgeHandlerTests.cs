using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CashReceiptPreviewBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UnsupportedPreviewCommandIsRejected()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendAsync(handler, "centralPmsCashReceipt.previewAndPrint", "corr-unsupported", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported_command", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PreviewCommandBlocksIncompleteAuthoritativePayloadWithoutPlaceholders()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var client = new ScriptedCentralPmsReceiptClient();
        var handler = database.CreateHandler(client, receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-preview");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_incomplete_authoritative_payload", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        var serialized = response.RootElement.GetRawText();
        Assert.DoesNotContain("[REGISTERED BUSINESS NAME]", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("[TIN]", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("authoritativePresentationJson", serialized, StringComparison.Ordinal);
        Assert.Empty(client.Operations);
        Assert.Equal(1, await database.CountReceiptCommandsAsync(receipt.TerminalCashTenderId));
    }

    [Fact]
    public async Task ActualAuthoritativeValuesReplacePlaceholdersAndCompleteConfiguration()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-complete");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var preview = response.RootElement.GetProperty("payload").GetProperty("preview");
        Assert.False(preview.GetProperty("hasPlaceholders").GetBoolean());
        Assert.Equal("Complete", preview.GetProperty("configurationCompleteness").GetString());
        Assert.Equal(receipt.AuthoritativePayloadHash, preview.GetProperty("authoritativePayloadHash").GetString());
        var serialized = preview.GetProperty("canonicalPrintableText").GetString()!;
        Assert.Contains("ExitPass Parking Corporation", serialized, StringComparison.Ordinal);
        Assert.Contains("VAT REG TIN", serialized, StringComparison.Ordinal);
        Assert.Contains("123-456-789", serialized, StringComparison.Ordinal);
        Assert.Contains("GOVERNED PLATE NUMBER", serialized, StringComparison.Ordinal);
        Assert.Contains("ACCR. NO.", serialized, StringComparison.Ordinal);
        Assert.Contains("ACCR-0001", serialized, StringComparison.Ordinal);
        Assert.Contains("PTU-0001", serialized, StringComparison.Ordinal);
        Assert.Contains("GOVERNED SUPPLIER", serialized, StringComparison.Ordinal);
        Assert.Contains("PHP 0.00", serialized, StringComparison.Ordinal);
        var canonicalLines = serialized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.All(canonicalLines, line => Assert.True(line.Length <= 35, $"Canonical preview line exceeds 35 columns: '{line}'"));
        Assert.Contains("Entry Time         2026-09-29 08:42", serialized, StringComparison.Ordinal);
        Assert.Contains("Payment            2026-09-30 22:39", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PHT", serialized, StringComparison.Ordinal);
        Assert.Equal(1, canonicalLines.Count(line => line.Contains("THANK YOU FOR CHOOSING OUR SERVICE", StringComparison.Ordinal)));
        Assert.DoesNotContain("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("rawValue", serialized, StringComparison.Ordinal);
        Assert.Equal("GOVERNED TICKET", preview.GetProperty("aptTicketNumber").GetString());
        Assert.Equal(
            AptTicketQrCode.CreateDataUrl("GOVERNED TICKET"),
            preview.GetProperty("aptTicketQrCodeDataUrl").GetString());
    }

    [Fact]
    public async Task PersistedOriginalPresentationVersionIsAccepted()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        await MutateReceiptAsync(database, receipt.TerminalCashTenderId, command =>
            command.PresentationVersion = ReceiptPreviewContract.PersistedOriginalPresentationVersion);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-persisted-original");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var preview = response.RootElement.GetProperty("payload").GetProperty("preview");
        Assert.Equal(
            ReceiptPreviewContract.PersistedOriginalPresentationVersion,
            preview.GetProperty("presentationVersion").GetString());
        Assert.Contains("SALES INVOICE", preview.GetProperty("canonicalPrintableText").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("digital-sales-invoice-presentation-json-v2", "digital-sales-invoice-json-v1", "application/json", "receipt_preview_unsupported_version")]
    [InlineData("digital-sales-invoice-presentation-json-v1", "digital-sales-invoice-json-v2", "application/json", "receipt_preview_unsupported_version")]
    [InlineData("digital-sales-invoice-presentation-json-v1", "digital-sales-invoice-json-v1", "text/plain", "receipt_preview_unsupported_version")]
    public async Task UnsupportedVersionTemplateOrContentTypeIsRejectedSafely(
        string presentationVersion,
        string templateVersion,
        string contentType,
        string expectedCode)
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        await MutateReceiptAsync(database, receipt.TerminalCashTenderId, command =>
        {
            command.PresentationVersion = presentationVersion;
            command.TemplateVersion = templateVersion;
            command.ContentType = contentType;
        });
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-unsupported");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PayloadHashMismatchBlocksPreview()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        await MutateReceiptAsync(database, receipt.TerminalCashTenderId, command => command.AuthoritativePayloadHash = "sha256:tampered");
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-hash");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_integrity_failed", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedAuthoritativeJsonBlocksPreview()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        await MutateReceiptAsync(database, receipt.TerminalCashTenderId, command =>
        {
            command.AuthoritativePresentationJson = "{\"presentation\":";
            command.AuthoritativePayloadHash = TerminalCashReceiptPayloadFactory.ComputeHash(command.AuthoritativePresentationJson);
        });
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-malformed");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_decode_failed", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingPayloadBlocksPreview()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        await MutateReceiptAsync(database, receipt.TerminalCashTenderId, command =>
        {
            command.AuthoritativePresentationJson = null;
            command.AuthoritativePayloadHash = null;
        });
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-missing");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_missing_payload", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NonAvailableReceiptStateBlocksPreview()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-pending");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_not_available", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task VoidedPresentationIncludesExplicitVoidPosture()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, voided: true, complete: true);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-voided");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var preview = response.RootElement.GetProperty("payload").GetProperty("preview");
        Assert.True(preview.GetProperty("voided").GetBoolean());
        Assert.Equal("voided", preview.GetProperty("voidStatus").GetString());
        Assert.Equal("operator_void", preview.GetProperty("voidReasonCode").GetString());
    }

    [Fact]
    public async Task FeatureDisabledBlocksPreviewWithoutMutation()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: false);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-disabled");

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("feature_disabled", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, await database.CountReceiptCommandsAsync(receipt.TerminalCashTenderId));
    }

    [Theory]
    [InlineData(null, "receipt-paper-57", 57)]
    [InlineData("57", "receipt-paper-57", 57)]
    [InlineData("58", "receipt-paper-58", 58)]
    [InlineData("80", "receipt-paper-80", 80)]
    [InlineData("99", "receipt-paper-57", 57)]
    public async Task PaperWidthSelectionIsControlledAndDoesNotAlterFacts(
        string? width,
        string expectedProfile,
        int expectedWidth)
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        var hashBefore = receipt.AuthoritativePayloadHash;
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPaperWidthMm: width);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, $"corr-width-{expectedWidth}");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal(expectedProfile, payload.GetProperty("paperProfile").GetProperty("id").GetString());
        Assert.Equal(expectedWidth, payload.GetProperty("paperProfile").GetProperty("paperWidthMm").GetInt32());
        Assert.Equal(hashBefore, payload.GetProperty("preview").GetProperty("authoritativePayloadHash").GetString());
        Assert.Equal("SI-000001", payload.GetProperty("preview").GetProperty("fiscalDocumentNumber").GetString());
    }

    [Fact]
    public async Task PreviewDoesNotIntroducePrintExitProviderOrPosServerBehavior()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-boundary");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var serialized = response.RootElement.GetRawText();
        Assert.DoesNotContain("printedState", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("printJob", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exitAuthorization", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateCommand", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("posServerClient", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaceholderDatabasePathIsRejectedSafely()
    {
        var handler = CreateHandlerForDatabasePath(@"C:\<actual>\seeded.db");

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.Health, "corr-invalid-path", new { });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("LOCAL_DATABASE_CONFIGURATION_INVALID", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("<actual>", response.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("stack", response.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InaccessibleDatabasePathDoesNotEscapeBridgeHandler()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"exitpass-apt-inaccessible-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);

        try
        {
            WriteValidEnvelopeForDatabasePath(directoryPath);
            var handler = CreateHandlerForDatabasePath(directoryPath, new DeterministicLocalDatabaseKeyProtector());

            using var response = await SendAsync(handler, LocalJournalBridgeCommand.Health, "corr-unavailable-db", new { });

            Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("EncryptedDatabaseUnreadable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain(directoryPath, response.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stack", response.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ValidExistingSeededDatabaseStillInitializes()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database, complete: true);
        var handler = database.CreateHandler(new ScriptedCentralPmsReceiptClient(), receiptPreviewEnabled: true);

        using var response = await SendPreviewAsync(handler, receipt.TerminalCashTenderId, "corr-valid-existing");

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("SI-000001", response.RootElement.GetProperty("payload").GetProperty("preview").GetProperty("fiscalDocumentNumber").GetString());
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> StoreAvailableReceiptAsync(
        ReceiptBridgeTestDatabase database,
        bool voided = false,
        bool complete = false)
    {
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt, voided, complete), 200));
        return await new TerminalCashReceiptRetrievalService(client, database.OptionsForPreviewTests)
            .RetrieveReceiptAsync(receipt.Id);
    }

    private static TerminalCashReceiptPresentationResponse Available(
        TerminalCashReceiptRetrievalCommand command,
        bool voided,
        bool complete)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(CanonicalPresentation(complete), JsonOptions));

        return new TerminalCashReceiptPresentationResponse(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            "CONFIRMED",
            command.FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            command.PosFiscalDocumentId,
            "SI-000001",
            voided ? "voided" : "recorded",
            voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
            ReceiptPreviewContract.PresentationVersion,
            ReceiptPreviewContract.TemplateVersion,
            "sha256:fiscal-semantic",
            "pos-server-semantic-hash:sha256:v1",
            "MATCHED",
            ReceiptPreviewContract.ContentType,
            document.RootElement.Clone(),
            voided ? "voided" : null,
            voided ? "operator_void" : null,
            voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            Guid.Parse(command.RetrievalCorrelationId));
    }

    internal static object CanonicalPresentation(bool complete)
    {
        if (!complete)
        {
            return new
            {
                canonicalText = string.Empty,
                presentation = new
                {
                    sections = new[]
                    {
                        Section("fiscalNumbering", "Fiscal Numbering", 50,
                            Row("fiscalNumbering.fiscalDocumentNumber", "Fiscal Document Number", "SI-000001"))
                    }
                }
            };
        }

        return new
        {
            canonicalText = CanonicalPrintableText,
            presentation = new
            {
                sections = new[]
                {
                    Section("header", "Header", 10,
                        Row("header.documentTitle", "Document Title", "SALES INVOICE")),
                    Section("salesInvoiceHeaderSnapshot", "Sales Invoice Header Snapshot", 20,
                        Row("salesInvoiceHeaderSnapshot.fiscalIdentityId", "Fiscal Identity ID", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        Row("salesInvoiceHeaderSnapshot.registeredBusinessName", "Registered Business Name", "GOVERNED REGISTERED BUSINESS NAME"),
                        Row("salesInvoiceHeaderSnapshot.registeredBusinessAddress", "Registered Business Address", "GOVERNED REGISTERED BUSINESS ADDRESS"),
                        Row("salesInvoiceHeaderSnapshot.tin", "TIN", "GOVERNED TIN"),
                        Row("salesInvoiceHeaderSnapshot.posSerialNumber", "POS Serial Number", "GOVERNED POS SERIAL NUMBER"),
                        Row("salesInvoiceHeaderSnapshot.machineIdentificationNumber", "MIN", "GOVERNED MACHINE IDENTIFICATION NUMBER"),
                        Row("salesInvoiceHeaderSnapshot.parkingLocationDisplay", "Parking Location", "GOVERNED PARKING LOCATION"),
                        Row("salesInvoiceHeaderSnapshot.supplierDeveloperRegisteredName", "Supplier / Developer Registered Name", "GOVERNED SUPPLIER"),
                        Row("salesInvoiceHeaderSnapshot.supplierDeveloperAddress", "Supplier / Developer Address", "GOVERNED SUPPLIER ADDRESS"),
                        Row("salesInvoiceHeaderSnapshot.supplierDeveloperTin", "Supplier / Developer TIN", "GOVERNED SUPPLIER TIN"),
                        Row("salesInvoiceHeaderSnapshot.birAccreditationNumber", "BIR Accreditation Number", "GOVERNED BIR ACCREDITATION NO."),
                        Row("salesInvoiceHeaderSnapshot.birAccreditationIssuedDate", "BIR Accreditation Issued Date", "GOVERNED BIR ACCREDITATION DATE ISSUED"),
                        Row("salesInvoiceHeaderSnapshot.birAccreditationValidUntil", "BIR Accreditation Valid Until", "GOVERNED BIR ACCREDITATION VALID UNTIL"),
                        Row("salesInvoiceHeaderSnapshot.ptuNumber", "PTU Number", "GOVERNED PTU NO."),
                        Row("salesInvoiceHeaderSnapshot.ptuIssuedDate", "PTU Issued Date", "GOVERNED PTU DATE ISSUED"),
                        Row("salesInvoiceHeaderSnapshot.salesInvoiceLegalStatement", "Sales Invoice Legal Statement", "THIS SERVES AS YOUR SALES INVOICE")),
                    Section("fiscalNumbering", "Fiscal Numbering", 50,
                        Row("fiscalNumbering.fiscalDocumentNumber", "Fiscal Document Number", "SI-000001")),
                    Section("parkingPaymentReferences", "Parking / Payment", 60,
                        Row("parkingPaymentReferences.branchOrSite", "Branch / Site", "GOVERNED SITE"),
                        Row("parkingPaymentReferences.ticketNumber", "Ticket Number", "GOVERNED TICKET"),
                        Row("parkingPaymentReferences.plateNumber", "Plate Number", "GOVERNED PLATE NUMBER"),
                        Row("parkingPaymentReferences.entryTime", "Entry Time", "GOVERNED ENTRY TIME"),
                        Row("parkingPaymentReferences.paymentTime", "Payment Time", "GOVERNED PAYMENT TIME"),
                        Row("parkingPaymentReferences.parkingDuration", "Parking Duration", "GOVERNED DURATION"),
                        Row("parkingPaymentReferences.paymentMethod", "Payment Method", "CASH")),
                    Section("lineItems", "Line Items", 70,
                        Row("lineItems[0000].description", "Description", "Parking fee - cash"),
                        Row("lineItems[0000].quantity", "Quantity", "1"),
                        Row("lineItems[0000].netAmount", "Net Amount", "PHP 125.00")),
                    Section("vatBreakdown", "VAT Breakdown", 95,
                        Row("totals.vatableSales", "VATable Sales", "PHP 125.00"),
                        Row("totals.vatAmount", "VAT Amount", "PHP 0.00"),
                        Row("totals.vatExemptSales", "VAT Exempt Sales", "PHP 0.00"),
                        Row("totals.zeroRatedSales", "Zero Rated Sales", "PHP 0.00")),
                    Section("tenders", "Tenders", 100,
                        Row("tenders[0000].tenderTypeCodeKey", "Tender Type", "CASH"),
                        Row("tenders[0000].amount", "Tender Amount", "PHP 150.00")),
                    Section("totals", "Totals", 110,
                        Row("totals.summary.subtotal", "Subtotal", "PHP 125.00"),
                        Row("totals.summary.totalAmount", "Total Amount", "PHP 125.00"))
                }
            }
        };
    }

    internal static string CanonicalPrintableText { get; } = string.Join("\r\n",
    [
        " ExitPass Parking Corporation",
        "      123 Sample Address",
        "",
        "VAT REG TIN            123-456-789",
        "MIN                        MIN-001",
        "S/N                     POS-SN-001",
        "Branch / Site          PITX Level 3",
        "Parking Location       PITX Level 3",
        "--------------------------------",
        "         SALES INVOICE",
        "--------------------------------",
        "            ORIGINAL",
        "",
        "SI No                     SI-000001",
        "Issued Date        2026-09-30 22:40",
        "--------------------------------",
        "        PARKING DETAILS",
        "--------------------------------",
        "Ticket Number       GOVERNED TICKET",
        "Plate Number",
        "              GOVERNED PLATE NUMBER",
        "Entry Time         2026-09-29 08:42",
        "Payment            2026-09-30 22:39",
        "Duration          GOVERNED DURATION",
        "--------------------------------",
        "             ITEMS",
        "--------------------------------",
        "Item                             1",
        "Description           Parking fee",
        "Quantity                         1",
        "Unit Amount              PHP 125.00",
        "Amount                   PHP 125.00",
        "Subtotal                 PHP 125.00",
        "--------------------------------",
        "           DISCOUNTS",
        "--------------------------------",
        "Discount Reason                NONE",
        "Discount Amount            PHP 0.00",
        "--------------------------------",
        "          VAT BREAKDOWN",
        "--------------------------------",
        "VATable Sales            PHP 125.00",
        "VAT Amount                 PHP 0.00",
        "VAT Exempt Sales           PHP 0.00",
        "Zero Rated Sales           PHP 0.00",
        "--------------------------------",
        "        PAYMENT DETAILS",
        "--------------------------------",
        "Type                           CASH",
        "Amount                   PHP 150.00",
        "Total Paid               PHP 150.00",
        "Change                    PHP 25.00",
        "--------------------------------",
        "THIS SERVES AS YOUR SALES INVOICE",
        "Print Date         2026-09-30 22:40",
        "--------------------------------",
        "      Customer Information",
        "--------------------------------",
        "NAME                Juan Dela Cruz",
        "ADDRESS           123 Sample Street",
        "TIN              123-456-789-000",
        "BUS. STYLE                 Retail",
        "--------------------------------",
        "POS SOFTWARE SUPPLIER / DEVELOPER",
        "--------------------------------",
        "        GOVERNED SUPPLIER",
        "TIN                   SUPPLIER-TIN",
        "ACCR. NO.                ACCR-0001",
        "PTU                       PTU-0001",
        "THANK YOU FOR CHOOSING OUR SERVICE",
        "    ===== NOTHING FOLLOWS =====",
        ""
    ]);

    private static object Section(string name, string label, int sortOrder, params object[] rows) =>
        new { name, label, sortOrder, posture = "required", rows };

    private static object Row(string key, string label, string displayValue) =>
        new { key, label, valueKind = "text", posture = "required", displayValue, rawValue = displayValue };

    private static async Task MutateReceiptAsync(
        ReceiptBridgeTestDatabase database,
        Guid terminalCashTenderId,
        Action<TerminalCashReceiptRetrievalCommand> mutate)
    {
        await using var dbContext = new CashJournalService(database.OptionsForPreviewTests).CreateDbContext();
        var command = await dbContext.TerminalCashReceiptRetrievalCommands
            .SingleAsync(value => value.TerminalCashTenderId == terminalCashTenderId);
        mutate(command);
        command.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    private static Task<JsonDocument> SendPreviewAsync(LocalJournalBridgeHandler handler, Guid terminalCashTenderId, string correlationId) =>
        SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview, correlationId, new { localCashTenderId = terminalCashTenderId });

    private static LocalJournalBridgeHandler CreateHandlerForDatabasePath(
        string databasePath,
        ILocalDatabaseKeyProtector? databaseKeyProtector = null)
    {
        var options = new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "http://127.0.0.1:9",
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true,
            EnableCentralPmsReceiptRetrieval: true,
            DatabaseKeyProtector: databaseKeyProtector);

        return new LocalJournalBridgeHandler(
            new CashJournalService(options),
            centralPmsCashSubmissionEnabled: true,
            centralPmsFiscalIssuanceEnabled: true,
            centralPmsReceiptRetrievalEnabled: true,
            receiptPreviewEnabled: true,
            receiptPaperWidthMm: "57",
            centralPmsBaseUrl: "http://127.0.0.1:9",
            submissionService: new TerminalCashPaymentSubmissionService(new ScriptedCentralPmsClient(), options),
            fiscalService: new TerminalCashFiscalSubmissionService(new ScriptedCentralPmsFiscalClient(), options),
            receiptService: new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), options));
    }

    private static void WriteValidEnvelopeForDatabasePath(string databasePath)
    {
        var manager = new LocalDatabaseEncryptionManager(databasePath, new DeterministicLocalDatabaseKeyProtector());
        var key = LocalDatabaseKeyGenerator.Generate();
        var protectedKey = new DeterministicLocalDatabaseKeyProtector().Protect(key, LocalDatabaseKeyEnvelope.EntropyBytes);
        try
        {
            var envelope = LocalDatabaseKeyEnvelope.Create(manager.DatabaseIdentity, protectedKey, DateTimeOffset.UtcNow);
            File.WriteAllText(manager.EnvelopePath, envelope.ToJson(), Encoding.UTF8);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
    {
        var request = JsonSerializer.Serialize(
            new
            {
                source = LocalJournalBridgeCommand.Source,
                command,
                correlationId,
                payload
            },
            JsonOptions);

        var response = await handler.HandleWebMessageAsync(request);
        Assert.NotNull(response);
        return JsonDocument.Parse(response!);
    }
}
internal sealed class DeterministicLocalDatabaseKeyProtector : ILocalDatabaseKeyProtector
{
    public string Scope => LocalDatabaseKeyEnvelope.CurrentUserScope;

    public byte[] Protect(byte[] plaintextKey, byte[] entropy) => Transform(plaintextKey, entropy);

    public byte[] Unprotect(byte[] protectedKey, byte[] entropy) => Transform(protectedKey, entropy);

    private static byte[] Transform(byte[] source, byte[] entropy)
    {
        var result = new byte[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            result[index] = (byte)(source[index] ^ entropy[index % entropy.Length] ^ 0x5A);
        }

        return result;
    }
}
