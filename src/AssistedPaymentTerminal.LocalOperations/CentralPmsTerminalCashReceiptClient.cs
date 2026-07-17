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
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(payload!, (int)response.StatusCode);
        }

        var safeErrorCode = await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotFound(
                (int)response.StatusCode,
                safeErrorCode);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return IsInconsistent(safeErrorCode)
                ? CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Inconsistent((int)response.StatusCode, safeErrorCode)
                : CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.NotReady((int)response.StatusCode, safeErrorCode);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Rejected(
                (int)response.StatusCode,
                safeErrorCode);
        }

        if ((int)response.StatusCode >= 500)
        {
            return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unavailable(
                (int)response.StatusCode,
                safeErrorCode);
        }

        return CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Unknown(
            (int)response.StatusCode,
            safeErrorCode);
    }

    private static bool IsInconsistent(string? safeErrorCode) =>
        safeErrorCode is "POS_FISCAL_DOCUMENT_PRESENTATION_INCONSISTENT"
            or "TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT"
            or "POS_FISCAL_DOCUMENT_ID_MISSING";

    private static async Task<string?> ReadSafeErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken).ConfigureAwait(false);
            return error?.ErrorCode;
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
    string? SafeErrorCode)
{
    public static CentralPmsTerminalCashReceiptResult<T> Available(T payload, int httpStatus) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Available, payload, httpStatus, null);

    public static CentralPmsTerminalCashReceiptResult<T> NotFound(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.NotFound, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashReceiptResult<T> NotReady(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.NotReady, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashReceiptResult<T> Rejected(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Rejected, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashReceiptResult<T> Inconsistent(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Inconsistent, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashReceiptResult<T> Timeout() =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Timeout, default, null, "TIMEOUT");

    public static CentralPmsTerminalCashReceiptResult<T> Unavailable(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Unavailable, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashReceiptResult<T> Unknown(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashReceiptRetrievalAttemptOutcome.Unknown, default, httpStatus, safeErrorCode);
}
