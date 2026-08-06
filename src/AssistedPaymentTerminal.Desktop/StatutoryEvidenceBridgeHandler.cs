using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public sealed class StatutoryEvidenceBridgeHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> AllowedCommands =
    [
        StatutoryEvidenceBridgeCommand.Bootstrap,
        StatutoryEvidenceBridgeCommand.Status,
        StatutoryEvidenceBridgeCommand.Revalidate,
        StatutoryEvidenceBridgeCommand.SelectFile,
        StatutoryEvidenceBridgeCommand.CreateUploadSession,
        StatutoryEvidenceBridgeCommand.Upload,
        StatutoryEvidenceBridgeCommand.CancelUpload,
        StatutoryEvidenceBridgeCommand.Finalize
    ];

    private readonly ICentralPmsStatutoryEvidenceClient _client;
    private readonly IStatutoryEvidenceFilePicker _filePicker;
    private readonly ConcurrentDictionary<Guid, SelectedFileState> _selectedFiles = new();
    private readonly ConcurrentDictionary<Guid, UploadState> _uploadSessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeUploads = new();

    public StatutoryEvidenceBridgeHandler(
        ICentralPmsStatutoryEvidenceClient client,
        IStatutoryEvidenceFilePicker? filePicker = null)
    {
        _client = client;
        _filePicker = filePicker ?? new WindowsStatutoryEvidenceFilePicker();
    }

    public async Task<string?> HandleWebMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        StatutoryEvidenceBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<StatutoryEvidenceBridgeRequest>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null || !string.Equals(request.Source, StatutoryEvidenceBridgeCommand.Source, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId) || !Guid.TryParse(request.CorrelationId, out var correlationId) || correlationId == Guid.Empty)
        {
            return Failure(request.Command, request.CorrelationId ?? string.Empty, "MISSING_CORRELATION_ID", "The evidence operation requires a correlation reference.", false);
        }

        if (!AllowedCommands.Contains(request.Command))
        {
            return Failure(request.Command, request.CorrelationId, "UNSUPPORTED_COMMAND", "The requested evidence operation is not supported.", false);
        }

        try
        {
            return request.Command switch
            {
                StatutoryEvidenceBridgeCommand.Bootstrap => await BootstrapAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.Status => await StatusAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.Revalidate => await RevalidateAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.SelectFile => await SelectFileAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.CreateUploadSession => await CreateUploadSessionAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.Upload => await UploadAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                StatutoryEvidenceBridgeCommand.CancelUpload => CancelUpload(request),
                StatutoryEvidenceBridgeCommand.Finalize => await FinalizeAsync(request, correlationId, cancellationToken).ConfigureAwait(false),
                _ => Failure(request.Command, request.CorrelationId, "UNSUPPORTED_COMMAND", "The requested evidence operation is not supported.", false)
            };
        }
        catch (IOException)
        {
            return Failure(request.Command, request.CorrelationId, "LOCAL_FILE_UNAVAILABLE", "The selected evidence file is no longer available. Select the file again.", true);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(request.Command, request.CorrelationId, "LOCAL_FILE_ACCESS_DENIED", "The selected evidence file cannot be read. Select an accessible JPEG or PNG file.", false);
        }
        catch (JsonException)
        {
            return Failure(request.Command, request.CorrelationId, "MALFORMED_REQUEST", "The evidence operation request was malformed.", false);
        }
        catch (Exception)
        {
            return Failure(request.Command, request.CorrelationId, "UNEXPECTED_FAILURE", "The evidence operation could not be completed safely.", false);
        }
    }

    private async Task<string> BootstrapAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<DecisionPayload>(request);
        if (!ValidDecision(payload))
        {
            return InvalidDecision(request);
        }

        var result = await _client.BootstrapAsync(payload!.StatutoryDiscountDecisionCommandId, payload.ClientOperationKey, correlationId, cancellationToken).ConfigureAwait(false);
        return ChannelResult(request, result);
    }

    private async Task<string> StatusAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<DecisionPayload>(request);
        if (!ValidDecision(payload))
        {
            return InvalidDecision(request);
        }

        var result = await _client.GetStatusAsync(payload!.StatutoryDiscountDecisionCommandId, correlationId, cancellationToken).ConfigureAwait(false);
        return ChannelResult(request, result);
    }

    private async Task<string> RevalidateAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<DecisionPayload>(request);
        if (!ValidDecision(payload))
        {
            return InvalidDecision(request);
        }

        var result = await _client.RevalidateAsync(payload!.StatutoryDiscountDecisionCommandId, correlationId, cancellationToken).ConfigureAwait(false);
        return ChannelResult(request, result);
    }

    private async Task<string> SelectFileAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<DecisionPayload>(request);
        if (!ValidDecision(payload))
        {
            return InvalidDecision(request);
        }

        var statusResult = await _client.GetStatusAsync(payload!.StatutoryDiscountDecisionCommandId, correlationId, cancellationToken);
        if (!statusResult.Ok || statusResult.Payload is null)
        {
            return ClientFailure(request, statusResult);
        }

        var status = statusResult.Payload;
        if (!CanSelectFile(status))
        {
            return Failure(request.Command, request.CorrelationId, "CAPTURE_NOT_PERMITTED", SafeMessage(status), status.Retryable);
        }

        var candidate = _filePicker.SelectSingleImage();
        if (candidate is null)
        {
            return Success(request, new { cancelled = true });
        }

        var error = ValidateCandidate(candidate, status);
        if (error is not null)
        {
            return Failure(request.Command, request.CorrelationId, error.Value.Code, error.Value.Message, false);
        }

        await using var stream = OpenFile(candidate.Path);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var selectionReference = Guid.NewGuid();
        foreach (var prior in _selectedFiles.Where(pair => pair.Value.DecisionCommandId == payload.StatutoryDiscountDecisionCommandId).ToArray())
        {
            _selectedFiles.TryRemove(prior.Key, out _);
        }

        _selectedFiles[selectionReference] = new SelectedFileState(
            payload.StatutoryDiscountDecisionCommandId,
            candidate.Path,
            candidate.DisplayName,
            candidate.ContentType,
            candidate.Length,
            checksum);

        return Success(request, new
        {
            cancelled = false,
            selectionReference,
            displayName = candidate.DisplayName,
            contentType = candidate.ContentType,
            byteLength = candidate.Length
        });
    }

    private async Task<string> CreateUploadSessionAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<CreateUploadSessionPayload>(request);
        if (payload is null || payload.SelectionReference == Guid.Empty || payload.StatutoryDiscountDecisionCommandId == Guid.Empty ||
            !_selectedFiles.TryGetValue(payload.SelectionReference, out var selected) ||
            selected.DecisionCommandId != payload.StatutoryDiscountDecisionCommandId)
        {
            return Failure(request.Command, request.CorrelationId, "FILE_RESELECTION_REQUIRED", "Select the evidence file again before requesting an upload session.", false);
        }

        var statusResult = await _client.GetStatusAsync(selected.DecisionCommandId, correlationId, cancellationToken).ConfigureAwait(false);
        if (!statusResult.Ok || statusResult.Payload is null)
        {
            return ClientFailure(request, statusResult);
        }

        var status = statusResult.Payload;
        if (!CanSelectFile(status) || status.EvidenceSetReference is null || status.EvidenceItemReference is null)
        {
            return Failure(request.Command, request.CorrelationId, "UPLOAD_NOT_PERMITTED", SafeMessage(status), status.Retryable);
        }

        await VerifySelectedFileAsync(selected, cancellationToken).ConfigureAwait(false);
        var result = await _client.CreateUploadSessionAsync(
            new StatutoryEvidenceUploadSessionRequest(
                status.EvidenceSetReference.Value,
                status.EvidenceItemReference.Value,
                selected.ContentType,
                selected.Length,
                selected.ChecksumSha256,
                payload.ClientOperationKey),
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return ClientFailure(request, result);
        }

        if (result.Payload.Classification is not ("REJECTED" or "SEMANTIC_CONFLICT") &&
            result.Payload.OpaqueUploadSessionReference is Guid uploadReference && uploadReference != Guid.Empty)
        {
            _uploadSessions[uploadReference] = new UploadState(payload.SelectionReference, selected.DecisionCommandId, result.Payload.ExpiresAt);
        }

        return Success(request, ToSafeUploadResponse(result.Payload));
    }

    private async Task<string> UploadAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<UploadPayload>(request);
        if (payload is null || payload.OpaqueUploadSessionReference == Guid.Empty ||
            !_uploadSessions.TryGetValue(payload.OpaqueUploadSessionReference, out var upload) ||
            !_selectedFiles.TryGetValue(upload.SelectionReference, out var selected))
        {
            return Failure(request.Command, request.CorrelationId, "FILE_RESELECTION_REQUIRED", "The upload cannot resume from local state. Reconcile with Central PMS and select the file again.", false);
        }

        if (upload.ExpiresAt is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow)
        {
            return Failure(request.Command, request.CorrelationId, "UPLOAD_SESSION_EXPIRED", "The upload session expired. Request a new session when Central PMS permits it.", true);
        }

        await VerifySelectedFileAsync(selected, cancellationToken).ConfigureAwait(false);
        using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeUploads.TryAdd(payload.OpaqueUploadSessionReference, uploadCancellation))
        {
            return Failure(request.Command, request.CorrelationId, "UPLOAD_ALREADY_IN_PROGRESS", "This evidence upload is already in progress.", false);
        }

        try
        {
            await using var stream = OpenFile(selected.Path);
            var result = await _client.UploadAsync(
                payload.OpaqueUploadSessionReference,
                stream,
                selected.ContentType,
                selected.Length,
                correlationId,
                uploadCancellation.Token).ConfigureAwait(false);
            return result.Ok && result.Payload is not null
                ? Success(request, ToSafeUploadResponse(result.Payload))
                : ClientFailure(request, result);
        }
        finally
        {
            _activeUploads.TryRemove(payload.OpaqueUploadSessionReference, out _);
        }
    }

    private string CancelUpload(StatutoryEvidenceBridgeRequest request)
    {
        var payload = Deserialize<UploadPayload>(request);
        if (payload is null || payload.OpaqueUploadSessionReference == Guid.Empty)
        {
            return Failure(request.Command, request.CorrelationId, "INVALID_UPLOAD_SESSION", "A valid opaque upload session is required.", false);
        }

        var cancelled = _activeUploads.TryGetValue(payload.OpaqueUploadSessionReference, out var cancellation);
        cancellation?.Cancel();
        return Success(request, new
        {
            cancelled,
            reconciliationRequired = true,
            safeMessage = "The local upload attempt was cancelled. Central PMS status must be checked before retrying."
        });
    }

    private async Task<string> FinalizeAsync(StatutoryEvidenceBridgeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var payload = Deserialize<FinalizePayload>(request);
        if (payload is null || payload.OpaqueUploadSessionReference == Guid.Empty)
        {
            return Failure(request.Command, request.CorrelationId, "INVALID_UPLOAD_SESSION", "A valid opaque upload session is required.", false);
        }

        var result = await _client.FinalizeAsync(
            payload.OpaqueUploadSessionReference,
            payload.ClientOperationKey,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return ClientFailure(request, result);
        }

        if (_uploadSessions.TryRemove(payload.OpaqueUploadSessionReference, out var upload))
        {
            _selectedFiles.TryRemove(upload.SelectionReference, out _);
        }

        return Success(request, ToSafeChannelResponse(result.Payload));
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task VerifySelectedFileAsync(SelectedFileState selected, CancellationToken cancellationToken)
    {
        var file = new FileInfo(selected.Path);
        if (!file.Exists || file.Length != selected.Length)
        {
            throw new IOException("Selected file changed.");
        }

        await using var stream = OpenFile(selected.Path);
        var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(currentHash), Convert.FromHexString(selected.ChecksumSha256)))
        {
            throw new IOException("Selected file changed.");
        }
    }

    private static (string Code, string Message)? ValidateCandidate(StatutoryEvidenceFileCandidate candidate, StatutoryEvidenceChannelResponse status)
    {
        if (candidate.Length <= 0)
        {
            return ("EMPTY_FILE", "Select a non-empty JPEG or PNG file.");
        }

        if (string.IsNullOrWhiteSpace(candidate.ContentType) || !status.AllowedContentTypes.Contains(candidate.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return ("UNSUPPORTED_MEDIA_TYPE", "Only the JPEG and PNG media types allowed by Central PMS can be uploaded.");
        }

        if (status.MaximumContentLengthBytes <= 0 || candidate.Length > status.MaximumContentLengthBytes)
        {
            return ("FILE_TOO_LARGE", "The selected image exceeds the maximum size allowed by Central PMS.");
        }

        return null;
    }

    private static bool CanSelectFile(StatutoryEvidenceChannelResponse status)
    {
        if (!status.EvidenceRequired || status.EvidenceSetReference is null || status.EvidenceItemReference is null)
        {
            return false;
        }

        return status.LifecycleClassification is "REQUIRED_NOT_STARTED" or "ITEM_CREATED" or "UPLOAD_SESSION_AVAILABLE" ||
               string.Equals(status.ReplacementPosture, "REPLACEMENT_ALLOWED", StringComparison.Ordinal);
    }

    private static bool ValidDecision(DecisionPayload? payload) =>
        payload is not null && payload.StatutoryDiscountDecisionCommandId != Guid.Empty;

    private static string InvalidDecision(StatutoryEvidenceBridgeRequest request) =>
        Failure(request.Command, request.CorrelationId, "INVALID_DECISION_REFERENCE", "A valid statutory decision reference is required.", false);

    private static T? Deserialize<T>(StatutoryEvidenceBridgeRequest request) =>
        request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : request.Payload.Deserialize<T>(JsonOptions);

    private static string ChannelResult(
        StatutoryEvidenceBridgeRequest request,
        StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse> result) =>
        result.Ok && result.Payload is not null
            ? Success(request, ToSafeChannelResponse(result.Payload))
            : ClientFailure(request, result);

    private static string ClientFailure<T>(StatutoryEvidenceBridgeRequest request, StatutoryEvidenceClientResult<T> result) =>
        Failure(
            request.Command,
            request.CorrelationId,
            result.ErrorCode ?? "UNEXPECTED_FAILURE",
            result.SafeMessage ?? "The evidence operation could not be completed safely.",
            result.Retryable);

    private static object ToSafeChannelResponse(StatutoryEvidenceChannelResponse response) => new
    {
        response.Classification,
        response.Retryable,
        response.ErrorCode,
        response.CorrelationId,
        response.SourceChannel,
        response.EvidenceRequired,
        response.EvidenceSetReference,
        response.EvidenceItemReference,
        response.AllowedContentTypes,
        response.MaximumContentLengthBytes,
        response.MaximumImageWidth,
        response.MaximumImageHeight,
        response.MaximumImagePixelCount,
        response.RequiredDocumentType,
        response.RequiredItemRole,
        response.LifecycleClassification,
        response.ReplacementPosture,
        response.ReadyForReview,
        response.ReadyForAptPreCash,
        response.BlockingReasonCode,
        response.EvaluatedAt,
        safeMessage = SafeMessage(response)
    };

    private static object ToSafeUploadResponse(StatutoryEvidenceUploadSessionResponse response) => new
    {
        response.Classification,
        response.Retryable,
        response.ErrorCode,
        response.CorrelationId,
        response.OpaqueUploadSessionReference,
        response.Method,
        response.ExpiresAt,
        response.AcceptedContentType,
        response.MaximumContentLengthBytes,
        safeMessage = response.Classification switch
        {
            "REJECTED" => "Central PMS rejected the evidence upload operation.",
            "SEMANTIC_CONFLICT" => "The evidence upload request conflicts with authoritative state. Refresh before retrying.",
            _ => "Central PMS accepted the evidence upload operation."
        }
    };

    private static string SafeMessage(StatutoryEvidenceChannelResponse response) => response.LifecycleClassification switch
    {
        "NOT_REQUIRED" => "Statutory evidence is not required for this request.",
        "REQUIRED_NOT_STARTED" => "Statutory evidence is required. Select a JPEG or PNG image.",
        "ITEM_CREATED" => "The evidence item is ready for capture.",
        "UPLOAD_SESSION_AVAILABLE" => "An evidence upload session is available.",
        "UPLOAD_IN_PROGRESS" => "Evidence upload is in progress.",
        "UPLOADED" => "Evidence was uploaded and must be finalized.",
        "VALIDATION_PENDING" => "Evidence validation is pending.",
        "VALIDATION_FAILED" => "Evidence validation failed. Cash remains blocked.",
        "SCAN_PENDING" => "Evidence security scanning is pending.",
        "SCAN_RETRYABLE" => "Evidence security scanning is temporarily unavailable.",
        "SCAN_FAILED" => "Evidence security scanning could not be completed safely.",
        "MALWARE_DETECTED" => "The evidence was rejected by security scanning.",
        "NOT_REVIEWABLE" => "The evidence is not reviewable.",
        "REVIEWABLE" => "The evidence is ready for authorized review.",
        "REVIEW_PENDING" => "Authorized review is pending.",
        "APPROVED" => "The statutory request is approved but the payable basis is not yet applied.",
        "REJECTED" => "The statutory request was rejected.",
        "APPLIED" => "Evidence and the statutory payable basis are applied.",
        _ => response.EvidenceRequired
            ? "Central PMS could not establish a safe evidence state. Cash remains blocked."
            : "Central PMS evidence state is unavailable."
    };

    private static string Success(StatutoryEvidenceBridgeRequest request, object payload) =>
        JsonSerializer.Serialize(new
        {
            source = StatutoryEvidenceBridgeCommand.Source,
            request.Command,
            request.CorrelationId,
            ok = true,
            payload
        }, JsonOptions);

    private static string Failure(string command, string correlationId, string code, string message, bool retryable) =>
        JsonSerializer.Serialize(new
        {
            source = StatutoryEvidenceBridgeCommand.Source,
            command,
            correlationId,
            ok = false,
            error = new { code, message, retryable }
        }, JsonOptions);

    private sealed record SelectedFileState(
        Guid DecisionCommandId,
        string Path,
        string DisplayName,
        string ContentType,
        long Length,
        string ChecksumSha256);

    private sealed record UploadState(Guid SelectionReference, Guid DecisionCommandId, DateTimeOffset? ExpiresAt);
}

public sealed record StatutoryEvidenceBridgeRequest(
    string Source,
    string Command,
    string CorrelationId,
    JsonElement Payload);

public sealed record DecisionPayload(Guid StatutoryDiscountDecisionCommandId, string? ClientOperationKey);
public sealed record CreateUploadSessionPayload(Guid StatutoryDiscountDecisionCommandId, Guid SelectionReference, string? ClientOperationKey);
public sealed record UploadPayload(Guid OpaqueUploadSessionReference);
public sealed record FinalizePayload(Guid OpaqueUploadSessionReference, string? ClientOperationKey);
