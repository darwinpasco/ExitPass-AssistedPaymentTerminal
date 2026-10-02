using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;
using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop;

public sealed record ReceiptPrintDocument(
    Guid TerminalCashTenderId,
    Guid FiscalDocumentId,
    string FiscalDocumentNumber,
    string AuthoritativePayloadHash,
    string? SemanticRequestHash,
    TerminalCashReceiptPrintClassification Classification,
    int CopySequence,
    DateTimeOffset? ReprintedAt,
    string? ReprintMarker,
    ReceiptPreviewPaperProfile PaperProfile,
    IReadOnlyList<string> Lines,
    string AptTicketNumber,
    string AptTicketQrCodeDataUrl);

public sealed record ReceiptPrinterAvailability(
    bool Available,
    string? FailureClassification,
    bool Retryable,
    string SafeMessage);

public sealed record ReceiptPrinterSubmissionResult(
    bool Submitted,
    string? WindowsSpoolerJobId,
    string? FailureClassification,
    bool Retryable,
    string SafeMessage)
{
    public static ReceiptPrinterSubmissionResult Accepted(string? windowsSpoolerJobId = null) =>
        new(true, windowsSpoolerJobId, null, false, "Submitted to printer.");

    public static ReceiptPrinterSubmissionResult Failed(string failureClassification, bool retryable, string safeMessage) =>
        new(false, null, failureClassification, retryable, safeMessage);
}
public interface IReceiptPrinter
{
    Task<ReceiptPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrinterSubmissionResult> SubmitAsync(
        ReceiptPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default);
}

public static class ReceiptPrintDocumentBuilder
{
    public static ReceiptPrintDocument Build(
        ReceiptPreviewDocument preview,
        TerminalCashReceiptPrintClassification classification,
        int copySequence,
        string canonicalText,
        DateTimeOffset? reprintAcceptedAt = null)
    {
        if (string.IsNullOrWhiteSpace(canonicalText) || canonicalText.Contains('\0'))
        {
            throw new InvalidOperationException("Canonical Sales Invoice text is required for printing.");
        }

        if (classification == TerminalCashReceiptPrintClassification.Reprint && reprintAcceptedAt is null)
        {
            throw new InvalidOperationException("Governed reprint evidence is required for reprint output.");
        }

        var lines = CanonicalLines(canonicalText);
        if (!lines.Any(IsNothingFollowsLine))
            throw new InvalidOperationException("Canonical Sales Invoice text must contain the closing marker.");
        if (string.IsNullOrWhiteSpace(preview.AptTicketNumber)
            || string.IsNullOrWhiteSpace(preview.AptTicketQrCodeDataUrl))
            throw new InvalidOperationException("The governed APT ticket QR code is required for printing.");

        return new ReceiptPrintDocument(
            preview.TerminalCashTenderId,
            preview.PosFiscalDocumentId,
            preview.FiscalDocumentNumber ?? "Unavailable",
            preview.AuthoritativePayloadHash ?? "",
            preview.SemanticRequestHash,
            classification,
            copySequence,
            reprintAcceptedAt,
            ReprintMarker: null,
            preview.PaperProfile,
            lines,
            preview.AptTicketNumber,
            preview.AptTicketQrCodeDataUrl);
    }

    private static IReadOnlyList<string> CanonicalLines(string canonicalText)
    {
        var normalized = canonicalText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    internal static bool IsNothingFollowsLine(string line) =>
        line.Contains("NOTHING FOLLOWS", StringComparison.Ordinal);
}

public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
    internal const float PhysicalPrintFontScale = 1.0f;

    public Task<ReceiptPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredPrinterName))
        {
            return Task.FromResult(new ReceiptPrinterAvailability(
                false,
                "PRINTER_CONFIGURATION_MISSING",
                false,
                "A Windows printer must be configured before Sales Invoice printing."));
        }

        try
        {
            var installed = PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(value => string.Equals(value, configuredPrinterName.Trim(), StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(installed
                ? new ReceiptPrinterAvailability(true, null, false, "Printer is available.")
                : new ReceiptPrinterAvailability(
                    false,
                    "PRINTER_QUEUE_NOT_FOUND",
                    false,
                    "The configured Windows printer queue was not found."));
        }
        catch (Exception)
        {
            return Task.FromResult(new ReceiptPrinterAvailability(
                false,
                "PRINTER_AVAILABILITY_UNKNOWN",
                true,
                "Printer availability could not be confirmed."));
        }
    }

    public async Task<ReceiptPrinterSubmissionResult> SubmitAsync(
        ReceiptPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        var availability = await CheckAvailabilityAsync(configuredPrinterName, cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
        {
            return ReceiptPrinterSubmissionResult.Failed(
                availability.FailureClassification ?? "PRINTER_UNAVAILABLE",
                availability.Retryable,
                availability.SafeMessage);
        }

        try
        {
            using var printDocument = new PrintDocument();
            printDocument.PrinterSettings.PrinterName = configuredPrinterName.Trim();
            printDocument.DocumentName = $"ExitPass Sales Invoice {document.FiscalDocumentNumber} {document.Classification} #{document.CopySequence}";
            printDocument.OriginAtMargins = true;
            printDocument.DefaultPageSettings.Margins = ReceiptMargins();
            printDocument.PrintController = new StandardPrintController();
            ConfigureSingleThermalPage(printDocument, document);

            printDocument.PrintPage += (_, args) =>
            {
                if (args.Graphics is null)
                {
                    args.HasMorePages = false;
                    return;
                }

                args.Graphics.PageUnit = GraphicsUnit.Pixel;
                var contentBounds = ResolvePrintableBounds(
                    args.MarginBounds,
                    args.PageSettings.PrintableArea,
                    args.Graphics.DpiX,
                    args.Graphics.DpiY);
                using var format = CreateCanonicalTextFormat();
                var layout = CreatePhysicalLayout(args.Graphics, document.Lines, contentBounds.Width, format);
                using var font = new Font("Consolas", layout.FontSizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
                using var qrStream = new MemoryStream(AptTicketQrCode.DecodeDataUrl(document.AptTicketQrCodeDataUrl), writable: false);
                using var qrImage = Image.FromStream(qrStream);
                var lineHeight = font.GetHeight(args.Graphics);
                var y = contentBounds.Top;
                var textBlockWidth = MeasureTextBlockWidth(args.Graphics, layout.Lines, font, format);
                var x = contentBounds.Left + Math.Max(0, (contentBounds.Width - textBlockWidth) / 2);
                args.Graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                var qrPrinted = false;

                for (var lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
                {
                    var line = layout.Lines[lineIndex];
                    if (!qrPrinted && lineIndex == layout.QrInsertionLineIndex)
                    {
                        var qrSize = ResolveQrSize(contentBounds.Width, args.Graphics.DpiX);
                        var qrX = contentBounds.Left + ((contentBounds.Width - qrSize) / 2);
                        args.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        args.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        args.Graphics.DrawImage(qrImage, qrX, y, qrSize, qrSize);
                        y += qrSize + (lineHeight * 0.5f);
                        qrPrinted = true;
                    }

                    args.Graphics.DrawString(line, font, Brushes.Black, x, y, format);
                    y += lineHeight;
                }

                args.HasMorePages = false;
            };

            printDocument.Print();
            return ReceiptPrinterSubmissionResult.Accepted();
        }
        catch (InvalidPrinterException)
        {
            return ReceiptPrinterSubmissionResult.Failed(
                "PRINTER_QUEUE_INVALID",
                false,
                "The configured Windows printer queue is invalid.");
        }
        catch (Exception)
        {
            return ReceiptPrinterSubmissionResult.Failed(
                "SPOOLER_SUBMISSION_FAILED",
                true,
                "Sales Invoice submission to the Windows printer failed.");
        }
    }

    internal static void ConfigureSingleThermalPage(PrintDocument printDocument, ReceiptPrintDocument document)
    {
        var pageSettings = printDocument.DefaultPageSettings;
        var margins = ReceiptMargins();
        pageSettings.Margins = margins;

        using var graphics = printDocument.PrinterSettings.CreateMeasurementGraphics();
        graphics.PageUnit = GraphicsUnit.Pixel;
        var printableWidthHundredths = Math.Min(
            pageSettings.PrintableArea.Width,
            Math.Max(1, pageSettings.PaperSize.Width - margins.Left - margins.Right));
        var contentWidth = HundredthsToPixels(
            Math.Max(1, printableWidthHundredths - (2 * ThermalSafetyInset)),
            graphics.DpiX);
        using var format = CreateCanonicalTextFormat();
        var layout = CreatePhysicalLayout(graphics, document.Lines, contentWidth, format);
        using var font = new Font("Consolas", layout.FontSizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
        var requiredHeight = CalculateRequiredContentHeight(
            graphics,
            layout.Lines.Count,
            font,
            ResolveQrSize(contentWidth, graphics.DpiX));
        var requiredHeightHundredths = PixelsToHundredths(requiredHeight, graphics.DpiY);
        var paperHeight = (int)Math.Ceiling(
            requiredHeightHundredths + margins.Top + margins.Bottom + (2 * ThermalSafetyInset));

        pageSettings.PaperSize = new PaperSize(
            "ExitPass Receipt",
            pageSettings.PaperSize.Width,
            Math.Max(paperHeight, margins.Top + margins.Bottom + 1));
        pageSettings.Margins = margins;
    }

    internal static Font CreateFittingMonospaceFont(
        Graphics graphics,
        IReadOnlyList<string> lines,
        float availableWidth,
        StringFormat? format = null)
    {
        var ownsFormat = format is null;
        format ??= CreateCanonicalTextFormat();

        try
        {
            for (var size = 14.0f; size >= 4.0f; size -= 0.05f)
            {
                var candidate = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
                if (MeasureTextBlockWidth(graphics, lines, candidate, format) <= availableWidth)
                {
                    return candidate;
                }

                candidate.Dispose();
            }

            return new Font("Consolas", 4.0f, FontStyle.Regular, GraphicsUnit.Point);
        }
        finally
        {
            if (ownsFormat)
            {
                format.Dispose();
            }
        }
    }

    internal static ReceiptPhysicalLayout CreatePhysicalLayout(
        Graphics graphics,
        IReadOnlyList<string> canonicalLines,
        float availableWidth,
        StringFormat? format = null)
    {
        var ownsFormat = format is null;
        format ??= CreateCanonicalTextFormat();

        try
        {
            using var fittingFont = CreateFittingMonospaceFont(graphics, canonicalLines, availableWidth, format);
            var enlargedSize = Math.Min(14.0f, fittingFont.SizeInPoints * PhysicalPrintFontScale);
            using var enlargedFont = new Font("Consolas", enlargedSize, FontStyle.Regular, GraphicsUnit.Point);
            var physicalLines = new List<string>();
            var qrInsertionLineIndex = -1;
            foreach (var canonicalLine in canonicalLines)
            {
                if (qrInsertionLineIndex < 0 && ReceiptPrintDocumentBuilder.IsNothingFollowsLine(canonicalLine))
                {
                    qrInsertionLineIndex = physicalLines.Count;
                }

                physicalLines.AddRange(WrapPhysicalLine(graphics, canonicalLine, enlargedFont, availableWidth, format));
            }

            return new ReceiptPhysicalLayout(enlargedSize, physicalLines, qrInsertionLineIndex);
        }
        finally
        {
            if (ownsFormat)
            {
                format.Dispose();
            }
        }
    }

    internal static IReadOnlyList<string> WrapPhysicalLine(
        Graphics graphics,
        string line,
        Font font,
        float availableWidth,
        StringFormat format)
    {
        if (line.Length == 0)
        {
            return [string.Empty];
        }

        var wrapped = new List<string>();
        var offset = 0;
        while (offset < line.Length)
        {
            var remaining = line[offset..];
            if (MeasureLineWidth(graphics, remaining, font, format) <= availableWidth)
            {
                wrapped.Add(remaining);
                break;
            }

            var fittingLength = FindLargestFittingPrefix(
                graphics,
                remaining,
                font,
                availableWidth,
                format);
            wrapped.Add(remaining[..fittingLength]);
            offset += fittingLength;
        }

        return wrapped;
    }

    private static int FindLargestFittingPrefix(
        Graphics graphics,
        string value,
        Font font,
        float availableWidth,
        StringFormat format)
    {
        var low = 1;
        var high = value.Length;
        var best = 1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (MeasureLineWidth(graphics, value[..middle], font, format) <= availableWidth)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private const float ThermalSafetyInset = 1f;

    internal static Margins ReceiptMargins() => new(5, 5, 5, 5);

    internal static float ResolveQrSize(float printableWidth, float dpi = 100f) =>
        Math.Min(printableWidth * 0.56f, dpi * 1.1f);

    internal static RectangleF ResolvePrintableBounds(
        Rectangle marginBounds,
        RectangleF printableArea,
        float dpiX = 100f,
        float dpiY = 100f)
    {
        var width = Math.Min(marginBounds.Width, printableArea.Width);
        var height = Math.Min(marginBounds.Height, printableArea.Height);
        return new RectangleF(
            HundredthsToPixels(ThermalSafetyInset, dpiX),
            HundredthsToPixels(ThermalSafetyInset, dpiY),
            HundredthsToPixels(Math.Max(1, width - (2 * ThermalSafetyInset)), dpiX),
            HundredthsToPixels(Math.Max(1, height - (2 * ThermalSafetyInset)), dpiY));
    }

    internal static float CalculateRequiredContentHeight(
        Graphics graphics,
        int lineCount,
        Font font,
        float qrSize) =>
        (Math.Max(0, lineCount) * font.GetHeight(graphics)) + qrSize + (font.GetHeight(graphics) * 0.5f);

    internal static float HundredthsToPixels(float hundredthsOfAnInch, float dpi) =>
        hundredthsOfAnInch * dpi / 100f;

    internal static float PixelsToHundredths(float pixels, float dpi) =>
        pixels * 100f / dpi;

    internal static StringFormat CreateCanonicalTextFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return format;
    }

    internal static float MeasureTextBlockWidth(
        Graphics graphics,
        IReadOnlyList<string> lines,
        Font font,
        StringFormat format)
    {
        return lines.Count == 0
            ? 0
            : lines.Max(line => MeasureLineWidth(graphics, line, font, format));
    }

    private static float MeasureLineWidth(Graphics graphics, string line, Font font, StringFormat format) =>
        graphics.MeasureString(line, font, int.MaxValue, format).Width;
}

internal sealed record ReceiptPhysicalLayout(
    float FontSizeInPoints,
    IReadOnlyList<string> Lines,
    int QrInsertionLineIndex);

public enum ControlledReceiptPrinterMode
{
    Accept,
    PrinterUnavailable,
    RetryableFailure,
    UnknownOutcome
}

public sealed class ControlledReceiptPrinter(ControlledReceiptPrinterMode mode = ControlledReceiptPrinterMode.Accept) : IReceiptPrinter
{
    private readonly List<ReceiptPrintDocument> _submittedDocuments = [];

    public IReadOnlyList<ReceiptPrintDocument> SubmittedDocuments => _submittedDocuments;

    public Task<ReceiptPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(mode == ControlledReceiptPrinterMode.PrinterUnavailable
            ? new ReceiptPrinterAvailability(
                false,
                "PRINTER_UNAVAILABLE",
                true,
                "Controlled printer is unavailable.")
            : new ReceiptPrinterAvailability(true, null, false, "Controlled printer is available."));
    }

    public Task<ReceiptPrinterSubmissionResult> SubmitAsync(
        ReceiptPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        _submittedDocuments.Add(document);

        return Task.FromResult(mode switch
        {
            ControlledReceiptPrinterMode.RetryableFailure => ReceiptPrinterSubmissionResult.Failed(
                "SPOOLER_SUBMISSION_RETRYABLE",
                true,
                "Controlled printer failed retryably."),
            ControlledReceiptPrinterMode.UnknownOutcome => ReceiptPrinterSubmissionResult.Failed(
                "SPOOLER_OUTCOME_UNKNOWN",
                false,
                "Controlled printer outcome is unknown."),
            _ => ReceiptPrinterSubmissionResult.Accepted($"controlled-spooler-{document.CopySequence}")
        });
    }
}

public sealed class VisualSmokeReceiptPrinter : IReceiptPrinter
{
    private static readonly Guid PrinterUnavailableTenderId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeee3003");
    private static readonly Guid RetryableFailureTenderId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeee3004");
    private static readonly Guid UnknownOutcomeTenderId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeee3005");

    private readonly List<ReceiptPrintDocument> _submittedDocuments = [];

    public IReadOnlyList<ReceiptPrintDocument> SubmittedDocuments => _submittedDocuments;

    public Task<ReceiptPrinterAvailability> CheckAvailabilityAsync(
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrinterAvailability(true, null, false, "Visual smoke controlled printer is available."));
    }

    public Task<ReceiptPrinterSubmissionResult> SubmitAsync(
        ReceiptPrintDocument document,
        string configuredPrinterName,
        CancellationToken cancellationToken = default)
    {
        _submittedDocuments.Add(document);

        if (document.TerminalCashTenderId == PrinterUnavailableTenderId)
        {
            return Task.FromResult(ReceiptPrinterSubmissionResult.Failed(
                "PRINTER_UNAVAILABLE",
                true,
                "Visual smoke printer is unavailable."));
        }

        if (document.TerminalCashTenderId == RetryableFailureTenderId)
        {
            return Task.FromResult(ReceiptPrinterSubmissionResult.Failed(
                "SPOOLER_SUBMISSION_RETRYABLE",
                true,
                "Visual smoke printer failed retryably."));
        }

        if (document.TerminalCashTenderId == UnknownOutcomeTenderId)
        {
            return Task.FromResult(ReceiptPrinterSubmissionResult.Failed(
                "SPOOLER_OUTCOME_UNKNOWN",
                false,
                "Visual smoke printer outcome is unknown."));
        }

        return Task.FromResult(ReceiptPrinterSubmissionResult.Accepted($"visual-smoke-spooler-{document.CopySequence}"));
    }
}
