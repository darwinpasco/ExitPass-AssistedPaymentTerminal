using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
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
    IReadOnlyList<string> Lines);

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
        DateTimeOffset? reprintAcceptedAt = null,
        TimeZoneInfo? siteTimeZone = null)
    {
        var lineWidth = preview.PaperProfile.PaperWidthMm switch
        {
            80 => 46,
            58 => 33,
            _ => 32
        };
        var lines = new List<string>();
        var separator = new string('-', lineWidth);
        string? reprintMarker = null;

        if (classification == TerminalCashReceiptPrintClassification.Reprint)
        {
            if (reprintAcceptedAt is null)
            {
                throw new InvalidOperationException("Reprint output requires the accepted reprint timestamp.");
            }

            reprintMarker = $"REPRINTED: {FormatLocalReprintTimestamp(reprintAcceptedAt.Value, siteTimeZone ?? TimeZoneInfo.Local)}";
            lines.Add(Center(reprintMarker, lineWidth));
            lines.Add(separator);
        }

        foreach (var section in preview.Sections)
        {
            AppendSection(lines, section, lineWidth, separator);
        }

        lines.Add(separator);
        lines.Add($"Fiscal doc: {preview.FiscalDocumentNumber ?? "Unavailable"}");
        lines.Add($"Payload hash: {preview.AuthoritativePayloadHash ?? "Unavailable"}");
        if (!string.IsNullOrWhiteSpace(preview.SemanticRequestHash))
        {
            lines.Add($"Semantic hash: {preview.SemanticRequestHash}");
        }

        return new ReceiptPrintDocument(
            preview.TerminalCashTenderId,
            preview.PosFiscalDocumentId,
            preview.FiscalDocumentNumber ?? "Unavailable",
            preview.AuthoritativePayloadHash ?? "",
            preview.SemanticRequestHash,
            classification,
            copySequence,
            reprintAcceptedAt,
            reprintMarker,
            preview.PaperProfile,
            lines);
    }

    private static string FormatLocalReprintTimestamp(DateTimeOffset timestamp, TimeZoneInfo siteTimeZone)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, siteTimeZone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static void AppendSection(
        List<string> lines,
        ReceiptPreviewSection section,
        int lineWidth,
        string separator)
    {
        if (section.Title == "Sales Invoice Title")
        {
            lines.Add(Center(section.Fields.FirstOrDefault()?.Value ?? "SALES INVOICE", lineWidth));
            lines.Add(separator);
            return;
        }

        lines.Add(section.Title.ToUpperInvariant());
        foreach (var field in section.Fields)
        {
            AppendField(lines, field, lineWidth);
        }

        foreach (var row in section.Rows)
        {
            foreach (var field in row.Fields)
            {
                AppendField(lines, field, lineWidth);
            }
        }

        lines.Add(separator);
    }

    private static void AppendField(List<string> lines, ReceiptPreviewField field, int lineWidth)
    {
        var prefix = string.IsNullOrWhiteSpace(field.Label) ? "" : $"{field.Label}: ";
        foreach (var line in Wrap($"{prefix}{field.Value}", lineWidth))
        {
            lines.Add(line);
        }
    }

    private static string Center(string value, int lineWidth)
    {
        var text = value.Trim();
        if (text.Length >= lineWidth)
        {
            return text;
        }

        var padding = Math.Max(0, (lineWidth - text.Length) / 2);
        return new string(' ', padding) + text;
    }

    private static IEnumerable<string> Wrap(string value, int lineWidth)
    {
        var remaining = value.Trim();
        while (remaining.Length > lineWidth)
        {
            var splitAt = remaining.LastIndexOf(' ', Math.Min(lineWidth, remaining.Length - 1));
            if (splitAt <= 0)
            {
                splitAt = lineWidth;
            }

            yield return remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}

public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
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
            printDocument.DocumentName = $"ExitPass Sales Invoice {document.FiscalDocumentNumber}";

            var lineIndex = 0;
            printDocument.PrintPage += (_, args) =>
            {
                if (args.Graphics is null)
                {
                    args.HasMorePages = false;
                    return;
                }

                using var font = new Font("Consolas", document.PaperProfile.PaperWidthMm == 80 ? 9.0f : 8.0f);
                var lineHeight = font.GetHeight(args.Graphics);
                var y = (float)args.MarginBounds.Top;

                while (lineIndex < document.Lines.Count && y + lineHeight < args.MarginBounds.Bottom)
                {
                    args.Graphics.DrawString(document.Lines[lineIndex], font, Brushes.Black, args.MarginBounds.Left, y);
                    y += lineHeight;
                    lineIndex++;
                }

                args.HasMorePages = lineIndex < document.Lines.Count;
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
}

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
