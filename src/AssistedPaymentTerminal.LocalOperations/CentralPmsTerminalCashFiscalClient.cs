using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public interface ICentralPmsTerminalCashFiscalClient
{
    Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> SubmitAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        TerminalCashFiscalIssuanceRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CentralPmsTerminalCashFiscalClient(HttpClient httpClient) : ICentralPmsTerminalCashFiscalClient
{
    public async Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> SubmitAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        TerminalCashFiscalIssuanceRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(baseUri, $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}/fiscal-issuance"))
            {
                Content = JsonContent.Create(payload, options: TerminalCashPaymentPayloadFactory.JsonOptions)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return await ParseResponseAsync(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout();
        }
        catch (HttpRequestException)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unavailable();
        }
    }

    public async Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(
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
                new Uri(baseUri, $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}/fiscal-issuance"));
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return await ParseResponseAsync(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Timeout();
        }
        catch (HttpRequestException)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unavailable();
        }
    }

    private static async Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var payload = await response.Content.ReadFromJsonAsync<TerminalCashFiscalIssuanceResponse>(
                TerminalCashPaymentPayloadFactory.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(payload!, (int)response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.NotFound(
                (int)response.StatusCode,
                await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Conflict(
                (int)response.StatusCode,
                await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Rejected(
                (int)response.StatusCode,
                await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if ((int)response.StatusCode >= 500)
        {
            return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unavailable(
                (int)response.StatusCode,
                await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        return CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Unknown(
            (int)response.StatusCode,
            await ReadSafeErrorCodeAsync(response, cancellationToken).ConfigureAwait(false));
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

public sealed record CentralPmsTerminalCashFiscalResult<T>(
    TerminalCashFiscalAttemptOutcome Outcome,
    T? Payload,
    int? HttpStatus,
    string? SafeErrorCode)
{
    public static CentralPmsTerminalCashFiscalResult<T> Recorded(T payload, int httpStatus) =>
        new(TerminalCashFiscalAttemptOutcome.Recorded, payload, httpStatus, null);

    public static CentralPmsTerminalCashFiscalResult<T> NotFound(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashFiscalAttemptOutcome.NotFound, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashFiscalResult<T> Conflict(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashFiscalAttemptOutcome.Conflict, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashFiscalResult<T> Rejected(int httpStatus, string? safeErrorCode) =>
        new(TerminalCashFiscalAttemptOutcome.Rejected, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashFiscalResult<T> Timeout() =>
        new(TerminalCashFiscalAttemptOutcome.Timeout, default, null, "TIMEOUT");

    public static CentralPmsTerminalCashFiscalResult<T> Unavailable(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashFiscalAttemptOutcome.Unavailable, default, httpStatus, safeErrorCode);

    public static CentralPmsTerminalCashFiscalResult<T> Unknown(int? httpStatus = null, string? safeErrorCode = null) =>
        new(TerminalCashFiscalAttemptOutcome.Unknown, default, httpStatus, safeErrorCode);
}
