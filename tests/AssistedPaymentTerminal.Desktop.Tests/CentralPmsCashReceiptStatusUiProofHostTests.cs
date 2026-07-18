using System.Net;
using System.Net.Http.Json;
using AssistedPaymentTerminal.CentralPmsCashReceiptStatusUiProof;
using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashReceiptStatusUiProofHostTests
{
    [Fact]
    public void ReceiptAutomatedProofArgumentsParseWithoutInteractiveMode()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "receipt-proof.db");

        var arguments = ReceiptProofArguments.Parse(["--database-path", databasePath]);

        Assert.False(arguments.Interactive);
        Assert.Equal(ReceiptStatusUiProofScenario.Available, arguments.Scenario);
        Assert.Equal(Path.GetFullPath(databasePath), arguments.DatabasePath);
    }

    [Fact]
    public void ReceiptInteractiveModeParsesEverySupportedScenario()
    {
        foreach (var scenarioName in Enum.GetNames<ReceiptStatusUiProofScenario>())
        {
            var arguments = ReceiptProofArguments.Parse(["--interactive", "--scenario", scenarioName]);

            Assert.True(arguments.Interactive);
            Assert.Equal(Enum.Parse<ReceiptStatusUiProofScenario>(scenarioName), arguments.Scenario);
        }
    }

    [Fact]
    public void ReceiptInvalidScenarioIsRejectedSafely()
    {
        var exception = Assert.Throws<ArgumentException>(() => ReceiptProofArguments.Parse(["--interactive", "--scenario", "Printed"]));

        Assert.Contains("Unsupported scenario", exception.Message);
    }

    [Fact]
    public async Task ReceiptInteractiveHostBindsOnlyToLoopback()
    {
        await using var host = InteractiveCentralPmsReceiptProofHost.Start(ReceiptStatusUiProofScenario.Available);

        Assert.Equal("127.0.0.1", host.BaseUrl.Host);
        Assert.True(host.BaseUrl.Port > 0);
    }

    [Fact]
    public async Task AvailableReceiptResponseContainsStableMetadata()
    {
        var receipt = await ExecuteReceiptGetAsync(ReceiptStatusUiProofScenario.Available);

        Assert.Equal("AVAILABLE", receipt.ReceiptAvailabilityState);
        Assert.Equal("SI-000001", receipt.FiscalDocumentNumber);
        Assert.Equal("digital-sales-invoice-presentation-json-v1", receipt.PresentationVersion);
        Assert.Equal("digital-sales-invoice-json-v1", receipt.TemplateVersion);
        Assert.Equal("application/json", receipt.ContentType);
    }

    [Fact]
    public async Task VoidedReceiptResponsePreservesVoidPosture()
    {
        var receipt = await ExecuteReceiptGetAsync(ReceiptStatusUiProofScenario.Voided);

        Assert.Equal("VOIDED_PRESENTATION_AVAILABLE", receipt.ReceiptAvailabilityState);
        Assert.Equal("voided", receipt.VoidStatus);
        Assert.Equal("operator_void", receipt.VoidReasonCode);
    }

    [Fact]
    public async Task NotReadyConflictDoesNotReturnPresentation()
    {
        using var response = await ExecuteReceiptGetResponseAsync(ReceiptStatusUiProofScenario.NotReady);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>();
        Assert.Equal("RECEIPT_PRESENTATION_NOT_READY", error!.ErrorCode);
    }

    [Fact]
    public async Task InconsistentConflictMapsSafely()
    {
        using var response = await ExecuteReceiptGetResponseAsync(ReceiptStatusUiProofScenario.Inconsistent);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>();
        Assert.Equal("TERMINAL_CASH_RECEIPT_REFERENCE_CONFLICT", error!.ErrorCode);
    }

    [Fact]
    public async Task RejectedMapsSafeError()
    {
        using var response = await ExecuteReceiptGetResponseAsync(ReceiptStatusUiProofScenario.Rejected);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>();
        Assert.Equal("RECEIPT_PRESENTATION_REJECTED", error!.ErrorCode);
    }

    [Fact]
    public async Task UnavailableThenAvailablePerformsSecondGetSuccessfully()
    {
        await using var host = await StartHostAsync(ReceiptStatusUiProofScenario.UnavailableThenAvailable);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };
        var request = PaymentRequest();
        await PostCashPaymentAsync(client, request);
        await PostFiscalAsync(client, request.TerminalCashTenderId);

        using var first = await GetReceiptAsync(client, request.TerminalCashTenderId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var second = await GetReceiptAsync(client, request.TerminalCashTenderId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(["GET", "GET"], host.RequestLog
            .Where(entry => entry.Operation == "terminal-cash-receipt-presentation")
            .Select(entry => entry.Method)
            .ToArray());
    }

    [Fact]
    public async Task ReceiptProofHostDoesNotExposeRenderPrintExitGateProviderOrDirectPosBehavior()
    {
        await using var host = await StartHostAsync(ReceiptStatusUiProofScenario.Available);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };

        foreach (var path in new[]
        {
            "/v1/receipts/render",
            "/v1/print-jobs",
            "/v1/exit-authorizations",
            "/v1/gates/open",
            "/v1/payment-orchestrator/payments",
            "/v1/pos-server/fiscal-documents"
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static async Task<InteractiveCentralPmsReceiptProofHost> StartHostAsync(ReceiptStatusUiProofScenario scenario)
    {
        var host = InteractiveCentralPmsReceiptProofHost.Start(scenario);
        var cancellation = new CancellationTokenSource();
        _ = Task.Run(() => host.RunUntilCancelledAsync(cancellation.Token));
        await Task.Delay(50);
        return host;
    }

    private static async Task<TerminalCashReceiptPresentationResponse> ExecuteReceiptGetAsync(ReceiptStatusUiProofScenario scenario)
    {
        using var response = await ExecuteReceiptGetResponseAsync(scenario);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<TerminalCashReceiptPresentationResponse>();
        return payload!;
    }

    private static async Task<HttpResponseMessage> ExecuteReceiptGetResponseAsync(ReceiptStatusUiProofScenario scenario)
    {
        await using var host = await StartHostAsync(scenario);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };
        var request = PaymentRequest();
        await PostCashPaymentAsync(client, request);
        await PostFiscalAsync(client, request.TerminalCashTenderId);

        return await GetReceiptAsync(client, request.TerminalCashTenderId);
    }

    private static Task<HttpResponseMessage> PostCashPaymentAsync(HttpClient client, TerminalCashPaymentRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/terminal-cash-payments")
        {
            Content = JsonContent.Create(request, options: TerminalCashPaymentPayloadFactory.JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", "cash-idempotency-key");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", "77777777-7777-4777-8777-777777777777");
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> PostFiscalAsync(HttpClient client, Guid terminalCashTenderId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}/fiscal-issuance")
        {
            Content = JsonContent.Create(new TerminalCashFiscalIssuanceRequest(), options: TerminalCashPaymentPayloadFactory.JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", "fiscal-idempotency-key");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", "88888888-8888-4888-8888-888888888888");
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> GetReceiptAsync(HttpClient client, Guid terminalCashTenderId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/terminal-cash-payments/references/{terminalCashTenderId:D}/receipt-presentation");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", "99999999-9999-4999-8999-999999999999");
        return client.SendAsync(message);
    }

    private static TerminalCashPaymentRequest PaymentRequest() =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            "cashier-proof",
            "cashier-session-proof",
            "shift-proof",
            "terminal-proof",
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            Guid.Parse("66666666-6666-4666-8666-666666666666"),
            "pos-proof",
            "PHP",
            12500,
            15000,
            2500,
            DateTimeOffset.Parse("2026-07-15T00:03:00Z"),
            [new TerminalCashDenominationEntry("PHP-100", 10000, 1)],
            "cash-received-event-proof");
}
