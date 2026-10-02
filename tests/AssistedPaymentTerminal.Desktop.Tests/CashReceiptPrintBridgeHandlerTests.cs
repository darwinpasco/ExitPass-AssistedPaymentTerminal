using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CashReceiptPrintBridgeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(TerminalCashReceiptPrintClassification.Original, TerminalCashReceiptPrintClassification.Original)]
    [InlineData(TerminalCashReceiptPrintClassification.Original, TerminalCashReceiptPrintClassification.Reprint)]
    [InlineData(TerminalCashReceiptPrintClassification.Reprint, TerminalCashReceiptPrintClassification.Original)]
    [InlineData(TerminalCashReceiptPrintClassification.Reprint, TerminalCashReceiptPrintClassification.Reprint)]
    public async Task SequentialPrintDocumentsNeverRetainPreviousInvoiceOrQr(
        TerminalCashReceiptPrintClassification firstClassification,
        TerminalCashReceiptPrintClassification secondClassification)
    {
        var printer = new ControlledReceiptPrinter();
        var profile = ReceiptPreviewPaperProfiles.Select("57").Profile;
        var first = new ReceiptPrintDocument(
            Guid.NewGuid(), Guid.NewGuid(), "SI-A", "sha256:a", null, firstClassification, 1,
            firstClassification == TerminalCashReceiptPrintClassification.Reprint ? DateTimeOffset.Parse("2026-10-01T01:00:00Z") : null,
            null, profile, ["SALES INVOICE", firstClassification.ToString().ToUpperInvariant(), "SI-A", "TICKET-A", "NOTHING FOLLOWS"],
            "TICKET-A", AptTicketQrCode.CreateDataUrl("TICKET-A"));
        var second = new ReceiptPrintDocument(
            Guid.NewGuid(), Guid.NewGuid(), "SI-B", "sha256:b", null, secondClassification, 1,
            secondClassification == TerminalCashReceiptPrintClassification.Reprint ? DateTimeOffset.Parse("2026-10-01T01:01:00Z") : null,
            null, profile, ["SALES INVOICE", secondClassification.ToString().ToUpperInvariant(), "SI-B", "TICKET-B", "NOTHING FOLLOWS"],
            "TICKET-B", AptTicketQrCode.CreateDataUrl("TICKET-B"));

        await printer.SubmitAsync(first, "Controlled Printer");
        await printer.SubmitAsync(second, "Controlled Printer");

        Assert.Equal(2, printer.SubmittedDocuments.Count);
        var submittedSecond = printer.SubmittedDocuments[1];
        Assert.Contains("SI-B", submittedSecond.Lines);
        Assert.Contains("TICKET-B", submittedSecond.Lines);
        Assert.DoesNotContain("SI-A", submittedSecond.Lines);
        Assert.DoesNotContain("TICKET-A", submittedSecond.Lines);
        Assert.Equal("TICKET-B", submittedSecond.AptTicketNumber);
        Assert.NotEqual(first.AptTicketQrCodeDataUrl, submittedSecond.AptTicketQrCodeDataUrl);
    }

    [Fact]
    public async Task PrintUsesStoredAuthoritativePresentationWithoutReceiptNetworkRetrieval()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var printClient = new ScriptedCentralPmsReceiptClient();
        var printer = new ControlledReceiptPrinter();
        var reprintClient = new ScriptedCentralPmsReceiptReprintClient(receipt, DateTimeOffset.Parse("2026-07-24T07:42:00Z"));
        var handler = database.CreateHandler(
            printClient,
            receiptPreviewEnabled: false,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: printer,
            receiptReprintClient: reprintClient);

        using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-print", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal("Original", payload.GetProperty("job").GetProperty("classification").GetString());
        Assert.Equal("SubmittedToSpooler", payload.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("SI-000001", payload.GetProperty("job").GetProperty("fiscalDocumentNumber").GetString());
        Assert.Equal(receipt.AuthoritativePayloadHash, payload.GetProperty("job").GetProperty("authoritativePayloadHash").GetString());
        var printLines = PrintLines(payload);
        var canonicalLines = CanonicalLines(CashReceiptPreviewBridgeHandlerTests.CanonicalPrintableText);
        Assert.Equal(canonicalLines, printLines);
        Assert.All(printLines, line => Assert.True(line.Length <= 48, $"Canonical receipt line exceeds 48 columns: '{line}'"));
        Assert.Contains(printLines, line => string.Equals(line.Trim(), "SALES INVOICE", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => line.Contains("REPRINTED:", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => line.Contains("SALES INVOICE DETAILS", StringComparison.Ordinal));
        Assert.DoesNotContain("authoritativePresentationJson", response.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(printClient.Operations);
        Assert.Single(printer.SubmittedDocuments);
    }

    [Theory]
    [InlineData(57, "receipt-paper-57")]
    [InlineData(58, "receipt-paper-58")]
    [InlineData(80, "receipt-paper-80")]
    public async Task PrintUsesTheExplicitSupportedPaperWidthWithoutChangingCanonicalText(int paperWidthMm, string profileId)
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var printer = new ControlledReceiptPrinter();
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: printer);

        using var response = await SendAsync(
            handler,
            LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit,
            $"corr-width-{paperWidthMm}",
            new { localCashTenderId = receipt.TerminalCashTenderId, paperWidthMm });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal(paperWidthMm, payload.GetProperty("job").GetProperty("paperWidthMm").GetInt32());
        Assert.Equal(profileId, payload.GetProperty("job").GetProperty("paperProfileId").GetString());
        Assert.Equal(CanonicalLines(CashReceiptPreviewBridgeHandlerTests.CanonicalPrintableText), PrintLines(payload));
        Assert.Single(printer.SubmittedDocuments);
    }

    [Theory]
    [InlineData(50.8f, 6.0f, 6.6f)]
    [InlineData(72.0f, 8.4f, 9.2f)]
    public void WindowsPrintFontUsesTheLargestLegibleSizeForTheActualPrintableWidth(
        float printableWidthMm,
        float minimumExpectedPoints,
        float maximumExpectedPoints)
    {
        using var bitmap = new System.Drawing.Bitmap(1200, 300);
        bitmap.SetResolution(203, 203);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var availableWidth = printableWidthMm / 25.4f * graphics.DpiX;
        var lines = new[] { new string('X', 42), "SALES INVOICE" };
        using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();

        using var font = WindowsReceiptPrinter.CreateFittingMonospaceFont(
            graphics,
            lines,
            availableWidth,
            format);
        var renderedWidth = WindowsReceiptPrinter.MeasureTextBlockWidth(graphics, lines, font, format);

        Assert.True(renderedWidth <= availableWidth, $"The 42-column canonical line does not fit {printableWidthMm} mm at {font.SizeInPoints} pt.");
        Assert.True(renderedWidth >= availableWidth * 0.95f, $"The canonical line uses only {renderedWidth / availableWidth:P1} of the printable width.");
        Assert.InRange(font.SizeInPoints, minimumExpectedPoints, maximumExpectedPoints);
    }

    [Theory]
    [InlineData(50.8f)]
    [InlineData(72.0f)]
    public void PhysicalPrintLayoutFitsCanonicalTextWithoutOverflow(float printableWidthMm)
    {
        using var bitmap = new System.Drawing.Bitmap(1200, 600);
        bitmap.SetResolution(203, 203);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var availableWidth = printableWidthMm / 25.4f * graphics.DpiX;
        var canonicalLines = CanonicalLines(CashReceiptPreviewBridgeHandlerTests.CanonicalPrintableText);
        using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();
        using var fittingFont = WindowsReceiptPrinter.CreateFittingMonospaceFont(
            graphics,
            canonicalLines,
            availableWidth,
            format);

        var layout = WindowsReceiptPrinter.CreatePhysicalLayout(
            graphics,
            canonicalLines,
            availableWidth,
            format);
        using var enlargedFont = new System.Drawing.Font(
            "Consolas",
            layout.FontSizeInPoints,
            System.Drawing.FontStyle.Regular,
            System.Drawing.GraphicsUnit.Point);

        Assert.Equal(fittingFont.SizeInPoints, layout.FontSizeInPoints, 2);
        Assert.All(
            layout.Lines,
            line => Assert.True(
                WindowsReceiptPrinter.MeasureTextBlockWidth(graphics, [line], enlargedFont, format) <= availableWidth,
                $"Wrapped physical line exceeds {printableWidthMm} mm: '{line}'"));
        Assert.Equal(canonicalLines, ReassemblePhysicalLines(canonicalLines, layout.Lines));
        Assert.InRange(layout.QrInsertionLineIndex, 0, layout.Lines.Count - 1);
    }

    [Theory]
    [InlineData(200f, 110f)]
    [InlineData(284f, 110f)]
    public void AptTicketQrUsesAReadableBoundedSizeWithinThePrintableArea(float printableWidth, float expectedSize)
    {
        Assert.Equal(expectedSize, WindowsReceiptPrinter.ResolveQrSize(printableWidth), 2);
    }

    [Fact]
    public void ReceiptMarginsUseTheTscPrintableAreaInsteadOfDefaultOneInchMargins()
    {
        var margins = WindowsReceiptPrinter.ReceiptMargins();
        var bounds = WindowsReceiptPrinter.ResolvePrintableBounds(
            new System.Drawing.Rectangle(5, 5, 200, 390),
            new System.Drawing.RectangleF(4.926108f, 0, 200, 400));

        Assert.Equal(5, margins.Left);
        Assert.Equal(5, margins.Right);
        Assert.Equal(5, margins.Top);
        Assert.Equal(5, margins.Bottom);
        Assert.Equal(1, bounds.Left, 3);
        Assert.Equal(198, bounds.Width, 3);
        Assert.Equal(50.29f, bounds.Width * 0.254f, 1);
    }

    [Fact]
    public void SingleThermalPageHeightContainsEveryLineAndQrWithoutPagination()
    {
        using var bitmap = new System.Drawing.Bitmap(1200, 300);
        bitmap.SetResolution(203, 203);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.PageUnit = System.Drawing.GraphicsUnit.Pixel;
        using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();
        var lines = Enumerable.Range(0, 84).Select(index => $"LINE {index:00}").ToArray();
        var printableWidth = WindowsReceiptPrinter.HundredthsToPixels(198, graphics.DpiX);
        var qrSize = WindowsReceiptPrinter.ResolveQrSize(printableWidth, graphics.DpiX);
        using var font = WindowsReceiptPrinter.CreateFittingMonospaceFont(graphics, lines, printableWidth, format);
        var requiredHeight = WindowsReceiptPrinter.CalculateRequiredContentHeight(
            graphics,
            lines.Length,
            font,
            qrSize);

        var lineHeight = font.GetHeight(graphics);
        Assert.True(requiredHeight >= (lines.Length * lineHeight) + qrSize + (lineHeight * 0.5f));
        Assert.True(
            WindowsReceiptPrinter.PixelsToHundredths(requiredHeight, graphics.DpiY) > 400,
            "The long receipt must be represented by one custom page taller than the driver's fixed 4-inch page.");
    }

    [Fact]
    public async Task ReprintIsLabeledAndPreservesFiscalIdentity()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var acceptedAt = DateTimeOffset.Parse("2026-07-24T07:42:00Z");
        var reprintClient = new ScriptedCentralPmsReceiptReprintClient(receipt, acceptedAt);
        var handler = database.CreateHandler(
            new ScriptedCentralPmsReceiptClient(),
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: new ControlledReceiptPrinter(),
            receiptReprintClient: reprintClient,
            siteTimeZoneId: "Singapore Standard Time",
            utcNow: () => acceptedAt);

        using var first = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "11111111-1111-4111-8111-111111111111", new { localCashTenderId = receipt.TerminalCashTenderId });
        using var second = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "22222222-2222-4222-8222-222222222222", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(second.RootElement.GetProperty("ok").GetBoolean());
        var payload = second.RootElement.GetProperty("payload");
        Assert.Equal("Reprint", payload.GetProperty("job").GetProperty("classification").GetString());
        Assert.Equal(2, payload.GetProperty("job").GetProperty("copySequence").GetInt32());
        Assert.Equal(receipt.PosFiscalDocumentId, payload.GetProperty("job").GetProperty("posFiscalDocumentId").GetGuid());
        Assert.Equal(acceptedAt, payload.GetProperty("job").GetProperty("submittedToSpoolerAt").GetDateTimeOffset());

        var printLines = PrintLines(payload);
        var reprintIndex = Array.FindIndex(printLines, line => string.Equals(line.Trim(), "REPRINT", StringComparison.Ordinal));
        var headingIndex = Array.FindIndex(printLines, line => string.Equals(line.Trim(), "SALES INVOICE", StringComparison.Ordinal));
        Assert.True(reprintIndex >= 0, "Reprint output must include the POS-governed REPRINT copy label.");
        Assert.True(headingIndex >= 0, "Reprint output must include the Sales Invoice heading.");
        Assert.True(reprintIndex > headingIndex, "POS canonical copy label must follow the Sales Invoice heading.");
        Assert.DoesNotContain(printLines, line => line.Contains("REPRINTED:", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => string.Equals(line.Trim(), "ORIGINAL", StringComparison.Ordinal));
        Assert.DoesNotContain(printLines, line => line.Contains("SALES INVOICE DETAILS", StringComparison.Ordinal));
        Assert.Equal(receipt.AuthoritativePayloadHash, payload.GetProperty("printDocument").GetProperty("authoritativePayloadHash").GetString());
        Assert.Equal(receipt.PosFiscalDocumentId, payload.GetProperty("printDocument").GetProperty("fiscalDocumentId").GetGuid());
        Assert.Single(reprintClient.Operations);
        Assert.StartsWith("apt-reprint-", reprintClient.Operations[0].OperationKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrintHistoryForTenderIsReadOnlyAndShowsOriginalReprintEvidence()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var printClient = new ScriptedCentralPmsReceiptClient();
        var printer = new ControlledReceiptPrinter();
        var reprintClient = new ScriptedCentralPmsReceiptReprintClient(receipt, DateTimeOffset.Parse("2026-07-24T07:42:00Z"));
        var handler = database.CreateHandler(
            printClient,
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: printer,
            receiptReprintClient: reprintClient);

        using var first = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "33333333-3333-4333-8333-333333333333", new { localCashTenderId = receipt.TerminalCashTenderId });
        using var second = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "44444444-4444-4444-8444-444444444444", new { localCashTenderId = receipt.TerminalCashTenderId });
        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(second.RootElement.GetProperty("ok").GetBoolean());

        using var history = await SendAsync(handler, LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForTender, "corr-history", new { localCashTenderId = receipt.TerminalCashTenderId });

        Assert.True(history.RootElement.GetProperty("ok").GetBoolean());
        var payload = history.RootElement.GetProperty("payload");
        Assert.Equal("terminalCashTenderId", payload.GetProperty("scope").GetString());
        Assert.Equal(1, payload.GetProperty("summary").GetProperty("reprintCount").GetInt32());
        Assert.Equal("Submitted to printer", payload.GetProperty("summary").GetProperty("latestStatus").GetString());
        Assert.Contains("Original submitted", payload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Original", payload.GetProperty("jobs")[0].GetProperty("classification").GetString());
        Assert.Equal("Reprint", payload.GetProperty("jobs")[1].GetProperty("classification").GetString());
        Assert.Equal("controlled-spooler-1", payload.GetProperty("jobs")[0].GetProperty("windowsSpoolerJobId").GetString());
        Assert.Empty(printClient.Operations);
        Assert.Equal(2, printer.SubmittedDocuments.Count);
    }

    [Fact]
    public async Task PrintHistoryDetailAndUnknownOutcomeExposeSafeReconciliationWithoutMutation()
    {
        using var database = ReceiptBridgeTestDatabase.Create();
        var receipt = await StoreAvailableReceiptAsync(database);
        var printClient = new ScriptedCentralPmsReceiptClient();
        var printer = new ControlledReceiptPrinter(ControlledReceiptPrinterMode.UnknownOutcome);
        var handler = database.CreateHandler(
            printClient,
            receiptPreviewEnabled: true,
            receiptPrintingEnabled: true,
            receiptPrinterName: "APT Controlled Printer",
            receiptPrinter: printer);

        using var submit = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit, "corr-unknown", new { localCashTenderId = receipt.TerminalCashTenderId });
        var printJobId = submit.RootElement.GetProperty("payload").GetProperty("job").GetProperty("printJobId").GetGuid();
        var before = await CountPrintJobsAsync(database.OptionsForPreviewTests, receipt.TerminalCashTenderId);

        using var history = await SendAsync(handler, LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForTender, "corr-history", new { localCashTenderId = receipt.TerminalCashTenderId });
        using var detail = await SendAsync(handler, LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetDetail, "corr-detail", new { printJobId });
        var after = await CountPrintJobsAsync(database.OptionsForPreviewTests, receipt.TerminalCashTenderId);

        Assert.True(history.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("PRINT_RESULT_REQUIRES_CONFIRMATION", history.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.True(detail.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("will not resubmit", detail.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("authoritativePresentationJson", detail.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, after);
        Assert.Empty(printClient.Operations);
        Assert.Single(printer.SubmittedDocuments);
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
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            CashReceiptPreviewBridgeHandlerTests.CanonicalPresentation(complete: true),
            JsonOptions));

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

    private static string[] CanonicalLines(string canonicalText) =>
        canonicalText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .SkipLast(1)
            .ToArray();

    private static string[] ReassemblePhysicalLines(
        IReadOnlyList<string> canonicalLines,
        IReadOnlyList<string> physicalLines)
    {
        var reassembled = new List<string>();
        var physicalIndex = 0;
        foreach (var canonicalLine in canonicalLines)
        {
            var value = string.Empty;
            do
            {
                value += physicalLines[physicalIndex++];
            }
            while (value.Length < canonicalLine.Length);

            reassembled.Add(value);
        }

        return reassembled.ToArray();
    }

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

internal sealed class ScriptedCentralPmsReceiptReprintClient(
    TerminalCashReceiptRetrievalCommand receipt,
    DateTimeOffset committedAt) : ICentralPmsTerminalCashReceiptReprintClient
{
    public List<(Guid TenderId, string OperationKey)> Operations { get; } = [];

    public Task<CentralPmsTerminalCashReceiptReprintResult> ReprintAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string operationKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Operations.Add((terminalCashTenderId, operationKey));
        using var reprintJson = JsonDocument.Parse($$"""
        {
          "reprintRequestId": "{{Guid.NewGuid():D}}",
          "fiscalDocumentId": "{{receipt.PosFiscalDocumentId:D}}",
          "fiscalDocumentNumber": "{{receipt.FiscalDocumentNumber}}",
          "copySequence": 1,
          "reprintStatus": "committed",
          "reprintLabelApplied": true,
          "committedAt": "{{committedAt:O}}"
        }
        """);
        var canonicalText = CashReceiptPreviewBridgeHandlerTests.CanonicalPrintableText
            .Replace("ORIGINAL", "REPRINT", StringComparison.Ordinal);
        var response = new TerminalCashReceiptReprintResponse(
            receipt.TerminalCashTenderId,
            receipt.CanonicalPaymentAttemptId,
            receipt.CanonicalPaymentConfirmationId,
            receipt.FiscalIssuanceReferenceId,
            receipt.PosFiscalDocumentId,
            receipt.FiscalDocumentNumber!,
            reprintJson.RootElement.Clone(),
            canonicalText,
            Guid.Parse(correlationId));
        return Task.FromResult(CentralPmsTerminalCashReceiptReprintResult.Success(response, 200));
    }
}
