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
        var sectionText = preview.GetProperty("sections").GetRawText();
        Assert.Contains("GOVERNED REGISTERED BUSINESS NAME", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED TIN", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED PLATE NUMBER", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED BIR ACCREDITATION NO.", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED BIR ACCREDITATION DATE ISSUED", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED BIR ACCREDITATION VALID UNTIL", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED PTU NO.", sectionText, StringComparison.Ordinal);
        Assert.Contains("GOVERNED PTU DATE ISSUED", sectionText, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"birAccreditationIssuedDateDisplay\"", sectionText, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"ptuIssuedDateDisplay\"", sectionText, StringComparison.Ordinal);
        Assert.Contains("PHP 0.00", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[REGISTERED BUSINESS NAME]", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[TIN]", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[PLATE NUMBER]", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[DISCOUNT AMOUNT]", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[BIR ACCREDITATION VALID UNTIL]", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("[PTU DATE ISSUED]", sectionText, StringComparison.Ordinal);
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
            var handler = CreateHandlerForDatabasePath(directoryPath);

            using var response = await SendAsync(handler, LocalJournalBridgeCommand.Health, "corr-unavailable-db", new { });

            Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("LOCAL_DATABASE_UNAVAILABLE", response.RootElement.GetProperty("error").GetProperty("code").GetString());
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
        var payload = complete
            ? """
              {
                "presentation": {
                  "registeredBusinessName": "GOVERNED REGISTERED BUSINESS NAME",
                  "registeredBusinessAddress": "GOVERNED REGISTERED BUSINESS ADDRESS",
                  "tin": "GOVERNED TIN",
                  "posSerialNumber": "GOVERNED POS SERIAL NUMBER",
                  "machineIdentificationNumber": "GOVERNED MACHINE IDENTIFICATION NUMBER",
                  "parkingLocation": "GOVERNED PARKING LOCATION",
                  "terminalId": "GOVERNED TERMINAL ID",
                  "fiscalDocumentNumber": "SI-000001",
                  "issuedAt": "GOVERNED ISSUED DATE",
                  "plateNumber": "GOVERNED PLATE NUMBER",
                  "entryTime": "GOVERNED ENTRY TIME",
                  "exitTime": "GOVERNED EXIT TIME",
                  "durationDisplay": "GOVERNED DURATION",
                  "lines": [
                    { "description": "Parking fee - cash", "quantity": "1", "unitPriceDisplay": "PHP 125.00", "displayAmount": "PHP 125.00" }
                  ],
                  "subtotalDisplay": "PHP 125.00",
                  "discounts": [
                    { "description": "None", "displayAmount": "PHP 0.00" }
                  ],
                  "vatableSalesDisplay": "PHP 125.00",
                  "outputVatDisplay": "PHP 0.00",
                  "vatExemptSalesDisplay": "PHP 0.00",
                  "zeroRatedSalesDisplay": "PHP 0.00",
                  "tenders": [
                    { "tenderType": "CASH", "provider": "not_applicable", "displayAmount": "PHP 150.00", "changeDisplay": "PHP 25.00" }
                  ],
                  "salesInvoiceStatement": "THIS SERVES AS YOUR SALES INVOICE",
                  "footer": { "message": "THANK YOU FOR CHOOSING OUR SERVICE" },
                  "birAccreditationNumber": "GOVERNED BIR ACCREDITATION NO.",
                  "birAccreditationIssuedDateDisplay": "GOVERNED BIR ACCREDITATION DATE ISSUED",
                  "birAccreditationValidUntilDisplay": "GOVERNED BIR ACCREDITATION VALID UNTIL",
                  "ptuNumber": "GOVERNED PTU NO.",
                  "ptuIssuedDateDisplay": "GOVERNED PTU DATE ISSUED"
                }
              }
              """
            : """
              {
                "presentation": {
                  "fiscalDocumentNumber": "SI-000001",
                  "lines": [
                    { "description": "Parking fee - cash", "quantity": "1", "displayAmount": "PHP 125.00" }
                  ],
                  "taxes": [
                    { "taxType": "VAT", "displayAmount": "PHP 0.00" }
                  ],
                  "totals": [
                    { "totalType": "grand_total", "displayAmount": "PHP 125.00" }
                  ],
                  "tenders": [
                    { "tenderType": "CASH", "displayAmount": "PHP 150.00", "changeDisplay": "PHP 25.00" }
                  ]
                }
              }
              """;

        using var document = JsonDocument.Parse(payload);

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

    private static LocalJournalBridgeHandler CreateHandlerForDatabasePath(string databasePath)
    {
        var options = new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "http://127.0.0.1:9",
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true,
            EnableCentralPmsReceiptRetrieval: true);

        return new LocalJournalBridgeHandler(
            new CashJournalService(options),
            enabled: true,
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
