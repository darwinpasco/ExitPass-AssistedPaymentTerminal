using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.Desktop;

public sealed class LocalJournalBridgeHandler
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> AllowedCommands =
    [
        LocalJournalBridgeCommand.Health,
        LocalJournalBridgeCommand.CreateOrGetDevelopmentSession,
        LocalJournalBridgeCommand.StartTender,
        LocalJournalBridgeCommand.RecordCashReceived,
        LocalJournalBridgeCommand.ReadTenderByParkingSession,
        LocalJournalBridgeCommand.PayableBasisStateSave,
        LocalJournalBridgeCommand.PayableBasisStateGetLatest,
        LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback,
        LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback,
        LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck,
        LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview,
        LocalJournalBridgeCommand.CentralPmsCashReceiptPrintGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit,
        LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForTender,
        LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForFiscalDocument,
        LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetRecent,
        LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetDetail
    ];

    private readonly CashJournalService _journal;
    private readonly bool _enabled;
    private readonly bool _centralPmsCashSubmissionEnabled;
    private readonly bool _centralPmsFiscalIssuanceEnabled;
    private readonly bool _centralPmsReceiptRetrievalEnabled;
    private readonly bool _receiptPreviewEnabled;
    private readonly bool _receiptPrintingEnabled;
    private readonly string? _centralPmsBaseUrl;
    private readonly string? _receiptPrinterName;
    private readonly ReceiptPreviewPaperSelection _receiptPaperSelection;
    private readonly TerminalCashPaymentSubmissionService _submissionService;
    private readonly TerminalCashFiscalSubmissionService _fiscalService;
    private readonly TerminalCashReceiptRetrievalService _receiptService;
    private readonly TerminalCashReceiptPrintJobService _printJobService;
    private readonly IReceiptPrinter _receiptPrinter;
    private readonly TimeZoneInfo _siteTimeZone;
    private readonly Func<DateTimeOffset> _utcNow;

    public LocalJournalBridgeHandler(
        CashJournalService journal,
        bool enabled,
        bool centralPmsCashSubmissionEnabled = false,
        bool centralPmsFiscalIssuanceEnabled = false,
        bool centralPmsReceiptRetrievalEnabled = false,
        bool receiptPreviewEnabled = false,
        string? receiptPaperWidthMm = null,
        string? centralPmsBaseUrl = null,
        TerminalCashPaymentSubmissionService? submissionService = null,
        TerminalCashFiscalSubmissionService? fiscalService = null,
        TerminalCashReceiptRetrievalService? receiptService = null,
        bool receiptPrintingEnabled = false,
        string? receiptPrinterName = null,
        TerminalCashReceiptPrintJobService? printJobService = null,
        IReceiptPrinter? receiptPrinter = null,
        string? siteTimeZoneId = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _journal = journal;
        _enabled = enabled;
        _centralPmsCashSubmissionEnabled = centralPmsCashSubmissionEnabled;
        _centralPmsFiscalIssuanceEnabled = centralPmsFiscalIssuanceEnabled;
        _centralPmsReceiptRetrievalEnabled = centralPmsReceiptRetrievalEnabled;
        _receiptPreviewEnabled = receiptPreviewEnabled;
        _receiptPrintingEnabled = receiptPrintingEnabled;
        _receiptPaperSelection = ReceiptPreviewPaperProfiles.Select(receiptPaperWidthMm);
        _centralPmsBaseUrl = string.IsNullOrWhiteSpace(centralPmsBaseUrl) ? null : centralPmsBaseUrl.Trim();
        _receiptPrinterName = string.IsNullOrWhiteSpace(receiptPrinterName) ? null : receiptPrinterName.Trim();
        _siteTimeZone = ResolveSiteTimeZone(siteTimeZoneId);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        var localOptions = new LocalOperationsDatabaseOptions(
            journal.DatabasePath,
            CentralPmsBaseUrl: _centralPmsBaseUrl ?? "UNCONFIGURED_CENTRAL_PMS",
            EnableCentralPmsCashSubmission: centralPmsCashSubmissionEnabled,
            EnableCentralPmsFiscalIssuance: centralPmsFiscalIssuanceEnabled,
            EnableCentralPmsReceiptRetrieval: centralPmsReceiptRetrievalEnabled);
        _submissionService = submissionService ?? new TerminalCashPaymentSubmissionService(
            new CentralPmsTerminalCashPaymentClient(new HttpClient()),
            localOptions);
        _fiscalService = fiscalService ?? new TerminalCashFiscalSubmissionService(
            new CentralPmsTerminalCashFiscalClient(new HttpClient()),
            localOptions);
        _receiptService = receiptService ?? new TerminalCashReceiptRetrievalService(
            new CentralPmsTerminalCashReceiptClient(new HttpClient()),
            localOptions);
        _printJobService = printJobService ?? new TerminalCashReceiptPrintJobService(localOptions);
        _receiptPrinter = receiptPrinter ?? new WindowsReceiptPrinter();
    }

    private static TimeZoneInfo ResolveSiteTimeZone(string? siteTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(siteTimeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(siteTimeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<string?> HandleWebMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        LocalJournalBridgeRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<LocalJournalBridgeRequest>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return SerializeFailure(
                command: "malformed",
                correlationId: "",
                code: "malformed_request",
                message: "Malformed local journal bridge request.");
        }

        if (request is null || !string.Equals(request.Source, LocalJournalBridgeCommand.Source, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return SerializeFailure(request.Command, "", "missing_correlation_id", "Local journal bridge request requires a correlation ID.");
        }

        if (!AllowedCommands.Contains(request.Command))
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "unsupported_command",
                $"Unsupported local journal bridge command '{request.Command}'.");
        }

        if (request.Command != LocalJournalBridgeCommand.Health && !_enabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Non-live cash custody capture is disabled.");
        }

        try
        {
            return request.Command switch
            {
                LocalJournalBridgeCommand.Health => await HealthAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CreateOrGetDevelopmentSession => await CreateOrGetDevelopmentSessionAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.StartTender => await StartTenderAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.RecordCashReceived => await RecordCashReceivedAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.ReadTenderByParkingSession => await ReadTenderByParkingSessionAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.PayableBasisStateSave => await SavePayableBasisStateAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.PayableBasisStateGetLatest => await GetLatestPayableBasisStateAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus => await GetCentralPmsCashSubmissionStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback => await SubmitOrReadbackCentralPmsCashSubmissionAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus => await GetCentralPmsCashFiscalStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback => await SubmitOrReadbackCentralPmsCashFiscalAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus => await GetCentralPmsCashReceiptStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck => await RetrieveOrCheckCentralPmsCashReceiptAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview => await GetCentralPmsCashReceiptPreviewAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptPrintGetStatus => await GetCentralPmsCashReceiptPrintStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptPrintSubmit => await SubmitCentralPmsCashReceiptPrintAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForTender => await GetSalesInvoicePrintHistoryForTenderAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetForFiscalDocument => await GetSalesInvoicePrintHistoryForFiscalDocumentAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetRecent => await GetRecentSalesInvoicePrintHistoryAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.SalesInvoicePrintHistoryGetDetail => await GetSalesInvoicePrintHistoryDetailAsync(request, cancellationToken).ConfigureAwait(false),
                _ => SerializeFailure(request.Command, request.CorrelationId, "unsupported_command", "Unsupported local journal bridge command.")
            };
        }
        catch (JsonException)
        {
            return SerializeFailure(request.Command, request.CorrelationId, "malformed_payload", "Malformed local journal bridge payload.");
        }
        catch (LocalOperationsDatabaseConfigurationException)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "LOCAL_DATABASE_CONFIGURATION_INVALID",
                "The configured local operational database path is invalid. Local cash actions are unavailable until the configuration is corrected.");
        }
        catch (Exception exception) when (IsLocalDatabaseUnavailable(exception))
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "LOCAL_DATABASE_UNAVAILABLE",
                "The local operational database is unavailable. Local cash actions are blocked until database access is restored.");
        }
    }

    private async Task<string> HealthAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        if (_enabled)
        {
            await _journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new LocalJournalHealthResponse(
                Healthy: true,
                Enabled: _enabled,
                DatabasePath: _journal.DatabasePath,
                CashDrawerEnabled: false,
                AuthorityWarning: "Local CASH_RECEIVED is terminal-local custody evidence only. Canonical payment and fiscal issuance are not performed."));
    }

    private async Task<string> CreateOrGetDevelopmentSessionAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CreateDevelopmentSessionPayload>(request);
        var result = await _journal.CreateOrGetCashCustodySessionAsync(new CreateCashCustodySessionRequest(
            CashierId: payload.CashierId,
            AuthenticatedCashierSessionReference: payload.AuthenticatedCashierSessionReference,
            CashierShiftId: payload.CashierShiftId,
            TerminalId: payload.TerminalId,
            SiteId: payload.SiteId,
            SiteGroupId: payload.SiteGroupId,
            PosServerId: payload.PosServerId,
            OpeningCashAmount: payload.OpeningCashAmount), cancellationToken).ConfigureAwait(false);

        return BridgeResult(request, result);
    }

    private async Task<string> StartTenderAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<StartTenderPayload>(request);
        var result = await _journal.StartCashTenderAsync(new StartCashTenderRequest(
            CashCustodySessionId: payload.CashCustodySessionId,
            ParkingSessionId: payload.ParkingSessionId,
            TariffSnapshotId: payload.TariffSnapshotId,
            Currency: payload.Currency,
            AmountDue: payload.AmountDue,
            AmountTendered: payload.AmountTendered,
            CorrelationId: request.CorrelationId,
            LocalIdempotencyIdentity: payload.LocalIdempotencyIdentity,
            LocalCashTenderId: payload.LocalCashTenderId), cancellationToken).ConfigureAwait(false);

        return BridgeResult(request, result);
    }

    private async Task<string> RecordCashReceivedAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<RecordCashReceivedPayload>(request);
        var result = await _journal.CommitCashReceivedAsync(new CommitCashReceivedRequest(
            LocalCashTenderId: payload.LocalCashTenderId,
            CashierAttested: payload.CashierAttested,
            Denominations: payload.Denominations
                .Where(denomination => denomination.Quantity > 0)
                .Select(denomination => new CashDenominationLine(
                    denomination.DenominationCode,
                    denomination.DenominationValue,
                    denomination.Quantity))
                .ToArray(),
            CentralPmsTarget: CentralPmsConfigurationIsValid()
                ? _centralPmsBaseUrl!
                : "UNCONFIGURED_CENTRAL_PMS"), cancellationToken).ConfigureAwait(false);

        return BridgeResult(request, result);
    }


    private async Task<string> SavePayableBasisStateAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<SavePayableBasisStatePayload>(request);
        var result = await _journal.SavePayableBasisStateAsync(new SavePayableBasisStateRequest(
            payload.LocalWorkflowId,
            payload.LookupReferenceType,
            payload.LookupReferenceValue,
            payload.ParkingSessionId,
            payload.TariffSnapshotId,
            payload.SiteId,
            payload.SiteGroupId,
            payload.SitePosServerId,
            payload.TerminalId,
            payload.AuthoritativeAmountMinorUnits,
            payload.Currency,
            payload.TariffCalculatedAt,
            payload.TariffValidUntil,
            payload.FeeValidUntil,
            payload.ParkingStatus,
            payload.PaymentStatus,
            payload.SessionReadiness,
            payload.TariffReadiness,
            payload.PaymentEligibility,
            payload.TerminalCashAvailability,
            payload.FiscalReadiness,
            payload.SalesInvoiceConfigurationReadiness,
            payload.CashAcceptanceReadiness,
            payload.ReadyForCashAcceptance,
            payload.BlockingReasonCodes,
            payload.Retryable,
            payload.SafeUserFacingClassification,
            payload.CentralPmsCorrelationId,
            payload.RevalidationOutcome,
            payload.CashierAcknowledgementRequired,
            payload.AmountChanged,
            payload.PriorDisplayedAmountMinorUnits), cancellationToken).ConfigureAwait(false);

        return SerializeSuccess(request.Command, request.CorrelationId, result);
    }

    private async Task<string> GetLatestPayableBasisStateAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<GetLatestPayableBasisStatePayload>(request);
        var result = await _journal.GetLatestPayableBasisStateAsync(
            payload.TerminalId,
            payload.SiteId,
            cancellationToken).ConfigureAwait(false);

        return SerializeSuccess(request.Command, request.CorrelationId, result);
    }
    private async Task<string> GetCentralPmsCashSubmissionStatusAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashSubmissionPayload>(request);
        var configuration = CentralPmsConfiguration();

        if (!_centralPmsCashSubmissionEnabled || !configuration.Valid)
        {
            return SerializeSuccess(
                request.Command,
                request.CorrelationId,
                new CentralPmsCashSubmissionStatusResponse(
                    _centralPmsCashSubmissionEnabled,
                    configuration.Valid,
                    configuration.Message,
                    null));
        }

        var command = await _journal.GetTerminalCashPaymentOutboxCommandByTenderAsync(
            payload.LocalCashTenderId,
            cancellationToken).ConfigureAwait(false);

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashSubmissionStatusResponse(
                _centralPmsCashSubmissionEnabled,
                configuration.Valid,
                configuration.Message,
                command is null ? null : CentralPmsCashSubmissionCommandSnapshot.FromEntity(command)));
    }

    private async Task<string> SubmitOrReadbackCentralPmsCashSubmissionAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashSubmissionPayload>(request);
        var configuration = CentralPmsConfiguration();

        if (!_centralPmsCashSubmissionEnabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Central PMS cash submission is disabled.");
        }

        if (!configuration.Valid)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "central_pms_configuration_invalid",
                configuration.Message);
        }

        var command = await _journal.GetTerminalCashPaymentOutboxCommandByTenderAsync(
            payload.LocalCashTenderId,
            cancellationToken).ConfigureAwait(false);

        if (command is null)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "outbox_command_not_found",
                $"No Central PMS cash-payment outbox command exists for local tender '{payload.LocalCashTenderId}'.");
        }

        var submitted = await _submissionService.SubmitOrReadbackAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashSubmissionStatusResponse(
                _centralPmsCashSubmissionEnabled,
                configuration.Valid,
                configuration.Message,
                CentralPmsCashSubmissionCommandSnapshot.FromEntity(submitted)));
    }

    private async Task<string> ReadTenderByParkingSessionAsync(LocalJournalBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadPayload<ReadTenderByParkingSessionPayload>(request);
        var tender = await _journal.GetCashTenderByParkingSessionAsync(payload.ParkingSessionId, cancellationToken).ConfigureAwait(false);
        var events = tender is null
            ? []
            : await _journal.GetCashTenderEventsAsync(tender.Id, cancellationToken).ConfigureAwait(false);

        return SerializeSuccess(request.Command, request.CorrelationId, new LocalTenderReadbackResponse(tender, events));
    }

    private async Task<string> GetCentralPmsCashFiscalStatusAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashFiscalPayload>(request);
        var configuration = CentralPmsFiscalConfiguration();

        if (!_centralPmsFiscalIssuanceEnabled || !configuration.Valid)
        {
            return SerializeSuccess(
                request.Command,
                request.CorrelationId,
                new CentralPmsCashFiscalStatusResponse(
                    _centralPmsFiscalIssuanceEnabled,
                    configuration.Valid,
                    configuration.Message,
                    null));
        }

        TerminalCashFiscalOutboxCommand? command;
        try
        {
            command = await _fiscalService.GetFiscalCommandByTenderAsync(
                    payload.LocalCashTenderId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (command is null)
            {
                command = await _fiscalService.EnsureForConfirmedPaymentAsync(
                        payload.LocalCashTenderId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "fiscal_command_unavailable",
                ex.Message);
        }

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashFiscalStatusResponse(
                _centralPmsFiscalIssuanceEnabled,
                configuration.Valid,
                configuration.Message,
                CentralPmsCashFiscalCommandSnapshot.FromEntity(command)));
    }

    private async Task<string> SubmitOrReadbackCentralPmsCashFiscalAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashFiscalPayload>(request);
        var configuration = CentralPmsFiscalConfiguration();

        if (!_centralPmsFiscalIssuanceEnabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Central PMS fiscal issuance is disabled.");
        }

        if (!configuration.Valid)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "central_pms_configuration_invalid",
                configuration.Message);
        }

        TerminalCashFiscalOutboxCommand command;
        try
        {
            command = await _fiscalService.GetFiscalCommandByTenderAsync(
                    payload.LocalCashTenderId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? await _fiscalService.EnsureForConfirmedPaymentAsync(
                        payload.LocalCashTenderId,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "fiscal_command_unavailable",
                ex.Message);
        }

        var submitted = await _fiscalService.SubmitOrReadbackFiscalAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashFiscalStatusResponse(
                _centralPmsFiscalIssuanceEnabled,
                configuration.Valid,
                configuration.Message,
                CentralPmsCashFiscalCommandSnapshot.FromEntity(submitted)));
    }

    private async Task<string> GetCentralPmsCashReceiptStatusAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);
        var configuration = CentralPmsReceiptConfiguration();

        if (!_centralPmsReceiptRetrievalEnabled || !configuration.Valid)
        {
            return SerializeSuccess(
                request.Command,
                request.CorrelationId,
                new CentralPmsCashReceiptStatusResponse(
                    _centralPmsReceiptRetrievalEnabled,
                    configuration.Valid,
                    configuration.Message,
                    null));
        }

        TerminalCashReceiptRetrievalCommand? command;
        try
        {
            command = await _receiptService.GetReceiptRetrievalByTenderAsync(
                    payload.LocalCashTenderId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (command is null)
            {
                command = await _receiptService.EnsureForRecordedFiscalAsync(
                        payload.LocalCashTenderId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_retrieval_unavailable",
                ex.Message);
        }

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashReceiptStatusResponse(
                _centralPmsReceiptRetrievalEnabled,
                configuration.Valid,
                configuration.Message,
                CentralPmsCashReceiptCommandSnapshot.FromEntity(command)));
    }

    private async Task<string> RetrieveOrCheckCentralPmsCashReceiptAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);
        var configuration = CentralPmsReceiptConfiguration();

        if (!_centralPmsReceiptRetrievalEnabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Central PMS receipt retrieval is disabled.");
        }

        if (!configuration.Valid)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "central_pms_configuration_invalid",
                configuration.Message);
        }

        TerminalCashReceiptRetrievalCommand command;
        try
        {
            command = await _receiptService.GetReceiptRetrievalByTenderAsync(
                    payload.LocalCashTenderId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? await _receiptService.EnsureForRecordedFiscalAsync(
                        payload.LocalCashTenderId,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_retrieval_unavailable",
                ex.Message);
        }

        var retrieved = await _receiptService.RetrieveReceiptAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashReceiptStatusResponse(
                _centralPmsReceiptRetrievalEnabled,
                configuration.Valid,
                configuration.Message,
                CentralPmsCashReceiptCommandSnapshot.FromEntity(retrieved)));
    }

    private async Task<string> GetCentralPmsCashReceiptPreviewAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);

        if (!_receiptPreviewEnabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Receipt preview is disabled.");
        }

        var command = await _receiptService.GetReceiptRetrievalByTenderAsync(
                payload.LocalCashTenderId,
                cancellationToken)
            .ConfigureAwait(false);

        if (command is null)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_retrieval_not_found",
                $"No durable receipt-retrieval record exists for local tender '{payload.LocalCashTenderId}'.");
        }

        var build = ReceiptPreviewBuilder.Build(command, _receiptPaperSelection.Profile);
        if (!build.Success)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                build.ErrorCode!,
                build.ErrorMessage!,
                new CentralPmsCashReceiptPreviewBlockedDetail(
                    CentralPmsCashReceiptCommandSnapshot.FromEntity(command),
                    _receiptPaperSelection.Profile,
                    _receiptPaperSelection.Warning));
        }

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashReceiptPreviewResponse(
                _receiptPreviewEnabled,
                CentralPmsCashReceiptCommandSnapshot.FromEntity(command),
                build.Document!,
                _receiptPaperSelection.Profile,
                _receiptPaperSelection.Warning));
    }

    private async Task<string> GetCentralPmsCashReceiptPrintStatusAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);
        var jobs = await _printJobService.GetJobsForTenderAsync(payload.LocalCashTenderId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var receipt = await _receiptService.GetReceiptRetrievalByTenderAsync(payload.LocalCashTenderId, cancellationToken).ConfigureAwait(false);
        var configuration = ReceiptPrintConfiguration();

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashReceiptPrintStatusResponse(
                _receiptPrintingEnabled,
                configuration.Valid,
                configuration.Message,
                receipt is null ? null : CentralPmsCashReceiptCommandSnapshot.FromEntity(receipt),
                jobs.Select(CentralPmsCashReceiptPrintJobSnapshot.FromEntity).ToArray()));
    }

    private async Task<string> GetSalesInvoicePrintHistoryForTenderAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);
        var jobs = await _printJobService.GetJobsForTenderAsync(
                payload.LocalCashTenderId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            CentralPmsCashReceiptPrintHistoryResponse.FromJobs("terminalCashTenderId", jobs));
    }

    private async Task<string> GetSalesInvoicePrintHistoryForFiscalDocumentAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptFiscalDocumentPayload>(request);
        var jobs = await _printJobService.GetJobsForFiscalDocumentAsync(
                payload.FiscalDocumentId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            CentralPmsCashReceiptPrintHistoryResponse.FromJobs("fiscalDocumentId", jobs));
    }

    private async Task<string> GetRecentSalesInvoicePrintHistoryAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new CentralPmsCashReceiptRecentPrintHistoryPayload(null)
            : ReadPayload<CentralPmsCashReceiptRecentPrintHistoryPayload>(request);
        var jobs = await _printJobService.GetRecentJobsAsync(payload.MaxResults ?? 50, cancellationToken).ConfigureAwait(false);

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            CentralPmsCashReceiptPrintHistoryResponse.FromJobs("recent", jobs));
    }

    private async Task<string> GetSalesInvoicePrintHistoryDetailAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPrintJobPayload>(request);
        var job = await _printJobService.GetJobAsync(payload.PrintJobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "print_job_not_found",
                "No local Sales Invoice print attempt exists for the requested support reference.");
        }

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            CentralPmsCashReceiptPrintHistoryDetailResponse.FromJob(job));
    }

    private async Task<string> SubmitCentralPmsCashReceiptPrintAsync(
        LocalJournalBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload<CentralPmsCashReceiptPayload>(request);
        var configuration = ReceiptPrintConfiguration();

        if (!_receiptPrintingEnabled)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "feature_disabled",
                "Sales Invoice printing is disabled.");
        }

        if (!configuration.Valid)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_printer_configuration_invalid",
                configuration.Message);
        }

        var receipt = await _receiptService.GetReceiptRetrievalByTenderAsync(
                payload.LocalCashTenderId,
                cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_retrieval_not_found",
                $"No durable receipt-retrieval record exists for local tender '{payload.LocalCashTenderId}'.");
        }

        var build = ReceiptPreviewBuilder.Build(receipt, _receiptPaperSelection.Profile);
        if (!build.Success)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                build.ErrorCode!,
                build.ErrorMessage!,
                new CentralPmsCashReceiptPreviewBlockedDetail(
                    CentralPmsCashReceiptCommandSnapshot.FromEntity(receipt),
                    _receiptPaperSelection.Profile,
                    _receiptPaperSelection.Warning));
        }

        TerminalCashReceiptPrintJob job;
        try
        {
            job = await _printJobService.RequestPrintJobAsync(
                    receipt,
                    _receiptPrinterName!,
                    _receiptPaperSelection.Profile.PaperWidthMm,
                    _receiptPaperSelection.Profile.Id,
                    request.CorrelationId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return SerializeFailure(
                request.Command,
                request.CorrelationId,
                "receipt_print_request_blocked",
                ex.Message);
        }

        job = await _printJobService.MarkPreparingAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var reprintAcceptedAt = job.Classification == TerminalCashReceiptPrintClassification.Reprint
            ? _utcNow()
            : (DateTimeOffset?)null;
        var document = ReceiptPrintDocumentBuilder.Build(
            build.Document!,
            job.Classification,
            job.CopySequence,
            reprintAcceptedAt,
            _siteTimeZone);
        var availability = await _receiptPrinter.CheckAvailabilityAsync(_receiptPrinterName!, cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
        {
            job = await _printJobService.MarkFailedAsync(
                    job.Id,
                    TerminalCashReceiptPrintJobStatus.PrinterUnavailable,
                    availability.FailureClassification ?? "PRINTER_UNAVAILABLE",
                    availability.Retryable,
                    cancellationToken)
                .ConfigureAwait(false);

            return SerializeSuccess(
                request.Command,
                request.CorrelationId,
                new CentralPmsCashReceiptPrintSubmitResponse(
                    CentralPmsCashReceiptPrintJobSnapshot.FromEntity(job),
                    document,
                    availability.SafeMessage));
        }

        var submitted = await _receiptPrinter.SubmitAsync(document, _receiptPrinterName!, cancellationToken).ConfigureAwait(false);
        var failedStatus = string.Equals(submitted.FailureClassification, "SPOOLER_OUTCOME_UNKNOWN", StringComparison.Ordinal)
            ? TerminalCashReceiptPrintJobStatus.UnknownAfterRestart
            : TerminalCashReceiptPrintJobStatus.SpoolerSubmissionFailed;
        job = submitted.Submitted
            ? await _printJobService.MarkSubmittedToSpoolerAsync(job.Id, submitted.WindowsSpoolerJobId, reprintAcceptedAt, cancellationToken).ConfigureAwait(false)
            : await _printJobService.MarkFailedAsync(
                    job.Id,
                    failedStatus,
                    submitted.FailureClassification ?? "SPOOLER_SUBMISSION_FAILED",
                    submitted.Retryable,
                    cancellationToken)
                .ConfigureAwait(false);

        return SerializeSuccess(
            request.Command,
            request.CorrelationId,
            new CentralPmsCashReceiptPrintSubmitResponse(
                CentralPmsCashReceiptPrintJobSnapshot.FromEntity(job),
                document,
                submitted.SafeMessage));
    }

    private static T ReadPayload<T>(LocalJournalBridgeRequest request)
    {
        if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new JsonException("Payload is required.");
        }

        return request.Payload.Deserialize<T>(JsonOptions) ?? throw new JsonException("Payload is required.");
    }

    private static string BridgeResult<T>(LocalJournalBridgeRequest request, CashJournalResult<T> result) =>
        result.IsSuccess
            ? SerializeSuccess(request.Command, request.CorrelationId, result.Value)
            : SerializeFailure(
                request.Command,
                request.CorrelationId,
                result.Error!.Code.ToString(),
                result.Error.Message,
                new
                {
                    result.Error.ExistingCashTenderId,
                    result.Error.ExistingCashTenderState
                });

    private static string SerializeSuccess(string command, string correlationId, object? payload) =>
        JsonSerializer.Serialize(new LocalJournalBridgeResponse(true, command, correlationId, payload, null), JsonOptions);

    private static string SerializeFailure(string command, string correlationId, string code, string message, object? detail = null) =>
        JsonSerializer.Serialize(
            new LocalJournalBridgeResponse(
                Ok: false,
                Command: command,
                CorrelationId: correlationId,
                Payload: null,
                Error: new LocalJournalBridgeError(code, message, detail)),
            JsonOptions);

    private static bool IsLocalDatabaseUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException
                or IOException
                or UnauthorizedAccessException
                or DbUpdateException)
            {
                return true;
            }
        }

        return false;
    }

    private bool CentralPmsConfigurationIsValid() =>
        CentralPmsConfiguration().Valid;

    private (bool Valid, string Message) CentralPmsConfiguration()
    {
        if (!_centralPmsCashSubmissionEnabled)
        {
            return (false, "Central PMS cash submission is disabled.");
        }

        if (!Uri.TryCreate(_centralPmsBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Host.EndsWith(".example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "CENTRAL_PMS_BASE_URL is not configured for Central PMS cash submission.");
        }

        return (true, "Central PMS cash submission is available.");
    }

    private (bool Valid, string Message) CentralPmsFiscalConfiguration()
    {
        if (!_centralPmsFiscalIssuanceEnabled)
        {
            return (false, "Central PMS fiscal issuance is disabled.");
        }

        if (!Uri.TryCreate(_centralPmsBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Host.EndsWith(".example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "CENTRAL_PMS_BASE_URL is not configured for Central PMS fiscal issuance.");
        }

        return (true, "Central PMS fiscal issuance is available.");
    }

    private (bool Valid, string Message) CentralPmsReceiptConfiguration()
    {
        if (!_centralPmsReceiptRetrievalEnabled)
        {
            return (false, "Central PMS receipt retrieval is disabled.");
        }

        if (!Uri.TryCreate(_centralPmsBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Host.EndsWith(".example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "CENTRAL_PMS_BASE_URL is not configured for receipt retrieval.");
        }

        return (true, "Central PMS receipt retrieval is available.");
    }

    private (bool Valid, string Message) ReceiptPrintConfiguration()
    {
        if (!_receiptPrintingEnabled)
        {
            return (false, "Sales Invoice printing is disabled.");
        }

        if (!_receiptPreviewEnabled)
        {
            return (false, "Receipt preview must be enabled before Sales Invoice printing.");
        }

        if (string.IsNullOrWhiteSpace(_receiptPrinterName))
        {
            return (false, "APT_RECEIPT_PRINTER_NAME is not configured for Sales Invoice printing.");
        }

        return (true, "Sales Invoice printing is configured.");
    }
}

public sealed record LocalJournalBridgeRequest(
    string Source,
    string Command,
    string CorrelationId,
    JsonElement Payload);

public sealed record LocalJournalBridgeResponse(
    bool Ok,
    string Command,
    string CorrelationId,
    object? Payload,
    LocalJournalBridgeError? Error)
{
    public string Source { get; init; } = LocalJournalBridgeCommand.Source;
}

public sealed record LocalJournalBridgeError(string Code, string Message, object? Detail = null);

public sealed record LocalJournalHealthResponse(
    bool Healthy,
    bool Enabled,
    string DatabasePath,
    bool CashDrawerEnabled,
    string AuthorityWarning);

public sealed record CreateDevelopmentSessionPayload(
    string CashierId,
    string AuthenticatedCashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    string SiteId,
    string SiteGroupId,
    string PosServerId,
    decimal OpeningCashAmount);

public sealed record StartTenderPayload(
    Guid? LocalCashTenderId,
    Guid CashCustodySessionId,
    string ParkingSessionId,
    string TariffSnapshotId,
    string Currency,
    decimal AmountDue,
    decimal AmountTendered,
    string LocalIdempotencyIdentity);

public sealed record RecordCashReceivedPayload(
    Guid LocalCashTenderId,
    bool CashierAttested,
    IReadOnlyCollection<BridgeDenominationLine> Denominations);

public sealed record BridgeDenominationLine(
    string DenominationCode,
    decimal DenominationValue,
    int Quantity);

public sealed record ReadTenderByParkingSessionPayload(string ParkingSessionId);

public sealed record LocalTenderReadbackResponse(
    CashTenderSnapshot? Tender,
    IReadOnlyList<CashTenderEventSnapshot> Events);

public sealed record CentralPmsCashSubmissionPayload(Guid LocalCashTenderId);

public sealed record CentralPmsCashFiscalPayload(Guid LocalCashTenderId);

public sealed record CentralPmsCashReceiptPayload(Guid LocalCashTenderId);

public sealed record CentralPmsCashReceiptFiscalDocumentPayload(Guid FiscalDocumentId);

public sealed record CentralPmsCashReceiptPrintJobPayload(Guid PrintJobId);

public sealed record CentralPmsCashReceiptRecentPrintHistoryPayload(int? MaxResults = null);

public sealed record CentralPmsCashSubmissionStatusResponse(
    bool Enabled,
    bool ConfigurationValid,
    string ConfigurationMessage,
    CentralPmsCashSubmissionCommandSnapshot? Command);

public sealed record CentralPmsCashSubmissionCommandSnapshot(
    Guid LocalCommandId,
    Guid TerminalCashTenderId,
    Guid CashCustodySessionId,
    TerminalCashPaymentCommandStatus Status,
    string StatusLabel,
    int AttemptCount,
    string OriginalCorrelationId,
    string? ResultClassification,
    Guid? CanonicalPaymentAttemptId,
    Guid? CanonicalPaymentConfirmationId,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? NextRetryAt,
    int? LastSafeHttpStatus,
    string? LastSafeErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CentralPmsCashSubmissionCommandSnapshot FromEntity(TerminalCashPaymentOutboxCommand command) =>
        new(
            command.Id,
            command.TerminalCashTenderId,
            command.CashCustodySessionId,
            command.Status,
            command.Status switch
            {
                TerminalCashPaymentCommandStatus.Pending => "Pending",
                TerminalCashPaymentCommandStatus.Submitting => "Submitting",
                TerminalCashPaymentCommandStatus.ReadbackRequired => "Readback required",
                TerminalCashPaymentCommandStatus.RetryPending => "Retry pending",
                TerminalCashPaymentCommandStatus.Confirmed => "Confirmed",
                TerminalCashPaymentCommandStatus.Conflict => "Conflict",
                TerminalCashPaymentCommandStatus.Rejected => "Rejected",
                _ => command.Status.ToString()
            },
            command.AttemptCount,
            command.OriginalCorrelationId,
            command.ResultClassification,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            command.ConfirmedAt,
            command.NextRetryAt,
            command.LastSafeHttpStatus,
            command.LastSafeErrorCode,
            command.CreatedAt,
            command.UpdatedAt);
}

public sealed record CentralPmsCashFiscalStatusResponse(
    bool Enabled,
    bool ConfigurationValid,
    string ConfigurationMessage,
    CentralPmsCashFiscalCommandSnapshot? Command);

public sealed record CentralPmsCashFiscalCommandSnapshot(
    Guid LocalFiscalCommandId,
    Guid TerminalCashTenderId,
    Guid RelatedCashPaymentOutboxCommandId,
    Guid CanonicalPaymentAttemptId,
    Guid CanonicalPaymentConfirmationId,
    TerminalCashFiscalCommandStatus Status,
    string StatusLabel,
    int AttemptCount,
    string FiscalCorrelationId,
    string? ResultClassification,
    Guid? FiscalIssuanceReferenceId,
    string? FiscalIssuanceState,
    Guid? PosFiscalDocumentId,
    string? FiscalDocumentNumber,
    DateTimeOffset? FiscalNumberAssignedAt,
    string? SemanticHashSourceVersion,
    DateTimeOffset? RecordedAt,
    DateTimeOffset? NextRetryAt,
    int? LastSafeHttpStatus,
    string? LastSafeErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CentralPmsCashFiscalCommandSnapshot FromEntity(TerminalCashFiscalOutboxCommand command) =>
        new(
            command.Id,
            command.TerminalCashTenderId,
            command.RelatedCashPaymentOutboxCommandId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            command.Status,
            command.Status switch
            {
                TerminalCashFiscalCommandStatus.Pending => "Pending",
                TerminalCashFiscalCommandStatus.Submitting => "Submitting",
                TerminalCashFiscalCommandStatus.ReadbackRequired => "Readback required",
                TerminalCashFiscalCommandStatus.RetryPending => "Retry pending",
                TerminalCashFiscalCommandStatus.Recorded => "Recorded",
                TerminalCashFiscalCommandStatus.Conflict => "Conflict",
                TerminalCashFiscalCommandStatus.Rejected => "Rejected",
                TerminalCashFiscalCommandStatus.Unknown => "Unknown",
                _ => command.Status.ToString()
            },
            command.AttemptCount,
            command.FiscalCorrelationId,
            command.ResultClassification,
            command.FiscalIssuanceReferenceId,
            command.FiscalIssuanceState,
            command.PosFiscalDocumentId,
            command.FiscalDocumentNumber,
            command.FiscalNumberAssignedAt,
            command.SemanticHashSourceVersion,
            command.RecordedAt,
            command.NextRetryAt,
            command.LastSafeHttpStatus,
            command.LastSafeErrorCode,
            command.CreatedAt,
            command.UpdatedAt);
}

public sealed record CentralPmsCashReceiptStatusResponse(
    bool Enabled,
    bool ConfigurationValid,
    string ConfigurationMessage,
    CentralPmsCashReceiptCommandSnapshot? Command);

public sealed record CentralPmsCashReceiptPreviewResponse(
    bool Enabled,
    CentralPmsCashReceiptCommandSnapshot Command,
    ReceiptPreviewDocument Preview,
    ReceiptPreviewPaperProfile PaperProfile,
    string? PaperWidthWarning);

public sealed record CentralPmsCashReceiptPreviewBlockedDetail(
    CentralPmsCashReceiptCommandSnapshot Command,
    ReceiptPreviewPaperProfile PaperProfile,
    string? PaperWidthWarning);

public sealed record CentralPmsCashReceiptPrintStatusResponse(
    bool Enabled,
    bool ConfigurationValid,
    string ConfigurationMessage,
    CentralPmsCashReceiptCommandSnapshot? Command,
    IReadOnlyList<CentralPmsCashReceiptPrintJobSnapshot> Jobs);

public sealed record CentralPmsCashReceiptPrintSubmitResponse(
    CentralPmsCashReceiptPrintJobSnapshot Job,
    ReceiptPrintDocument PrintDocument,
    string SafeMessage);

public sealed record CentralPmsCashReceiptPrintHistoryResponse(
    string Scope,
    CentralPmsCashReceiptPrintHistorySummary Summary,
    IReadOnlyList<CentralPmsCashReceiptPrintJobSnapshot> Jobs,
    IReadOnlyList<CentralPmsCashReceiptPrintHistoryIndicator> Indicators)
{
    public static CentralPmsCashReceiptPrintHistoryResponse FromJobs(
        string scope,
        IReadOnlyList<TerminalCashReceiptPrintJob> jobs) =>
        new(
            scope,
            CentralPmsCashReceiptPrintHistorySummary.FromJobs(jobs),
            jobs.Select(CentralPmsCashReceiptPrintJobSnapshot.FromEntity).ToArray(),
            CentralPmsCashReceiptPrintHistoryIndicator.FromJobs(jobs));
}

public sealed record CentralPmsCashReceiptPrintHistoryDetailResponse(
    CentralPmsCashReceiptPrintJobSnapshot Job,
    string StatusExplanation,
    string? ShortAuthoritativePayloadHash,
    string? ShortSemanticRequestHash,
    IReadOnlyList<CentralPmsCashReceiptPrintHistoryIndicator> Indicators)
{
    public static CentralPmsCashReceiptPrintHistoryDetailResponse FromJob(TerminalCashReceiptPrintJob job) =>
        new(
            CentralPmsCashReceiptPrintJobSnapshot.FromEntity(job),
            job.Status switch
            {
                TerminalCashReceiptPrintJobStatus.SubmittedToSpooler => "The Sales Invoice print attempt was accepted by the Windows printer queue. Physical paper output is not separately confirmed by this local evidence.",
                TerminalCashReceiptPrintJobStatus.Completed => "The local printer subsystem reported completion for this Sales Invoice print attempt.",
                TerminalCashReceiptPrintJobStatus.UnknownAfterRestart => "Submission had started before restart and the final printer result requires confirmation. This view will not resubmit the job.",
                TerminalCashReceiptPrintJobStatus.PrinterUnavailable => "The configured printer was unavailable. Receipt and fiscal records were not changed.",
                TerminalCashReceiptPrintJobStatus.SpoolerSubmissionFailed => "Windows printer submission failed. Receipt and fiscal records were not changed.",
                TerminalCashReceiptPrintJobStatus.PreparationFailed => "The stored authoritative presentation could not be prepared for printing.",
                TerminalCashReceiptPrintJobStatus.Requested => "Print was requested and persisted before printer submission.",
                TerminalCashReceiptPrintJobStatus.Preparing => "The Sales Invoice was being prepared for printer submission.",
                TerminalCashReceiptPrintJobStatus.SubmissionPending => "The Sales Invoice was being sent to the Windows printer queue.",
                _ => "Local print attempt evidence is available."
            },
            ShortHash(job.AuthoritativePayloadHash),
            ShortHash(job.SemanticRequestHash),
            CentralPmsCashReceiptPrintHistoryIndicator.FromJobs(new[] { job }));

    private static string? ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 24 ? trimmed : $"{trimmed[..24]}...";
    }
}

public sealed record CentralPmsCashReceiptPrintHistorySummary(
    bool HasHistory,
    string OriginalStatus,
    int ReprintCount,
    int? LatestCopySequence,
    string LatestStatus,
    string? LatestPrinterName,
    int? LatestPaperWidthMm,
    DateTimeOffset? LatestAttemptAt,
    bool RequiresConfirmation,
    bool AttentionRequired)
{
    public static CentralPmsCashReceiptPrintHistorySummary FromJobs(IReadOnlyList<TerminalCashReceiptPrintJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return new(
                false,
                "No print attempts recorded",
                0,
                null,
                "No print attempts recorded",
                null,
                null,
                null,
                false,
                false);
        }

        var latest = jobs.OrderByDescending(job => job.RequestedAt).ThenByDescending(job => job.LastUpdatedAt).First();
        var original = jobs
            .Where(job => job.Classification == TerminalCashReceiptPrintClassification.Original)
            .OrderByDescending(job => job.RequestedAt)
            .FirstOrDefault();
        var indicators = CentralPmsCashReceiptPrintHistoryIndicator.FromJobs(jobs);

        return new(
            true,
            original is null ? "No original print attempt recorded" : CentralPmsCashReceiptPrintJobSnapshot.FromEntity(original).StatusLabel,
            jobs.Count(job => job.Classification == TerminalCashReceiptPrintClassification.Reprint),
            latest.CopySequence,
            CentralPmsCashReceiptPrintJobSnapshot.FromEntity(latest).StatusLabel,
            latest.ConfiguredPrinterName,
            latest.PaperWidthMm,
            latest.RequestedAt,
            jobs.Any(job => job.Status == TerminalCashReceiptPrintJobStatus.UnknownAfterRestart),
            indicators.Any(indicator => string.Equals(indicator.Severity, "attention", StringComparison.Ordinal)));
    }
}

public sealed record CentralPmsCashReceiptPrintHistoryIndicator(
    string Code,
    string Label,
    string Severity,
    string Message)
{
    private static readonly TerminalCashReceiptPrintJobStatus[] SpoolerAcceptedOrUnknown =
    [
        TerminalCashReceiptPrintJobStatus.SubmittedToSpooler,
        TerminalCashReceiptPrintJobStatus.Completed,
        TerminalCashReceiptPrintJobStatus.UnknownAfterRestart
    ];

    public static IReadOnlyList<CentralPmsCashReceiptPrintHistoryIndicator> FromJobs(IReadOnlyList<TerminalCashReceiptPrintJob> jobs)
    {
        var indicators = new List<CentralPmsCashReceiptPrintHistoryIndicator>();
        if (jobs.Count == 0)
        {
            indicators.Add(new("NO_PRINT_HISTORY", "No print attempts recorded", "info", "No local Sales Invoice print attempt is recorded for this scope."));
            return indicators;
        }

        if (jobs.Any(job => job.Status == TerminalCashReceiptPrintJobStatus.UnknownAfterRestart))
        {
            indicators.Add(new("PRINT_RESULT_REQUIRES_CONFIRMATION", "Print result requires confirmation", "attention", "A print submission was interrupted and the terminal will not silently resubmit it."));
        }

        var latest = jobs.OrderByDescending(job => job.RequestedAt).ThenByDescending(job => job.LastUpdatedAt).First();
        if (latest.Retryable && latest.Status is TerminalCashReceiptPrintJobStatus.PrinterUnavailable or TerminalCashReceiptPrintJobStatus.SpoolerSubmissionFailed)
        {
            indicators.Add(new("LATEST_RETRYABLE_FAILURE", "Latest attempt failed", "attention", "The latest local print attempt failed retryably. This history view does not retry it."));
        }

        if (!jobs.Any(job => job.Classification == TerminalCashReceiptPrintClassification.Original && SpoolerAcceptedOrUnknown.Contains(job.Status)))
        {
            indicators.Add(new("NO_ORIGINAL_SPOOLER_ACCEPTANCE", "No original submitted evidence", "attention", "No original print attempt has spooler-accepted or unknown-after-submission local evidence."));
        }
        else
        {
            indicators.Add(new("ORIGINAL_SUBMITTED", "Original submitted", "info", "At least one original print attempt has local spooler or unknown-after-submission evidence."));
        }

        var reprintCount = jobs.Count(job => job.Classification == TerminalCashReceiptPrintClassification.Reprint);
        if (reprintCount > 0)
        {
            indicators.Add(new("REPRINT_COUNT", $"Reprint count: {reprintCount}", "info", "Reprint attempts remain linked to the same fiscal document evidence."));
        }

        if (jobs.Select(job => job.ConfiguredPrinterName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            indicators.Add(new("PRINTER_CHANGED_BETWEEN_COPIES", "Printer changed between copies", "attention", "Different printer queues are recorded across this copy chain."));
        }

        if (jobs.Select(job => job.PaperWidthMm).Distinct().Count() > 1)
        {
            indicators.Add(new("PAPER_WIDTH_CHANGED_BETWEEN_COPIES", "Paper width changed between copies", "attention", "Different paper widths are recorded across this copy chain."));
        }

        if (jobs.GroupBy(job => job.CopySequence).Any(group => group.Count() > 1))
        {
            indicators.Add(new("DUPLICATE_COPY_SEQUENCE", "Duplicate copy sequence", "attention", "More than one local print attempt uses the same copy sequence."));
        }

        if (jobs.Any(job => job.Classification == TerminalCashReceiptPrintClassification.Reprint)
            && !jobs.Any(job => job.Classification == TerminalCashReceiptPrintClassification.Original && SpoolerAcceptedOrUnknown.Contains(job.Status)))
        {
            indicators.Add(new("REPRINT_WITHOUT_ORIGINAL_BOUNDARY", "Reprint without original boundary", "attention", "A reprint exists without local evidence that the original boundary was consumed."));
        }

        if (jobs.Select(job => job.PosFiscalDocumentId).Distinct().Count() > 1)
        {
            indicators.Add(new("INCONSISTENT_FISCAL_DOCUMENT_IDENTITY", "Inconsistent fiscal document identity", "attention", "Print attempts in this view do not all reference the same fiscal document."));
        }

        if (jobs.Select(job => job.AuthoritativePayloadHash).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            indicators.Add(new("INCONSISTENT_PRESENTATION_HASH", "Inconsistent presentation hash", "attention", "Print attempts in this view do not all reference the same authoritative presentation hash."));
        }

        if (jobs.Any(job => job.PosFiscalDocumentId == Guid.Empty || string.IsNullOrWhiteSpace(job.FiscalDocumentNumber)))
        {
            indicators.Add(new("MISSING_FISCAL_IDENTITY", "Missing fiscal identity", "attention", "One or more print attempts are missing fiscal document evidence."));
        }

        if (jobs.Any(job => string.IsNullOrWhiteSpace(job.ConfiguredPrinterName)))
        {
            indicators.Add(new("MISSING_PRINTER_EVIDENCE", "Missing printer evidence", "attention", "One or more print attempts are missing configured printer evidence."));
        }

        if (jobs.Any(job => job.Status == TerminalCashReceiptPrintJobStatus.SubmittedToSpooler && job.SubmittedToSpoolerAt is null))
        {
            indicators.Add(new("SUBMITTED_TIMESTAMP_MISSING", "Submitted timestamp missing", "attention", "A submitted attempt is missing its local spooler-submission timestamp."));
        }

        if (indicators.Count == 0)
        {
            indicators.Add(new("PRINT_HISTORY_COMPLETE", "Print history complete for local journal", "info", "No local print-history attention condition was detected."));
        }

        return indicators;
    }
}

public sealed record CentralPmsCashReceiptPrintJobSnapshot(
    Guid PrintJobId,
    Guid TerminalCashTenderId,
    Guid LocalReceiptRetrievalId,
    Guid FiscalIssuanceReferenceId,
    Guid PosFiscalDocumentId,
    string FiscalDocumentNumber,
    string PresentationVersion,
    string TemplateVersion,
    string AuthoritativePayloadHash,
    string? SemanticRequestHash,
    int PaperWidthMm,
    string PaperProfileId,
    string ConfiguredPrinterName,
    TerminalCashReceiptPrintClassification Classification,
    string ClassificationLabel,
    int CopySequence,
    TerminalCashReceiptPrintJobStatus Status,
    string StatusLabel,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    DateTimeOffset? SubmissionStartedAt,
    DateTimeOffset? SubmittedToSpoolerAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt,
    string? FailureClassification,
    bool Retryable,
    string? WindowsSpoolerJobId,
    DateTimeOffset LastUpdatedAt,
    string CorrelationId)
{
    public static CentralPmsCashReceiptPrintJobSnapshot FromEntity(TerminalCashReceiptPrintJob job) =>
        new(
            job.Id,
            job.TerminalCashTenderId,
            job.LocalReceiptRetrievalId,
            job.FiscalIssuanceReferenceId,
            job.PosFiscalDocumentId,
            job.FiscalDocumentNumber,
            job.PresentationVersion,
            job.TemplateVersion,
            job.AuthoritativePayloadHash,
            job.SemanticRequestHash,
            job.PaperWidthMm,
            job.PaperProfileId,
            job.ConfiguredPrinterName,
            job.Classification,
            job.Classification == TerminalCashReceiptPrintClassification.Reprint ? "Reprint" : "Original",
            job.CopySequence,
            job.Status,
            job.Status switch
            {
                TerminalCashReceiptPrintJobStatus.Requested => "Print requested",
                TerminalCashReceiptPrintJobStatus.Preparing => "Preparing Sales Invoice",
                TerminalCashReceiptPrintJobStatus.PrinterUnavailable => "Printer unavailable",
                TerminalCashReceiptPrintJobStatus.PreparationFailed => "Print preparation failed",
                TerminalCashReceiptPrintJobStatus.SubmissionPending => "Sending to printer",
                TerminalCashReceiptPrintJobStatus.SubmittedToSpooler => "Submitted to printer",
                TerminalCashReceiptPrintJobStatus.SpoolerSubmissionFailed => "Print failed",
                TerminalCashReceiptPrintJobStatus.UnknownAfterRestart => "Print result requires confirmation",
                TerminalCashReceiptPrintJobStatus.Completed => "Printed",
                _ => job.Status.ToString()
            },
            job.RequestedAt,
            job.RequestedBy,
            job.SubmissionStartedAt,
            job.SubmittedToSpoolerAt,
            job.CompletedAt,
            job.FailedAt,
            job.FailureClassification,
            job.Retryable,
            job.WindowsSpoolerJobId,
            job.LastUpdatedAt,
            job.CorrelationId);
}

public sealed record CentralPmsCashReceiptCommandSnapshot(
    Guid LocalReceiptRetrievalId,
    Guid TerminalCashTenderId,
    Guid RelatedCashPaymentOutboxCommandId,
    Guid RelatedFiscalCommandId,
    Guid CanonicalPaymentAttemptId,
    Guid CanonicalPaymentConfirmationId,
    string? CanonicalPaymentStatus,
    Guid FiscalIssuanceReferenceId,
    Guid PosFiscalDocumentId,
    TerminalCashReceiptRetrievalStatus Status,
    string StatusLabel,
    int AttemptCount,
    string RetrievalCorrelationId,
    string? ResultClassification,
    string? ReceiptAvailabilityState,
    string? FiscalDocumentNumber,
    string? FiscalDocumentStatus,
    string? PresentationVersion,
    string? TemplateVersion,
    string? SemanticRequestHash,
    string? SemanticRequestHashVersion,
    string? SemanticRequestHashStatus,
    string? ContentType,
    string? AuthoritativePayloadHash,
    string? VoidStatus,
    string? VoidReasonCode,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? RetrievedAt,
    DateTimeOffset? NextRetryAt,
    int? LastSafeHttpStatus,
    string? LastSafeErrorCode,
    bool? LastRetryable,
    string? LastCentralPmsCorrelationId,
    DateTimeOffset? LastUpdatedFromCentralPms,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CentralPmsCashReceiptCommandSnapshot FromEntity(TerminalCashReceiptRetrievalCommand command) =>
        new(
            command.Id,
            command.TerminalCashTenderId,
            command.RelatedCashPaymentOutboxCommandId,
            command.RelatedFiscalCommandId,
            command.CanonicalPaymentAttemptId,
            command.CanonicalPaymentConfirmationId,
            command.CanonicalPaymentStatus,
            command.FiscalIssuanceReferenceId,
            command.PosFiscalDocumentId,
            command.Status,
            command.Status switch
            {
                TerminalCashReceiptRetrievalStatus.Pending => "Pending",
                TerminalCashReceiptRetrievalStatus.Retrieving => "Retrieving",
                TerminalCashReceiptRetrievalStatus.NotReady => "Not ready",
                TerminalCashReceiptRetrievalStatus.RetryPending => "Retry pending",
                TerminalCashReceiptRetrievalStatus.Available => "Available",
                TerminalCashReceiptRetrievalStatus.Voided => "Voided",
                TerminalCashReceiptRetrievalStatus.Rejected => "Rejected",
                TerminalCashReceiptRetrievalStatus.Inconsistent => "Inconsistent",
                TerminalCashReceiptRetrievalStatus.Unavailable => "Unavailable",
                TerminalCashReceiptRetrievalStatus.Unsupported => "Unsupported",
                TerminalCashReceiptRetrievalStatus.Malformed => "Malformed",
                _ => command.Status.ToString()
            },
            command.AttemptCount,
            command.RetrievalCorrelationId,
            command.ResultClassification,
            command.ReceiptAvailabilityState,
            command.FiscalDocumentNumber,
            command.FiscalDocumentStatus,
            command.PresentationVersion,
            command.TemplateVersion,
            command.SemanticRequestHash,
            command.SemanticRequestHashVersion,
            command.SemanticRequestHashStatus,
            command.ContentType,
            command.AuthoritativePayloadHash,
            command.VoidStatus,
            command.VoidReasonCode,
            command.VoidedAt,
            command.RetrievedAt,
            command.NextRetryAt,
            command.LastSafeHttpStatus,
            command.LastSafeErrorCode,
            command.LastRetryable,
            command.LastCentralPmsCorrelationId,
            command.LastUpdatedFromCentralPms,
            command.CreatedAt,
            command.UpdatedAt);
}

public sealed record SavePayableBasisStatePayload(
    string LocalWorkflowId,
    string LookupReferenceType,
    string LookupReferenceValue,
    string ParkingSessionId,
    string TariffSnapshotId,
    string SiteId,
    string SiteGroupId,
    string? SitePosServerId,
    string TerminalId,
    long AuthoritativeAmountMinorUnits,
    string Currency,
    DateTimeOffset? TariffCalculatedAt,
    DateTimeOffset TariffValidUntil,
    DateTimeOffset? FeeValidUntil,
    string ParkingStatus,
    string PaymentStatus,
    string? SessionReadiness,
    string? TariffReadiness,
    string? PaymentEligibility,
    string? TerminalCashAvailability,
    string? FiscalReadiness,
    string? SalesInvoiceConfigurationReadiness,
    string? CashAcceptanceReadiness,
    bool ReadyForCashAcceptance,
    IReadOnlyList<string> BlockingReasonCodes,
    bool Retryable,
    string SafeUserFacingClassification,
    string CentralPmsCorrelationId,
    string? RevalidationOutcome,
    bool CashierAcknowledgementRequired,
    bool AmountChanged,
    long? PriorDisplayedAmountMinorUnits);

public sealed record GetLatestPayableBasisStatePayload(
    string TerminalId,
    string SiteId);
