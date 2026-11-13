using System.Drawing;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CashierAccountabilityPrintBridgeHandlerTests
{
    [Fact]
    public async Task Submit_ValidatesAndSubmitsToConfiguredWindowsPrinterAt57Mm()
    {
        var printer = new RecordingAccountabilityPrinter();
        var handler = new CashierAccountabilityPrintBridgeHandler(
            printingEnabled: true,
            configuredPrinterName: "TSC TTP-225",
            printer);

        using var response = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", ValidReport());

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("submittedToWindowsSpooler").GetBoolean());
        Assert.False(payload.GetProperty("physicalPrintingConfirmed").GetBoolean());
        Assert.False(payload.GetProperty("duplicateSubmissionIgnored").GetBoolean());
        Assert.Equal("TSC TTP-225", payload.GetProperty("printerName").GetString());
        Assert.Equal(57, payload.GetProperty("paperWidthMm").GetInt32());
        Assert.Contains("Physical printing is not confirmed", payload.GetProperty("safeMessage").GetString());
        Assert.Equal(1, printer.AvailabilityChecks);
        Assert.Equal(1, printer.Submissions);
        Assert.Equal("TSC TTP-225", printer.LastPrinterName);
        Assert.Equal("custody-reference", printer.LastDocument?.CustodyReference);
        Assert.Contains(
            printer.LastDocument!.Sections.SelectMany(section => section.Rows),
            row => row.Label == "Signed cash variance" && row.Value == "-PHP 50.00");
    }

    [Fact]
    public async Task Submit_RepeatedSubmissionKeyAndIdenticalReportProducesOnePrintJob()
    {
        var printer = new RecordingAccountabilityPrinter();
        var handler = new CashierAccountabilityPrintBridgeHandler(true, "TSC TTP-225", printer);
        var report = ValidReport();

        using var first = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", report);
        using var second = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", report);

        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(second.RootElement.GetProperty("ok").GetBoolean());
        var secondPayload = second.RootElement.GetProperty("payload");
        Assert.True(secondPayload.GetProperty("duplicateSubmissionIgnored").GetBoolean());
        Assert.Contains("No duplicate print job was created", secondPayload.GetProperty("safeMessage").GetString());
        Assert.Equal(1, printer.AvailabilityChecks);
        Assert.Equal(1, printer.Submissions);
    }

    [Fact]
    public async Task Submit_SameKeyWithDifferentReportFailsWithoutAnotherPrintJob()
    {
        var printer = new RecordingAccountabilityPrinter();
        var handler = new CashierAccountabilityPrintBridgeHandler(true, "TSC TTP-225", printer);
        var changed = ValidReport() with { Status = "INTERIM - CUSTODY OPEN" };

        using var first = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", ValidReport());
        using var second = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", changed);

        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(second.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("SUBMISSION_KEY_CONFLICT", second.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, printer.Submissions);
    }

    [Fact]
    public async Task Submit_UnavailablePrinterFailsBeforeSpoolerSubmission()
    {
        var printer = new RecordingAccountabilityPrinter(
            availability: new CashierAccountabilityPrinterAvailability(
                false,
                "PRINTER_QUEUE_NOT_FOUND",
                true,
                "The configured Windows thermal printer queue was not found."));
        var handler = new CashierAccountabilityPrintBridgeHandler(true, "Missing printer", printer);

        using var response = await SendAsync(handler, Guid.NewGuid(), "submission-1-custody-reference", ValidReport());

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("PRINTER_QUEUE_NOT_FOUND", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(response.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Equal(1, printer.AvailabilityChecks);
        Assert.Equal(0, printer.Submissions);
    }

    [Fact]
    public async Task Submit_InconsistentCustodyReferenceFailsValidation()
    {
        var printer = new RecordingAccountabilityPrinter();
        var handler = new CashierAccountabilityPrintBridgeHandler(true, "TSC TTP-225", printer);
        var report = ValidReport() with { CustodyReference = "different-custody" };

        using var response = await SendAsync(handler, Guid.NewGuid(), "submission-1-different-custody", report);

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("INVALID_REPORT_DATA", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, printer.AvailabilityChecks);
        Assert.Equal(0, printer.Submissions);
    }

    [Fact]
    public void ThermalLayout_UsesFixedReadableFontAndWrapsEveryLongReferenceWithinPrintableWidth()
    {
        using var bitmap = new Bitmap(1200, 2400);
        using var graphics = Graphics.FromImage(bitmap);
        using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();
        const float printableWidth = 180f;
        var document = ValidReport() with
        {
            CustodyReference = "custody-reference-with-a-very-long-unbroken-identifier-1234567890-ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            Sections = ValidReport().Sections
                .Select((section, index) => index == 0
                    ? section with
                    {
                        Rows = section.Rows
                            .Select(row => row.Label == "Custody reference"
                                ? row with { Value = "custody-reference-with-a-very-long-unbroken-identifier-1234567890-ABCDEFGHIJKLMNOPQRSTUVWXYZ" }
                                : row)
                            .ToList()
                    }
                    : section)
                .ToList()
        };

        var canonical = CashierAccountabilityThermalFormatter.CreateCanonicalLines(document);
        var layout = CashierAccountabilityThermalFormatter.CreatePhysicalLayout(
            graphics,
            document,
            printableWidth,
            format);
        using var font = new Font(
            CashierAccountabilityThermalFormatter.MonospaceFontFamily,
            layout.FontSizeInPoints,
            FontStyle.Regular,
            GraphicsUnit.Point);

        Assert.Equal(57, Math.Round(WindowsCashierAccountabilityPrinter.PaperWidthHundredthsOfAnInch * 25.4 / 100));
        Assert.Equal(8.0f, layout.FontSizeInPoints);
        Assert.True(layout.Lines.Count > canonical.Count);
        Assert.All(layout.Lines, line => Assert.True(
            WindowsReceiptPrinter.MeasureTextBlockWidth(graphics, [line], font, format) <= printableWidth + 0.5f,
            $"Line exceeded printable width: {line}"));
        var flattenedLayout = string.Concat(layout.Lines);
        Assert.Contains(document.CustodyReference, flattenedLayout, StringComparison.Ordinal);
        Assert.Contains("not fiscal sales", flattenedLayout, StringComparison.Ordinal);
        Assert.True(WindowsReceiptPrinter.CalculateRequiredContentHeight(graphics, layout.Lines.Count, font, 0) > 0);
    }

    private static async Task<JsonDocument> SendAsync(
        CashierAccountabilityPrintBridgeHandler handler,
        Guid correlationId,
        string submissionKey,
        CashierAccountabilityPrintDocument report)
    {
        var message = JsonSerializer.Serialize(new
        {
            source = CashierAccountabilityPrintBridgeHandler.Source,
            command = CashierAccountabilityPrintBridgeHandler.SubmitCommand,
            correlationId,
            payload = new { submissionKey, report }
        });
        var response = await handler.HandleWebMessageAsync(message);
        return JsonDocument.Parse(Assert.IsType<string>(response));
    }

    private static CashierAccountabilityPrintDocument ValidReport() => new(
        "FINAL",
        "custody-reference",
        [
            new CashierAccountabilityPrintSection(
                "Accountability scope",
                [
                    new("Site", "PITX Level 3"),
                    new("Site ID", "pitx-level-3"),
                    new("Terminal", "PITX Level 3 APT"),
                    new("Terminal ID", "PITX-APT-01"),
                    new("Terminal S/N", "ACE4_2E00_653A_D952"),
                    new("Cashier ID", "cashier-reference"),
                    new("Cashier name (current)", "Current Cashier"),
                    new("Shift reference", "shift-reference"),
                    new("Custody reference", "custody-reference")
                ]),
            new CashierAccountabilityPrintSection(
                "Custody period",
                [
                    new("Opened", "Nov 13, 2026, 1:41:10 PM Asia/Manila"),
                    new("Closed", "Nov 13, 2026, 3:22:36 PM Asia/Manila"),
                    new("Report generated", "Nov 13, 2026, 3:22:42 PM Asia/Manila")
                ]),
            new CashierAccountabilityPrintSection(
                "Physical cash activity",
                [
                    new("Opening cash fund", "PHP 1,850.00"),
                    new("Physical cash received (3)", "PHP 150.00"),
                    new("Central-Confirmed Payments (Local Record) (3)", "PHP 150.00"),
                    new("Completed cash returns", "PHP 0.00"),
                    new("Completed safe drops", "PHP 1,950.00"),
                    new("Completed direct remittances", "PHP 0.00"),
                    new("Unresolved cash submissions", "0")
                ]),
            new CashierAccountabilityPrintSection(
                "Closing reconciliation",
                [
                    new("Expected closing cash", "PHP 50.00"),
                    new("Actual physical closing cash", "PHP 0.00"),
                    new("Signed cash variance", "-PHP 50.00"),
                    new("Variance result", "SHORT")
                ])
        ],
        [
            "Physical cash received is custody evidence, not fiscal sales.",
            "This is not a POS X/Z Reading or fiscal business-day report."
        ]);

    private sealed class RecordingAccountabilityPrinter(
        CashierAccountabilityPrinterAvailability? availability = null,
        CashierAccountabilityPrinterSubmissionResult? submission = null) : ICashierAccountabilityPrinter
    {
        private readonly CashierAccountabilityPrinterAvailability _availability = availability
            ?? new CashierAccountabilityPrinterAvailability(true, null, false, "Printer is available.");
        private readonly CashierAccountabilityPrinterSubmissionResult _submission = submission
            ?? CashierAccountabilityPrinterSubmissionResult.Accepted("spooler-job-1");

        public int AvailabilityChecks { get; private set; }
        public int Submissions { get; private set; }
        public string? LastPrinterName { get; private set; }
        public CashierAccountabilityPrintDocument? LastDocument { get; private set; }

        public Task<CashierAccountabilityPrinterAvailability> CheckAvailabilityAsync(
            string configuredPrinterName,
            CancellationToken cancellationToken = default)
        {
            AvailabilityChecks++;
            LastPrinterName = configuredPrinterName;
            return Task.FromResult(_availability);
        }

        public Task<CashierAccountabilityPrinterSubmissionResult> SubmitAsync(
            CashierAccountabilityPrintDocument document,
            string configuredPrinterName,
            CancellationToken cancellationToken = default)
        {
            Submissions++;
            LastDocument = document;
            LastPrinterName = configuredPrinterName;
            return Task.FromResult(_submission);
        }
    }
}
