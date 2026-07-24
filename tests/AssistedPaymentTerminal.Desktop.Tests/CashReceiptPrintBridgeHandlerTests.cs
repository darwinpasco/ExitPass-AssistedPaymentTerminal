using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CashReceiptPrintBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PrintUsesStoredAuthoritativePresentationWithoutReceiptNetworkRetrieval()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var printClient = new ScriptedCentralPmsReceiptClient();
        var printer = new ControlledReceiptPrinter();
        var handler = database.CreateHandler(
            printClient,
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: printer);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-print", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal("Original", payload.GetProperty("job").GetProperty("classification").GetString());
        Assert.Equal("SubmittedToSpooler", payload.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("SI-000001", payload.GetProperty("job").GetProperty("fiscalDocumentNumber").GetString());
        Assert.Equal(receipt.AuthoritativePayloadHash, payload.GetProperty("job").GetProperty("authoritativePayloadHash").GetString());
        var printLines = PrintLines(payload);
        Assert.Contains(printLines, line => string.Equals(line.Trim(), "SALES INVOICE", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => line.Contains("REPRINTED:", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => line.Contains("SALES INVOICE DETAILS", StringComparison.Ordinal));
        Assert.DoesNotContain("authoritativePresentationJson", response.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(printClient.Operations);
        Assert.Single(printer.SubmittedDocuments);
    }

    [Fact]
    public async Task ReprintIsLabeledAndPreservesFiscalIdentity()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var acceptedAt = DateTimeOffset.Parse("2026-07-24T07:42:00Z");
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: new ControlledReceiptPrinter(),
            siteTimeZoneId: "Singapore Standard Time",
            utcNow: () => acceptedAt);

        using var first = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-print-1", new { localCashTenderId = receipt.TerminalCashTenderId });
        using var second = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-print-2", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(second.RootElement.GetProperty("ok").GetBoolean());
        var payload = second.RootElement.GetProperty("payload");
        Assert.Equal("Reprint", payload.GetProperty("job").GetProperty("classification").GetString());
        Assert.Equal(2, payload.GetProperty("job").GetProperty("copySequence").GetInt32());
        Assert.Equal(receipt.PosFiscalDocumentId, payload.GetProperty("job").GetProperty("posFiscalDocumentId").GetGuid());
        Assert.Equal(acceptedAt, payload.GetProperty("job").GetProperty("submittedToSpoolerAt").GetDateTimeOffset());

        var printLines = PrintLines(payload);
        var reprintIndex = Array.FindIndex(printLines, line => line.Contains("REPRINTED: 2026-07-24 15:42", StringComparison.Ordinal));
        var headingIndex = Array.FindIndex(printLines, line => string.Equals(line.Trim(), "SALES INVOICE", StringComparison.Ordinal));
        Assert.True(reprintIndex >= 0, "Reprint output must include the durable REPRINTED timestamp marker.");
        Assert.True(headingIndex >= 0, "Reprint output must include the Sales Invoice heading.");
        Assert.True(reprintIndex < headingIndex, "REPRINTED marker must appear above the Sales Invoice heading.");
        Assert.DoesNotContain(printLines, line => line.Contains("SALES INVOICE DETAILS", StringComparison.Ordinal));
        Assert.Equal(receipt.AuthoritativePayloadHash, payload.GetProperty("printDocument").GetProperty("authoritativePayloadHash").GetString());
        Assert.Equal(receipt.PosFiscalDocumentId, payload.GetProperty("printDocument").GetProperty("fiscalDocumentId").GetGuid());
    }

    [Fact]
    public async Task PendingReceiptCannotPrintAndDoesNotCreatePrintJob()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: new ControlledReceiptPrinter());

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-pending", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("receipt_preview_not_available", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, await CountPrintJobsAsync(database.OptionsForPreviewTests, receipt.TerminalCashTenderId));
    }

    [Fact]
    public async Task RetryablePrinterFailureIsSafeAndPersistsLinkedAttempt()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: new ControlledReceiptPrinter(ControlledReceiptPrinterMode.RetryableFailure));

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-retryable", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var job = response.RootElement.GetProperty("payload").GetProperty("job");
        Assert.Equal("SpoolerSubmissionFailed", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("retryable").GetBoolean());
        Assert.Equal("SPOOLER_SUBMISSION_RETRYABLE", job.GetProperty("failureClassification").GetString());
        Assert.Equal(1, await CountPrintJobsAsync(database.OptionsForPreviewTests, receipt.TerminalCashTenderId));
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> StoreAvailableReceiptAsync(ReceiptBridgeTestDatabase database)
    {
        var receipt = await database.CreateRecordedFiscalWithReceiptCommandAsync();
        var client = new ScriptedCentralPmsReceiptClient();
        client.Enqueue(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(Available(receipt), 200));
        return await new TerminalCashReceiptRetrievalService(client, database.OptionsForPreviewTests)
            .RetrieveReceiptAsync(receipt.Id);
    }

    private static TerminalCashReceiptPresentationResponse Available(TerminalCashReceiptRetrievalCommand command)
    {
        using var document = JsonDocument.Parse(
            """
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
                "lines": [{ "description": "Parking fee - cash", "quantity": "1", "unitPriceDisplay": "PHP 125.00", "displayAmount": "PHP 125.00" }],
                "subtotalDisplay": "PHP 125.00",
                "discounts": [{ "description": "None", "displayAmount": "PHP 0.00" }],
                "vatableSalesDisplay": "PHP 125.00",
                "outputVatDisplay": "PHP 0.00",
                "vatExemptSalesDisplay": "PHP 0.00",
                "zeroRatedSalesDisplay": "PHP 0.00",
                "tenders": [{ "tenderType": "CASH", "provider": "not_applicable", "displayAmount": "PHP 150.00", "changeDisplay": "PHP 25.00" }],
                "salesInvoiceStatement": "THIS SERVES AS YOUR SALES INVOICE",
                "footer": { "message": "THANK YOU FOR CHOOSING OUR SERVICE" },
                "birAccreditationNumber": "GOVERNED BIR ACCREDITATION NO.",
                "birAccreditationIssuedDateDisplay": "GOVERNED BIR ACCREDITATION DATE ISSUED",
                "birAccreditationValidUntilDisplay": "GOVERNED BIR ACCREDITATION VALID UNTIL",
                "ptuNumber": "GOVERNED PTU NO.",
                "ptuIssuedDateDisplay": "GOVERNED PTU DATE ISSUED"
              }
            }
            """);

        return new TerminalCashReceiptPresentationResponse(
            command.TerminalCashTenderId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            "CONFIRMED",
            command.FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            command.PosFiscalDocumentId,
            "SI-000001",
            "recorded",
            "AVAILABLE",
            ReceiptPreviewContract.PresentationVersion,
            ReceiptPreviewContract.TemplateVersion,
            "sha256:fiscal-semantic",
            "pos-server-semantic-hash:sha256:v1",
            "MATCHED",
            ReceiptPreviewContract.ContentType,
            document.RootElement.Clone(),
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
            Guid.Parse(command.RetrievalCorrelationId));
    }

    private static async Task<int> CountPrintJobsAsync(LocalOperationsDatabaseOptions options, Guid terminalCashTenderId)
    {
        await using var dbContext = new CashJournalService(options).CreateDbContext();
        return await dbContext.TerminalCashReceiptPrintJobs.CountAsync(job => job.TerminalCashTenderId == terminalCashTenderId);
    }

    private static string[] PrintLines(JsonElement payload) =>
        payload.GetProperty("printDocument")
            .GetProperty("lines")
            .EnumerateArray()
            .Select(line => line.GetString() ?? string.Empty)
            .ToArray();

    private static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
    {
        var request = JsonSerializer.Serialize(
            new { source = LocalJournalBridgeCommand.Source, command, correlationId, payload },
            JsonOptions);
        var response = await handler.HandleWebMessageAsync(request);
        Assert.NotNull(response);
        return JsonDocument.Parse(response!);
    }
}
