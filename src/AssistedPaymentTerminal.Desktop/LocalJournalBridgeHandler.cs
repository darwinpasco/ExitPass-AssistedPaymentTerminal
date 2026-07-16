using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssistedPaymentTerminal.LocalOperations;

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
        LocalJournalBridgeCommand.CentralPmsCashSubmissionSubmitOrReadback
    ];

    private readonly CashJournalService _journal;
    private readonly bool _enabled;
    private readonly bool _centralPmsCashSubmissionEnabled;
    private readonly string? _centralPmsBaseUrl;
    private readonly TerminalCashPaymentSubmissionService _submissionService;

    public LocalJournalBridgeHandler(
        CashJournalService journal,
        bool enabled,
        bool centralPmsCashSubmissionEnabled = false,
        string? centralPmsBaseUrl = null,
        TerminalCashPaymentSubmissionService? submissionService = null)
    {
        _journal = journal;
        _enabled = enabled;
        _centralPmsCashSubmissionEnabled = centralPmsCashSubmissionEnabled;
        _centralPmsBaseUrl = string.IsNullOrWhiteSpace(centralPmsBaseUrl) ? null : centralPmsBaseUrl.Trim();
        _submissionService = submissionService ?? new TerminalCashPaymentSubmissionService(
            new CentralPmsTerminalCashPaymentClient(new HttpClient()),
            new LocalOperationsDatabaseOptions(
                journal.DatabasePath,
                CentralPmsBaseUrl: _centralPmsBaseUrl ?? "UNCONFIGURED_CENTRAL_PMS",
                EnableCentralPmsCashSubmission: centralPmsCashSubmissionEnabled));
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
                _ => SerializeFailure(request.Command, request.CorrelationId, "unsupported_command", "Unsupported local journal bridge command.")
            };
        }
        catch (JsonException)
        {
            return SerializeFailure(request.Command, request.CorrelationId, "malformed_payload", "Malformed local journal bridge payload.");
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
