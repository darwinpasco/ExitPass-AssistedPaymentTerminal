using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public interface ICentralPmsTerminalCashPaymentClient
{
    Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(
        Uri baseUri,
        TerminalCashPaymentRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CentralPmsTerminalCashPaymentClient(HttpClient httpClient) : ICentralPmsTerminalCashPaymentClient
{
    public async Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(
        Uri baseUri,
        TerminalCashPaymentRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/v1/terminal-cash-payments"))
            {
                Content = JsonContent.Create(payload, options: TerminalCashPaymentPayloadFactory.JsonOptions)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return await ParseResponseAsync<TerminalCashPaymentResponse>(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Timeout();
        }
        catch (HttpRequestException)
        {
            return CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Unavailable();
        }
    }

    public async Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(
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
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}"));
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return await ParseResponseAsync<TerminalCashPaymentReadbackResponse>(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.Timeout();
        }
        catch (HttpRequestException)
        {
            return CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>.Unavailable();
        }
    }

    private static async Task<CentralPmsTerminalCashPaymentResult<T>> ParseResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var payload = await response.Content.ReadFromJsonAsync<T>(
                TerminalCashPaymentPayloadFactory.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return CentralPmsTerminalCashPaymentResult<T>.Confirmed(payload!, (int)response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CentralPmsTerminalCashPaymentResult<T>.NotFound((int)response.StatusCode, await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return CentralPmsTerminalCashPaymentResult<T>.Conflict((int)response.StatusCode, await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return CentralPmsTerminalCashPaymentResult<T>.Rejected((int)response.StatusCode, await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if ((int)response.StatusCode >= 500)
        {
            return CentralPmsTerminalCashPaymentResult<T>.Unavailable((int)response.StatusCode, await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        return CentralPmsTerminalCashPaymentResult<T>.Unknown((int)response.StatusCode, await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
    }

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

public sealed record CentralPmsTerminalCashPaymentResult<T>(
    TerminalCashPaymentAttemptOutcome Outcome,
    T? Payload,
    int? HttpStatus,
    string? SafeErrorCode)
{
    public static CentralPmsTerminalCashPaymentResult<T> Confirmed(T payload, int httpStatus) =>
        new(TerminalCashPaymentAttemptOutcome.Confirmed, payload, httpStatus, null);

    public static CentralPmsTerminalCashPaymentResult<T> NotFound(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashPaymentAttemptOutcome.NotFound, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashPaymentResult<T> Conflict(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashPaymentAttemptOutcome.Conflict, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashPaymentResult<T> Rejected(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashPaymentAttemptOutcome.Rejected, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashPaymentResult<T> Timeout() =>
        new(TerminalCashPaymentAttemptOutcome.Timeout, default, null, "TIMEOUT");

    public static CentralPmsTerminalCashPaymentResult<T> Unavailable(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashPaymentAttemptOutcome.Unavailable, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashPaymentResult<T> Unknown(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashPaymentAttemptOutcome.Unknown, default, httpStatus, safeErrorCode);
}
