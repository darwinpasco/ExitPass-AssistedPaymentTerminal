using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.CentralPmsCashReceiptStatusUiProof;

public enum ReceiptStatusUiProofScenario
{
    Available,
    NotReady,
    RetryPending,
    Inconsistent,
    Rejected,
    Voided,
    UnavailableThenAvailable
}

public sealed record ReceiptProofHostRequestLogEntry(
    string Operation,
    Guid TerminalCashTenderId,
    ReceiptStatusUiProofScenario Scenario,
    string Method,
    int Sequence);

public sealed class InteractiveCentralPmsReceiptProofHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid FiscalIssuanceReferenceId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid PosFiscalDocumentId = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-07-15T00:04:00Z");
    private static readonly DateTimeOffset UpdatedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");
    private static readonly DateTimeOffset FiscalNumberAssignedAt = DateTimeOffset.Parse("2026-07-15T00:05:00Z");

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, PaymentRecord> _payments = new();
    private readonly ConcurrentQueue<ReceiptProofHostRequestLogEntry> _requestLog = new();
    private readonly object _sequenceLock = new();
    private int _sequence;
    private int _unavailableReceiptGetCount;

    private InteractiveCentralPmsReceiptProofHost(ReceiptStatusUiProofScenario scenario, TcpListener listener)
    {
        Scenario = scenario;
        _listener = listener;
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = new Uri($"http://127.0.0.1:{endpoint.Port}");
    }

    public ReceiptStatusUiProofScenario Scenario { get; }

    public Uri BaseUrl { get; }

    public IReadOnlyCollection<ReceiptProofHostRequestLogEntry> RequestLog => _requestLog.ToArray();

    public static bool TryParseScenario(string value, out ReceiptStatusUiProofScenario scenario) =>
        Enum.TryParse(value, ignoreCase: true, out scenario)
        && Enum.IsDefined(typeof(ReceiptStatusUiProofScenario), scenario);

    public static InteractiveCentralPmsReceiptProofHost Start(ReceiptStatusUiProofScenario scenario, int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new InteractiveCentralPmsReceiptProofHost(scenario, listener);
    }

    public async Task RunUntilCancelledAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        client.LingerState = new LingerOption(enable: true, seconds: 1);
        using var stream = client.GetStream();
        var request = await HttpProofRequest.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var response = HandleRequest(request);
        await response.WriteAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
    }

    private HttpProofResponse HandleRequest(HttpProofRequest request)
    {
        if (request.Method == "POST" && request.Path == "/v1/terminal-cash-payments")
        {
            using var payload = JsonDocument.Parse(request.Body);
            var recordFromRequest = PaymentRecord.From(payload.RootElement, request.CorrelationId);
            var record = _payments.GetOrAdd(recordFromRequest.TerminalCashTenderId, recordFromRequest);
            Log("terminal-cash-payment", record.TerminalCashTenderId, request.Method);
            return HttpProofResponse.Json(HttpStatusCode.Created, record.ToPaymentResponse());
        }

        if (request.Path.StartsWith("/v1/terminal-cash-payments/references/", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = request.Path["/v1/terminal-cash-payments/references/".Length..];
            if (suffix.EndsWith("/fiscal-issuance", StringComparison.OrdinalIgnoreCase))
            {
                var tenderText = suffix[..^"/fiscal-issuance".Length].TrimEnd('/');
                return HandleFiscal(request, tenderText);
            }

            if (suffix.EndsWith("/receipt-presentation", StringComparison.OrdinalIgnoreCase))
            {
                var tenderText = suffix[..^"/receipt-presentation".Length].TrimEnd('/');
                return HandleReceipt(request, tenderText);
            }

            return HandlePaymentReadback(request, suffix);
        }

        return HttpProofResponse.Json(HttpStatusCode.NotFound, SafeError("UNSUPPORTED_PROOF_ENDPOINT", request.CorrelationId));
    }

    private HttpProofResponse HandlePaymentReadback(HttpProofRequest request, string tenderText)
    {
        if (!Guid.TryParse(tenderText, out var terminalCashTenderId))
        {
            return HttpProofResponse.Json(HttpStatusCode.BadRequest, SafeError("INVALID_TERMINAL_CASH_TENDER_ID", request.CorrelationId));
        }

        Log("terminal-cash-payment", terminalCashTenderId, request.Method);
        return _payments.TryGetValue(terminalCashTenderId, out var record)
            ? HttpProofResponse.Json(HttpStatusCode.OK, record.ToReadbackResponse())
            : HttpProofResponse.Json(HttpStatusCode.NotFound, SafeError("TERMINAL_CASH_PAYMENT_NOT_FOUND", request.CorrelationId));
    }

    private HttpProofResponse HandleFiscal(HttpProofRequest request, string tenderText)
    {
        if (!Guid.TryParse(tenderText, out var terminalCashTenderId))
        {
            return HttpProofResponse.Json(HttpStatusCode.BadRequest, SafeError("INVALID_TERMINAL_CASH_TENDER_ID", request.CorrelationId));
        }

        Log("terminal-cash-fiscal-issuance", terminalCashTenderId, request.Method);
        return _payments.TryGetValue(terminalCashTenderId, out var payment)
            ? HttpProofResponse.Json(HttpStatusCode.OK, RecordedFiscalResponse(payment, request.CorrelationId))
            : HttpProofResponse.Json(HttpStatusCode.NotFound, SafeError("TERMINAL_CASH_PAYMENT_NOT_FOUND", request.CorrelationId));
    }

    private HttpProofResponse HandleReceipt(HttpProofRequest request, string tenderText)
    {
        if (!Guid.TryParse(tenderText, out var terminalCashTenderId))
        {
            return HttpProofResponse.Json(HttpStatusCode.BadRequest, SafeError("INVALID_TERMINAL_CASH_TENDER_ID", request.CorrelationId));
        }

        Log("terminal-cash-receipt-presentation", terminalCashTenderId, request.Method);
        if (!_payments.TryGetValue(terminalCashTenderId, out var payment))
        {
            return HttpProofResponse.Json(HttpStatusCode.NotFound, SafeError("TERMINAL_CASH_PAYMENT_NOT_FOUND", request.CorrelationId));
        }

        return Scenario switch
        {
            ReceiptStatusUiProofScenario.NotReady => HttpProofResponse.Json(HttpStatusCode.Conflict, SafeError("RECEIPT_PRESENTATION_NOT_READY", request.CorrelationId)),
            ReceiptStatusUiProofScenario.RetryPending => HttpProofResponse.Json(HttpStatusCode.ServiceUnavailable, SafeError("CENTRAL_PMS_UNAVAILABLE", request.CorrelationId)),
            ReceiptStatusUiProofScenario.Inconsistent => HttpProofResponse.Json(HttpStatusCode.Conflict, SafeError("TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT", request.CorrelationId)),
            ReceiptStatusUiProofScenario.Rejected => HttpProofResponse.Json(HttpStatusCode.BadRequest, SafeError("RECEIPT_PRESENTATION_REJECTED", request.CorrelationId)),
            ReceiptStatusUiProofScenario.UnavailableThenAvailable when Interlocked.Increment(ref _unavailableReceiptGetCount) == 1 =>
                HttpProofResponse.Json(HttpStatusCode.ServiceUnavailable, SafeError("CENTRAL_PMS_UNAVAILABLE", request.CorrelationId)),
            ReceiptStatusUiProofScenario.Voided => HttpProofResponse.Json(HttpStatusCode.OK, ReceiptResponse(payment, request.CorrelationId, voided: true)),
            _ => HttpProofResponse.Json(HttpStatusCode.OK, ReceiptResponse(payment, request.CorrelationId, voided: false))
        };
    }

    private static TerminalCashFiscalIssuanceResponse RecordedFiscalResponse(PaymentRecord payment, Guid? correlationId) =>
        new(
            payment.TerminalCashTenderId,
            payment.PaymentAttemptId,
            payment.PaymentConfirmationId,
            FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            "NEWLY_CREATED",
            PosFiscalDocumentId,
            "SI-000001",
            FiscalNumberAssignedAt,
            "pos-server-semantic-hash:sha256:v1",
            CreatedAt,
            UpdatedAt,
            correlationId ?? payment.CorrelationId,
            null,
            null,
            true,
            false,
            false);

    private static TerminalCashReceiptPresentationResponse ReceiptResponse(PaymentRecord payment, Guid? correlationId, bool voided)
    {
        using var document = JsonDocument.Parse(voided ? VoidedPresentationJson : AvailablePresentationJson);
        return new TerminalCashReceiptPresentationResponse(
            payment.TerminalCashTenderId,
            payment.PaymentAttemptId,
            payment.PaymentConfirmationId,
            FiscalIssuanceReferenceId,
            "FISCAL_ISSUANCE_RECORDED",
            PosFiscalDocumentId,
            "SI-000001",
            voided ? "voided" : "recorded",
            voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
            "digital-sales-invoice-presentation-json-v1",
            "digital-sales-invoice-json-v1",
            "application/json",
            document.RootElement.Clone(),
            voided ? "voided" : null,
            voided ? "operator_void" : null,
            voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
            CreatedAt,
            UpdatedAt,
            correlationId ?? payment.CorrelationId);
    }

    private static CentralPmsSafeError SafeError(string code, Guid? correlationId) =>
        new(code, code.Replace('_', ' '), correlationId ?? Guid.Empty, Retryable: true);

    private void Log(string operation, Guid terminalCashTenderId, string method)
    {
        int sequence;
        lock (_sequenceLock)
        {
            sequence = ++_sequence;
        }

        var entry = new ReceiptProofHostRequestLogEntry(operation, terminalCashTenderId, Scenario, method, sequence);
        _requestLog.Enqueue(entry);
        Console.WriteLine($"{entry.Sequence}: {entry.Operation} {entry.Method} tender={entry.TerminalCashTenderId:D} scenario={entry.Scenario}");
    }

    private const string AvailablePresentationJson = """
    {"presentation":{"presentationVersion":"digital-sales-invoice-presentation-json-v1","lines":[{"description":"Parking fee - cash","amountMinorUnits":12500}],"taxes":[{"taxType":"VAT","amountMinorUnits":0}],"totals":[{"totalType":"grand_total","amountMinorUnits":12500}],"tenders":[{"tenderType":"CASH","amountMinorUnits":15000}]},"fiscalDocumentNumber":"SI-000001","contentType":"application/json"}
    """;

    private const string VoidedPresentationJson = """
    {"presentation":{"presentationVersion":"digital-sales-invoice-presentation-json-v1","lines":[{"description":"Parking fee - cash","amountMinorUnits":12500}],"taxes":[{"taxType":"VAT","amountMinorUnits":0}],"totals":[{"totalType":"grand_total","amountMinorUnits":12500}],"tenders":[{"tenderType":"CASH","amountMinorUnits":15000}]},"fiscalDocumentNumber":"SI-000001","contentType":"application/json","voidStatus":"voided","voidReasonCode":"operator_void"}
    """;

    private sealed record PaymentRecord(
        Guid TerminalCashTenderId,
        Guid PaymentAttemptId,
        Guid PaymentConfirmationId,
        Guid CashCustodySessionId,
        Guid ParkingSessionId,
        Guid TariffSnapshotId,
        string TerminalId,
        Guid SiteId,
        Guid SiteGroupId,
        string PosServerId,
        string CashierId,
        string CashierShiftId,
        string Currency,
        long AmountDueMinorUnits,
        long AmountTenderedMinorUnits,
        long ChangeDueMinorUnits,
        Guid CorrelationId)
    {
        public static PaymentRecord From(JsonElement request, Guid? correlationId) =>
            new(
                request.GetProperty("terminalCashTenderId").GetGuid(),
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                request.GetProperty("cashCustodySessionId").GetGuid(),
                request.GetProperty("parkingSessionId").GetGuid(),
                request.GetProperty("tariffSnapshotId").GetGuid(),
                request.GetProperty("terminalId").GetString()!,
                request.GetProperty("siteId").GetGuid(),
                request.GetProperty("siteGroupId").GetGuid(),
                request.GetProperty("posServerId").GetString()!,
                request.GetProperty("cashierId").GetString()!,
                request.GetProperty("cashierShiftId").GetString()!,
                request.GetProperty("currency").GetString()!,
                request.GetProperty("amountDueMinorUnits").GetInt64(),
                request.GetProperty("amountTenderedMinorUnits").GetInt64(),
                request.GetProperty("changeDueMinorUnits").GetInt64(),
                correlationId ?? Guid.Parse("77777777-7777-4777-8777-777777777777"));

        public TerminalCashPaymentResponse ToPaymentResponse() =>
            new(TerminalCashTenderId, PaymentAttemptId, PaymentConfirmationId, "CONFIRMED", "CREATED", "terminal-cash-payment", "terminal-cash-payment:sha256:v1", CreatedAt, UpdatedAt, UpdatedAt, CorrelationId, "NOT_STARTED_IN_THIS_SLICE");

        public TerminalCashPaymentReadbackResponse ToReadbackResponse() =>
            new(TerminalCashTenderId, PaymentAttemptId, CashCustodySessionId, ParkingSessionId, TariffSnapshotId, TerminalId, SiteId, SiteGroupId, PosServerId, CashierId, CashierShiftId, Currency, AmountDueMinorUnits, AmountTenderedMinorUnits, ChangeDueMinorUnits, "CONFIRMED", PaymentConfirmationId, "CREATED", "terminal-cash-payment", "terminal-cash-payment:sha256:v1", CreatedAt, UpdatedAt, UpdatedAt, CorrelationId, "NOT_STARTED_IN_THIS_SLICE");
    }
}

internal sealed record HttpProofRequest(string Method, string Path, string Body, Guid? CorrelationId)
{
    public static async Task<HttpProofRequest?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            headerBytes.Add(buffer[0]);
            if (headerBytes.Count >= 4 && headerBytes[^4] == '\r' && headerBytes[^3] == '\n' && headerBytes[^2] == '\r' && headerBytes[^1] == '\n') break;
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headers.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contentLength = 0;
        var chunked = false;
        Guid? correlationId = null;
        foreach (var line in lines.Skip(1))
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0) continue;
            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
            if (string.Equals(name, "X-Correlation-Id", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out var parsed)) correlationId = parsed;
        }

        var body = string.Empty;
        if (chunked)
        {
            body = await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        else if (contentLength > 0)
        {
            var bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }

            body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
        }

        return requestLine.Length < 2 ? null : new HttpProofRequest(requestLine[0].ToUpperInvariant(), requestLine[1], body, correlationId);
    }

    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var semicolonIndex = sizeLine.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                sizeLine = sizeLine[..semicolonIndex];
            }

            var chunkSize = Convert.ToInt32(sizeLine, 16);
            if (chunkSize == 0)
            {
                await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
                break;
            }

            var chunk = new byte[chunkSize];
            var offset = 0;
            while (offset < chunkSize)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset, chunkSize - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            body.Write(chunk, 0, offset);
            await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static async Task<string> ReadAsciiLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer[0] == '\n')
            {
                break;
            }

            if (buffer[0] != '\r')
            {
                bytes.Add(buffer[0]);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}

internal sealed record HttpProofResponse(HttpStatusCode StatusCode, object Payload)
{
    public static HttpProofResponse Json(HttpStatusCode statusCode, object payload) => new(statusCode, payload);

    public async Task WriteAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var statusLine = $"HTTP/1.1 {(int)StatusCode} {StatusCode}\r\n";
        var headers = $"Content-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(statusLine + headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
