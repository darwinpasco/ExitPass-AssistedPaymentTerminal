using System.Drawing;
using System.Drawing.Printing;
using System.Drawing.Text;

namespace AssistedPaymentTerminal.Desktop;

public sealed record CashierAccountabilityPrintDocument(
    string Status,
    string CustodyReference,
    IReadOnlyList<CashierAccountabilityPrintSection> Sections,
    IReadOnlyList<string> FooterNotes);

public sealed record CashierAccountabilityPrintSection(
    string Title,
    IReadOnlyList<CashierAccountabilityPrintRow> Rows);

public sealed record CashierAccountabilityPrintRow(string Label, string Value);

public sealed record CashierAccountabilityPrinterAvailability(
    bool Available,
    string? FailureClassification,
    bool Retryable,
    string SafeMessage);

public sealed record CashierAccountabilityPrinterSubmissionResult(
    bool SubmittedToWindowsSpooler,
    string? WindowsSpoolerJobId,
    string? FailureClassification,
    bool Retryable,
    string SafeMessage)
{
    public static CashierAccountabilityPrinterSubmissionResult Accepted(string? windowsSpoolerJobId = null) =>
        new(
            true,
            windowsSpoolerJobId,
            null,
            false,
            "Cashier Accountability Report submitted to the Windows spooler. Physical printing is not confirmed.");

    public static CashierAccountabilityPrinterSubmissionResult Failed(
        string failureClassification,
        bool retryable,
        string safeMessage) =>
        new(false, null, failureClassification, retryable, safeMessage);
}

public interface ICashierAccountabilityPrinter
{
    Task<CashierAccountabilityPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default);

    Task<CashierAccountabilityPrinterSubmissionResult> SubmitAsync(
        CashierAccountabilityPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsCashierAccountabilityPrinter : ICashierAccountabilityPrinter
{
    internal const int PaperWidthHundredthsOfAnInch = 224;

    public Task<CashierAccountabilityPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuredPrinterName))
        {
            return Task.FromResult(new CashierAccountabilityPrinterAvailability(
                false,
                "PRINTER_CONFIGURATION_MISSING",
                false,
                "A Windows thermal printer must be configured before printing the Cashier Accountability Report."));
        }

        try
        {
            var installed = PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(value => string.Equals(value, configuredPrinterName.Trim(), StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(installed
                ? new CashierAccountabilityPrinterAvailability(true, null, false, "Printer is available.")
                : new CashierAccountabilityPrinterAvailability(
                    false,
                    "PRINTER_QUEUE_NOT_FOUND",
                    true,
                    "The configured Windows thermal printer queue was not found."));
        }
        catch (Exception)
        {
            return Task.FromResult(new CashierAccountabilityPrinterAvailability(
                false,
                "PRINTER_AVAILABILITY_UNKNOWN",
                true,
                "Windows printer availability could not be confirmed."));
        }
    }

    public async Task<CashierAccountabilityPrinterSubmissionResult> SubmitAsync(
        CashierAccountabilityPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        var availability = await CheckAvailabilityAsync(configuredPrinterName, cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
        {
            return CashierAccountabilityPrinterSubmissionResult.Failed(
                availability.FailureClassification ?? "PRINTER_UNAVAILABLE",
                availability.Retryable,
                availability.SafeMessage);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var printDocument = new PrintDocument();
            printDocument.PrinterSettings.PrinterName = configuredPrinterName.Trim();
            printDocument.DocumentName = $"ExitPass Cashier Accountability {ShortReference(document.CustodyReference)}";
            printDocument.OriginAtMargins = true;
            printDocument.PrintController = new StandardPrintController();
            ConfigureThermalPage(printDocument, document);

            printDocument.PrintPage += (_, args) =>
            {
                if (args.Graphics is null)
                {
                    args.HasMorePages = false;
                    return;
                }

                args.Graphics.PageUnit = GraphicsUnit.Pixel;
                var contentBounds = WindowsReceiptPrinter.ResolvePrintableBounds(
                    args.MarginBounds,
                    args.PageSettings.PrintableArea,
                    args.Graphics.DpiX,
                    args.Graphics.DpiY);
                using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();
                var layout = CashierAccountabilityThermalFormatter.CreatePhysicalLayout(
                    args.Graphics,
                    document,
                    contentBounds.Width,
                    format);
                using var font = new Font(
                    CashierAccountabilityThermalFormatter.MonospaceFontFamily,
                    layout.FontSizeInPoints,
                    FontStyle.Regular,
                    GraphicsUnit.Point);
                var lineHeight = font.GetHeight(args.Graphics);
                var y = contentBounds.Top;
                args.Graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

                foreach (var line in layout.Lines)
                {
                    args.Graphics.DrawString(line, font, Brushes.Black, contentBounds.Left, y, format);
                    y += lineHeight;
                }

                args.HasMorePages = false;
            };

            printDocument.Print();
            return CashierAccountabilityPrinterSubmissionResult.Accepted();
        }
        catch (InvalidPrinterException)
        {
            return CashierAccountabilityPrinterSubmissionResult.Failed(
                "PRINTER_QUEUE_INVALID",
                false,
                "The configured Windows thermal printer queue is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CashierAccountabilityPrinterSubmissionResult.Failed(
                "SPOOLER_SUBMISSION_OUTCOME_UNKNOWN",
                false,
                "Windows could not confirm the report submission outcome. Check the printer queue before trying again.");
        }
    }

    internal static void ConfigureThermalPage(
        PrintDocument printDocument,
        CashierAccountabilityPrintDocument document)
    {
        var margins = WindowsReceiptPrinter.ReceiptMargins();
        var pageSettings = printDocument.DefaultPageSettings;
        pageSettings.Margins = margins;
        pageSettings.PaperSize = new PaperSize(
            "ExitPass 57 mm Report",
            PaperWidthHundredthsOfAnInch,
            1100);

        using var graphics = printDocument.PrinterSettings.CreateMeasurementGraphics();
        graphics.PageUnit = GraphicsUnit.Pixel;
        var paperContentWidth = PaperWidthHundredthsOfAnInch - margins.Left - margins.Right - 2;
        var driverPrintableWidth = pageSettings.PrintableArea.Width > 0
            ? pageSettings.PrintableArea.Width
            : paperContentWidth;
        var contentWidth = WindowsReceiptPrinter.HundredthsToPixels(
            Math.Max(1, Math.Min(paperContentWidth, driverPrintableWidth)),
            graphics.DpiX);
        using var format = WindowsReceiptPrinter.CreateCanonicalTextFormat();
        var layout = CashierAccountabilityThermalFormatter.CreatePhysicalLayout(
            graphics,
            document,
            contentWidth,
            format);
        using var font = new Font(
            CashierAccountabilityThermalFormatter.MonospaceFontFamily,
            layout.FontSizeInPoints,
            FontStyle.Regular,
            GraphicsUnit.Point);
        var requiredHeight = WindowsReceiptPrinter.CalculateRequiredContentHeight(
            graphics,
            layout.Lines.Count,
            font,
            qrSize: 0);
        var paperHeight = (int)Math.Ceiling(
            WindowsReceiptPrinter.PixelsToHundredths(requiredHeight, graphics.DpiY)
            + margins.Top
            + margins.Bottom
            + 2);

        pageSettings.PaperSize = new PaperSize(
            "ExitPass 57 mm Report",
            PaperWidthHundredthsOfAnInch,
            Math.Max(paperHeight, margins.Top + margins.Bottom + 1));
        pageSettings.Margins = margins;
    }

    private static string ShortReference(string value) =>
        value.Length <= 32 ? value : value[..32];
}

internal static class CashierAccountabilityThermalFormatter
{
    internal const string MonospaceFontFamily = "Consolas";
    internal const float FontSizeInPoints = 8.0f;

    internal static IReadOnlyList<string> CreateCanonicalLines(CashierAccountabilityPrintDocument document)
    {
        var lines = new List<string>
        {
            "CASHIER ACCOUNTABILITY REPORT",
            document.Status,
            new('=', 32)
        };

        foreach (var section in document.Sections)
        {
            lines.Add(string.Empty);
            lines.Add(section.Title.ToUpperInvariant());
            lines.Add(new('-', 32));
            lines.AddRange(section.Rows.Select(row => $"{row.Label}: {row.Value}"));
        }

        lines.Add(string.Empty);
        lines.Add(new('=', 32));
        lines.AddRange(document.FooterNotes);
        return lines;
    }

    internal static CashierAccountabilityThermalLayout CreatePhysicalLayout(
        Graphics graphics,
        CashierAccountabilityPrintDocument document,
        float availableWidth,
        StringFormat? format = null)
    {
        var ownsFormat = format is null;
        format ??= WindowsReceiptPrinter.CreateCanonicalTextFormat();

        try
        {
            using var font = new Font(
                MonospaceFontFamily,
                FontSizeInPoints,
                FontStyle.Regular,
                GraphicsUnit.Point);
            var physicalLines = CreateCanonicalLines(document)
                .SelectMany(line => WindowsReceiptPrinter.WrapPhysicalLine(
                    graphics,
                    line,
                    font,
                    availableWidth,
                    format))
                .ToList();
            return new CashierAccountabilityThermalLayout(FontSizeInPoints, physicalLines);
        }
        finally
        {
            if (ownsFormat)
            {
                format.Dispose();
            }
        }
    }
}

internal sealed record CashierAccountabilityThermalLayout(
    float FontSizeInPoints,
    IReadOnlyList<string> Lines);
