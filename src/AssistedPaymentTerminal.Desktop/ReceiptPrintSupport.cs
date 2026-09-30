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
        const int lineWidth = 48;
        var separator = new string('-', lineWidth);
        var lines = new List<string>();
        string? reprintMarker = null;

        if (classification == TerminalCashReceiptPrintClassification.Reprint)
        {
            if (reprintAcceptedAt is null)
            {
                throw new InvalidOperationException("Reprint output requires the accepted reprint timestamp.");
            }

            reprintMarker = $"REPRINTED: {FormatLocalReprintTimestamp(reprintAcceptedAt.Value, siteTimeZone ?? TimeZoneInfo.Local)}";
            lines.Add(reprintMarker);
            lines.Add(separator);
        }

        lines.AddRange(CanonicalLines(preview.CanonicalPrintableText));

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
