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
        LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback,
        LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback,
        LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus,
        LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck,
        LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview
    ];

    private readonly CashJournalService _journal;
    private readonly bool _enabled;
    private readonly bool _centralPmsCashSubmissionEnabled;
    private readonly bool _centralPmsFiscalIssuanceEnabled;
    private readonly bool _centralPmsReceiptRetrievalEnabled;
    private readonly bool _receiptPreviewEnabled;
    private readonly string? _centralPmsBaseUrl;
    private readonly ReceiptPreviewPaperSelection _receiptPaperSelection;
    private readonly TerminalCashPaymentSubmissionService _submissionService;
    private readonly TerminalCashFiscalSubmissionService _fiscalService;
    private readonly TerminalCashReceiptRetrievalService _receiptService;

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
        TerminalCashReceiptRetrievalService? receiptService = null)
    {
        _journal = journal;
        _enabled = enabled;
        _centralPmsCashSubmissionEnabled = centralPmsCashSubmissionEnabled;
        _centralPmsFiscalIssuanceEnabled = centralPmsFiscalIssuanceEnabled;
        _centralPmsReceiptRetrievalEnabled = centralPmsReceiptRetrievalEnabled;
        _receiptPreviewEnabled = receiptPreviewEnabled;
        _receiptPaperSelection = ReceiptPreviewPaperProfiles.Select(receiptPaperWidthMm);
        _centralPmsBaseUrl = string.IsNullOrWhiteSpace(centralPmsBaseUrl) ? null : centralPmsBaseUrl.Trim();
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
                LocalJournalBridgeCommand.CentralPmsCashSubmissionGetStatus => await GetCentralPmsCashSubmissionStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback => await SubmitOrReadbackCentralPmsCashSubmissionAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashFiscalGetStatus => await GetCentralPmsCashFiscalStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashFiscalSubmitOrReadback => await SubmitOrReadbackCentralPmsCashFiscalAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptGetStatus => await GetCentralPmsCashReceiptStatusAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptRetrieveOrCheck => await RetrieveOrCheckCentralPmsCashReceiptAsync(request, cancellationToken).ConfigureAwait(false),
                LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview => await GetCentralPmsCashReceiptPreviewAsync(request, cancellationToken).ConfigureAwait(false),
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
            LocalIdempotencyIdentity: payload.LocalIdempotencyIdentity), cancellationToken).ConfigureAwait(false);

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

public sealed record CentralPmsCashReceiptCommandSnapshot(
    Guid LocalReceiptRetrievalId,
    Guid TerminalCashTenderId,
    Guid RelatedCashPaymentOutboxCommandId,
    Guid RelatedFiscalCommandId,
    Guid CanonicalPaymentAttemptId,
    Guid CanonicalPaymentConfirmationId,
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
    string? ContentType,
    string? AuthoritativePayloadHash,
    string? VoidStatus,
    string? VoidReasonCode,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? RetrievedAt,
    DateTimeOffset? NextRetryAt,
    int? LastSafeHttpStatus,
    string? LastSafeErrorCode,
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
            command.ContentType,
            command.AuthoritativePayloadHash,
            command.VoidStatus,
            command.VoidReasonCode,
            command.VoidedAt,
            command.RetrievedAt,
            command.NextRetryAt,
            command.LastSafeHttpStatus,
            command.LastSafeErrorCode,
            command.CreatedAt,
            command.UpdatedAt);
}
