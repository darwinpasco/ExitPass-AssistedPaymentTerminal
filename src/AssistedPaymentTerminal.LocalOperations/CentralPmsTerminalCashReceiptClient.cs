using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public interface ICentralPmsTerminalCashReceiptClient
{
    Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CentralPmsTerminalCashReceiptClient(HttpClient httpClient) : ICentralPmsTerminalCashReceiptClient
{
    public async Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(baseUri, $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}/receipt-presentation"));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return await ParseResponseAsync(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Timeout();
        }
        catch (HttpRequestException)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable();
        }
    }

    private static async Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var payload = await response.Content.ReadFromJsonAsync<TerminalCashReceiptPresentationResponse>(
                TerminalCashPaymentPayloadFactory.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
                payload!,
                (int)response.StatusCode,
                payload!.CorrelationId);
        }

        var safeError = await ReadSafeErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var safeErrorCode = safeError?.ErrorCode;
        var retryable = safeError?.Retryable ?? IsRetryableTransportStatus(response.StatusCode);
        var correlationId = safeError?.CorrelationId;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (retryable)
            {
                return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady(
                    (int)response.StatusCode,
                    safeErrorCode,
                    retryable,
                    correlationId);
            }

            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotFound(
                (int)response.StatusCode,
                safeErrorCode,
                retryable,
                correlationId);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (IsUnsupported(safeErrorCode))
            {
                return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unsupported(
                    (int)response.StatusCode,
                    safeErrorCode,
                    retryable,
                    correlationId);
            }

            if (IsMalformed(safeErrorCode))
            {
                return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Malformed(
                    (int)response.StatusCode,
                    safeErrorCode,
                    retryable,
                    correlationId);
            }

            return IsInconsistent(safeErrorCode)
                ? CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Inconsistent(
                    (int)response.StatusCode,
                    safeErrorCode,
                    retryable,
                    correlationId)
                : CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady(
                    (int)response.StatusCode,
                    safeErrorCode,
                    retryable,
                    correlationId);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Rejected(
                (int)response.StatusCode,
                safeErrorCode,
                retryable,
                correlationId);
        }

        if ((int)response.StatusCode >= 500)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable(
                (int)response.StatusCode,
                safeErrorCode,
                retryable,
                correlationId);
        }

        return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unknown(
            (int)response.StatusCode,
            safeErrorCode,
            retryable,
            correlationId);
    }

    private static bool IsInconsistent(string? safeErrorCode) =>
        safeErrorCode is "POS_FISCAL_DOCUMENT_PRESENTATION_INCONSISTENT"
            or "TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT";

    private static bool IsUnsupported(string? safeErrorCode) =>
        safeErrorCode is "POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED";

    private static bool IsMalformed(string? safeErrorCode) =>
        safeErrorCode is "POS_SERVER_RECEIPT_PRESENTATION_MALFORMED";

    private static bool IsRetryableTransportStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.ServiceUnavailable || (int)statusCode >= 500;

    private static async Task<CentralPmsSafeError?> ReadSafeErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<CentralPmsSafeError>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record CentralPmsTerminalCashReceiptResult<T>(
    TerminalCashReceiptRetrievalAttemptOutcome Outcome,
    T? Payload,
    int? HttpStatus,
    string? SafeErrorCode,
    bool Retryable,
    Guid? CorrelationId)
{
    public static CentralPmsTerminalCashReceiptResult<T> Available(T payload, int httpStatus, Guid correlationId) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Available, payload, httpStatus, null, false, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Available(T payload, int httpStatus) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Available, payload, httpStatus, null, false, null);

    public static CentralPmsTerminalCashReceiptResult<T> NotFound(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = false,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.NotFound, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> NotReady(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = true,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.NotReady, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Rejected(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = false,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Rejected, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Inconsistent(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = false,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Inconsistent, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Unsupported(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = false,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Unsupported, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Malformed(
        int httpStatus,
        string? safeErrorCode,
        bool retryable = false,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Malformed, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Timeout() =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Timeout, default, null, "TIMEOUT", true, null);

    public static CentralPmsTerminalCashReceiptResult<T> Unavailable(
        int? httpStatus = null,
        string? safeErrorCode = null,
        bool retryable = true,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Unavailable, default, httpStatus, safeErrorCode, retryable, correlationId);

    public static CentralPmsTerminalCashReceiptResult<T> Unknown(
        int? httpStatus = null,
        string? safeErrorCode = null,
        bool retryable = true,
        Guid? correlationId = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Unknown, default, httpStatus, safeErrorCode, retryable, correlationId);
}
