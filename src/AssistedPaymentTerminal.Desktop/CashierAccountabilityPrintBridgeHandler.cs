using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public sealed class CashierAccountabilityPrintBridgeHandler
{
    internal const string Source = "apt-cashier-accountability-print";
    internal const string SubmitCommand = "cashierAccountabilityPrint.submit";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] RequiredSectionTitles =
    [
        "Accountability scope",
        "Custody period",
        "Physical cash activity",
        "Closing reconciliation"
    ];

    private readonly bool _printingEnabled;
    private readonly string? _configuredPrinterName;
    private readonly ICashierAccountabilityPrinter _printer;
    private readonly ConcurrentDictionary<string, SubmissionOperation> _submissions = new(StringComparer.Ordinal);

    public CashierAccountabilityPrintBridgeHandler(
        bool printingEnabled,
        string? configuredPrinterName,
        ICashierAccountabilityPrinter? printer = null)
    {
        _printingEnabled = printingEnabled;
        _configuredPrinterName = configuredPrinterName;
        _printer = printer ?? new WindowsCashierAccountabilityPrinter();
    }

    public async Task<string?> HandleWebMessageAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        CashierAccountabilityPrintBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CashierAccountabilityPrintBridgeRequest>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null || !string.Equals(request.Source, Source, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(request.Command, SubmitCommand, StringComparison.Ordinal))
        {
            return Failure(request.Command, request.CorrelationId, "UNSUPPORTED_COMMAND", "The requested report print operation is not supported.", false);
        }

        if (!Guid.TryParse(request.CorrelationId, out var correlationId) || correlationId == Guid.Empty)
        {
            return Failure(request.Command, request.CorrelationId, "MISSING_CORRELATION_ID", "Report printing requires a correlation reference.", false);
        }

        CashierAccountabilityPrintPayload? payload;
        try
        {
            payload = request.Payload.Deserialize<CashierAccountabilityPrintPayload>(JsonOptions);
        }
        catch (JsonException)
        {
            return Failure(request.Command, request.CorrelationId, "MALFORMED_REPORT", "The Cashier Accountability Report data is malformed.", false);
        }

        var validationError = Validate(payload);
        if (validationError is not null)
        {
            return Failure(request.Command, request.CorrelationId, validationError.Value.Code, validationError.Value.Message, false);
        }

        if (!_printingEnabled)
        {
            return Failure(request.Command, request.CorrelationId, "REPORT_PRINTING_DISABLED", "Cashier Accountability Report printing is disabled for this terminal.", false);
        }

        var printerName = _configuredPrinterName?.Trim() ?? string.Empty;
        var documentHash = DocumentHash(payload!.Report!);
        var candidate = new SubmissionOperation(
            documentHash,
            new Lazy<Task<CashierAccountabilityPrinterSubmissionResult>>(
                () => SubmitAsync(payload.Report!, printerName, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _submissions.GetOrAdd(payload.SubmissionKey, candidate);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(documentHash),
                Convert.FromHexString(operation.DocumentHash)))
        {
            return Failure(request.Command, request.CorrelationId, "SUBMISSION_KEY_CONFLICT", "The report print reference conflicts with different report data.", false);
        }

        var duplicateSubmissionIgnored = !ReferenceEquals(candidate, operation);
        CashierAccountabilityPrinterSubmissionResult result;
        try
        {
            result = await operation.Submission.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _submissions.TryRemove(new KeyValuePair<string, SubmissionOperation>(payload.SubmissionKey, operation));
            throw;
        }

        if (!result.SubmittedToWindowsSpooler)
        {
            if (result.Retryable)
            {
                _submissions.TryRemove(new KeyValuePair<string, SubmissionOperation>(payload.SubmissionKey, operation));
            }

            return Failure(
                request.Command,
                request.CorrelationId,
                result.FailureClassification ?? "REPORT_PRINT_FAILED",
                result.SafeMessage,
                result.Retryable);
        }

        return Success(request, new
        {
            submittedToWindowsSpooler = true,
            physicalPrintingConfirmed = false,
            duplicateSubmissionIgnored,
            printerName,
            paperWidthMm = 57,
            result.WindowsSpoolerJobId,
            safeMessage = duplicateSubmissionIgnored
                ? "This report was already submitted to the Windows spooler. No duplicate print job was created. Physical printing is not confirmed."
                : result.SafeMessage
        });
    }

    private async Task<CashierAccountabilityPrinterSubmissionResult> SubmitAsync(
        CashierAccountabilityPrintDocument report,
        string printerName,
        CancellationToken cancellationToken)
    {
        var availability = await _printer.CheckAvailabilityAsync(printerName, cancellationToken).ConfigureAwait(false);
        return availability.Available
            ? await _printer.SubmitAsync(report, printerName, cancellationToken).ConfigureAwait(false)
            : CashierAccountabilityPrinterSubmissionResult.Failed(
                availability.FailureClassification ?? "PRINTER_UNAVAILABLE",
                availability.Retryable,
                availability.SafeMessage);
    }

    private static (string Code, string Message)? Validate(CashierAccountabilityPrintPayload? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.SubmissionKey) || payload.SubmissionKey.Length > 300)
        {
            return ("INVALID_SUBMISSION_KEY", "A valid report print reference is required.");
        }

        var report = payload.Report;
        if (report is null
            || report.Status is not ("FINAL" or "INTERIM - CUSTODY OPEN")
            || !ValidText(report.CustodyReference, 500)
            || report.Sections is null
            || report.FooterNotes is null)
        {
            return ("INVALID_REPORT_DATA", "The Cashier Accountability Report data is incomplete.");
        }

        if (!payload.SubmissionKey.Contains(report.CustodyReference, StringComparison.Ordinal))
        {
            return ("INVALID_SUBMISSION_KEY", "The report print reference does not match the custody report.");
        }

        if (report.Sections.Count != RequiredSectionTitles.Length
            || report.FooterNotes.Count is < 2 or > 3)
        {
            return ("INVALID_REPORT_DATA", "The Cashier Accountability Report structure is incomplete.");
        }

        for (var index = 0; index < RequiredSectionTitles.Length; index++)
        {
            var section = report.Sections[index];
            if (!string.Equals(section.Title, RequiredSectionTitles[index], StringComparison.Ordinal)
                || section.Rows is null
                || !ValidSectionRows(section.Rows, index))
            {
                return ("INVALID_REPORT_DATA", "The Cashier Accountability Report sections are invalid.");
            }

            if (section.Rows.Any(row => row is null || !ValidText(row.Label, 200) || !ValidText(row.Value, 1000)))
            {
                return ("INVALID_REPORT_DATA", "The Cashier Accountability Report contains an invalid field.");
            }
        }

        if (report.FooterNotes.Any(note => !ValidText(note, 1000)))
        {
            return ("INVALID_REPORT_DATA", "The Cashier Accountability Report contains an invalid footer note.");
        }

        if (!string.Equals(
                report.FooterNotes[0],
                "Physical cash received is custody evidence, not fiscal sales.",
                StringComparison.Ordinal)
            || !string.Equals(
                report.FooterNotes[1],
                "This is not a POS X/Z Reading or fiscal business-day report.",
                StringComparison.Ordinal)
            || (report.FooterNotes.Count == 3
                && !string.Equals(
                    report.FooterNotes[2],
                    "The display name is current identity data. Historical attribution uses Cashier ID.",
                    StringComparison.Ordinal)))
        {
            return ("INVALID_REPORT_DATA", "The Cashier Accountability Report footer is invalid.");
        }

        var custodyRows = report.Sections[0].Rows
            .Where(row => row.Label == "Custody reference")
            .Take(2)
            .ToArray();
        if (custodyRows.Length != 1
            || !string.Equals(custodyRows[0].Value, report.CustodyReference, StringComparison.Ordinal))
        {
            return ("INVALID_REPORT_DATA", "The report custody reference is inconsistent.");
        }

        var totalCharacters = report.Sections.Sum(section =>
            section.Title.Length + section.Rows.Sum(row => row.Label.Length + row.Value.Length))
            + report.FooterNotes.Sum(note => note.Length);
        return totalCharacters <= 32_000
            ? null
            : ("REPORT_TOO_LARGE", "The Cashier Accountability Report is too large to print safely.");
    }

    private static bool ValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Contains('\0');

    private static bool ValidSectionRows(IReadOnlyList<CashierAccountabilityPrintRow> rows, int sectionIndex) =>
        sectionIndex switch
        {
            0 => LabelsEqual(rows,
                "Site",
                "Site ID",
                "Terminal",
                "Terminal ID",
                "Terminal S/N",
                "Cashier ID",
                "Cashier name (current)",
                "Shift reference",
                "Custody reference"),
            1 => LabelsEqual(rows, "Opened", "Closed", "Report generated"),
            2 => rows.Count == 7
                && rows[0].Label == "Opening cash fund"
                && rows[1].Label.StartsWith("Physical cash received (", StringComparison.Ordinal)
                && rows[1].Label.EndsWith(')')
                && rows[2].Label.StartsWith("Central-Confirmed Payments (Local Record) (", StringComparison.Ordinal)
                && rows[2].Label.EndsWith(')')
                && rows[3].Label == "Completed cash returns"
                && rows[4].Label == "Completed safe drops"
                && rows[5].Label == "Completed direct remittances"
                && rows[6].Label == "Unresolved cash submissions",
            3 => LabelsEqual(rows,
                "Expected closing cash",
                "Actual physical closing cash",
                "Signed cash variance",
                "Variance result"),
            _ => false
        };

    private static bool LabelsEqual(
        IReadOnlyList<CashierAccountabilityPrintRow> rows,
        params string[] expectedLabels) =>
        rows.Count == expectedLabels.Length
        && rows.Select(row => row.Label).SequenceEqual(expectedLabels, StringComparer.Ordinal);

    private static string DocumentHash(CashierAccountabilityPrintDocument document)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Success(CashierAccountabilityPrintBridgeRequest request, object payload) =>
        JsonSerializer.Serialize(new
        {
            source = Source,
            request.Command,
            request.CorrelationId,
            ok = true,
            payload
        }, JsonOptions);

    private static string Failure(string command, string correlationId, string code, string message, bool retryable) =>
        JsonSerializer.Serialize(new
        {
            source = Source,
            command,
            correlationId,
            ok = false,
            error = new { code, message, retryable }
        }, JsonOptions);

    private sealed record SubmissionOperation(
        string DocumentHash,
        Lazy<Task<CashierAccountabilityPrinterSubmissionResult>> Submission);
}

public sealed record CashierAccountabilityPrintBridgeRequest(
    string Source,
    string Command,
    string CorrelationId,
    JsonElement Payload);

public sealed record CashierAccountabilityPrintPayload(
    string SubmissionKey,
    CashierAccountabilityPrintDocument? Report);
