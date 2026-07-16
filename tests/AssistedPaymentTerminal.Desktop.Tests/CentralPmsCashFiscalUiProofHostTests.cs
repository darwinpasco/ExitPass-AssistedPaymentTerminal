using System.Net;
using System.Net.Http.Json;
using AssistedPaymentTerminal.CentralPmsCashFiscalUiProof;
using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashFiscalUiProofHostTests
{
    [Fact]
    public void AutomatedProofArgumentsParseWithoutInteractiveMode()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "proof.db");

        var arguments = ProofArguments.Parse(["--database-path", databasePath]);

        Assert.False(arguments.Interactive);
        Assert.Equal(FiscalUiProofScenario.Recorded, arguments.Scenario);
        Assert.Equal(Path.GetFullPath(databasePath), arguments.DatabasePath);
    }

    [Fact]
    public void InteractiveModeParsesEverySupportedScenario()
    {
        foreach (var scenarioName in Enum.GetNames<FiscalUiProofScenario>())
        {
            var arguments = ProofArguments.Parse(["--interactive", "--scenario", scenarioName]);

            Assert.True(arguments.Interactive);
            Assert.Equal(Enum.Parse<FiscalUiProofScenario>(scenarioName), arguments.Scenario);
        }
    }

    [Fact]
    public void InvalidScenarioIsRejectedSafely()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProofArguments.Parse(["--interactive", "--scenario", "DuplicateInvoice"]));

        Assert.Contains("Unsupported scenario", exception.Message);
    }

    [Fact]
    public async Task InteractiveHostBindsOnlyToLoopback()
    {
        await using var host = InteractiveCentralPmsFiscalProofHost.Start(FiscalUiProofScenario.Recorded);

        Assert.Equal("127.0.0.1", host.BaseUrl.Host);
        Assert.True(host.BaseUrl.Port > 0);
    }

    [Fact]
    public async Task CashPaymentConfirmationResponseIsContractFaithful()
    {
        await using var host = await StartHostAsync(FiscalUiProofScenario.Recorded);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };
        var request = PaymentRequest();

        using var response = await PostCashPaymentAsync(client, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<TerminalCashPaymentResponse>();
        Assert.NotNull(payload);
        Assert.Equal(request.TerminalCashTenderId, payload!.TerminalCashTenderId);
        Assert.Equal("CONFIRMED", payload.CanonicalPaymentStatus);
        Assert.Equal("NOT_STARTED_IN_THIS_SLICE", payload.FiscalStatus);
    }

    [Fact]
    public async Task RecordedFiscalResponseContainsStableIdentifiers()
    {
        var fiscal = await ExecuteFiscalPostAsync(FiscalUiProofScenario.Recorded);

        Assert.Equal("FISCAL_ISSUANCE_RECORDED", fiscal.FiscalIssuanceState);
        Assert.Equal(Guid.Parse("55555555-5555-4555-8555-555555555555"), fiscal.FiscalIssuanceReferenceId);
        Assert.Equal(Guid.Parse("66666666-6666-4666-8666-666666666666"), fiscal.PosFiscalDocumentId);
        Assert.Equal("SI-000001", fiscal.FiscalDocumentNumber);
        Assert.False(fiscal.ExitAuthorizationIssued);
        Assert.False(fiscal.GateBehaviorTriggered);
    }

    [Fact]
    public async Task ReplayReturnsTheSameIdentifiers()
    {
        var recorded = await ExecuteFiscalPostAsync(FiscalUiProofScenario.Recorded);
        var replay = await ExecuteFiscalPostAsync(FiscalUiProofScenario.Replay);

        Assert.Equal("IDEMPOTENT_REPLAY", replay.ResultClassification);
        Assert.Equal(recorded.FiscalIssuanceReferenceId, replay.FiscalIssuanceReferenceId);
        Assert.Equal(recorded.PosFiscalDocumentId, replay.PosFiscalDocumentId);
        Assert.Equal(recorded.FiscalDocumentNumber, replay.FiscalDocumentNumber);
    }

    [Fact]
    public async Task PendingDoesNotReturnRecordedState()
    {
        var fiscal = await ExecuteFiscalPostAsync(FiscalUiProofScenario.Pending);

        Assert.Equal("FISCAL_ISSUANCE_REQUESTED", fiscal.FiscalIssuanceState);
        Assert.Null(fiscal.PosFiscalDocumentId);
        Assert.Null(fiscal.FiscalDocumentNumber);
    }

    [Fact]
    public async Task ConflictReturnsSafeHttp409Posture()
    {
        using var response = await ExecuteFiscalPostResponseAsync(FiscalUiProofScenario.Conflict);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>();
        Assert.Equal("TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT", error!.ErrorCode);
    }

    [Fact]
    public async Task RejectedReturnsSafeDeterministicRejection()
    {
        using var response = await ExecuteFiscalPostResponseAsync(FiscalUiProofScenario.Rejected);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CentralPmsSafeError>();
        Assert.Equal("TERMINAL_CASH_FISCAL_REJECTED", error!.ErrorCode);
    }

    [Fact]
    public async Task UncertainThenRecordedPerformsUncertainPostThenSuccessfulGetReadback()
    {
        await using var host = await StartHostAsync(FiscalUiProofScenario.UncertainThenRecorded);
        using var client = new HttpClient { BaseAddress = host.BaseUrl, Timeout = TimeSpan.FromSeconds(2) };
        var request = PaymentRequest();
        await PostCashPaymentAsync(client, request);

        await Assert.ThrowsAsync<HttpRequestException>(() => PostFiscalAsync(client, request.TerminalCashTenderId));

        using var readbackResponse = await client.GetAsync($"/v1/terminal-cash-payments/references/{request.TerminalCashTenderId:D}/fiscal-issuance");
        Assert.Equal(HttpStatusCode.OK, readbackResponse.StatusCode);
        var fiscal = await readbackResponse.Content.ReadFromJsonAsync<TerminalCashFiscalIssuanceResponse>();
        Assert.Equal("FISCAL_ISSUANCE_RECORDED", fiscal!.FiscalIssuanceState);
        Assert.Equal(
            ["POST", "GET"],
            host.RequestLog
                .Where(entry => entry.Operation == "terminal-cash-fiscal-issuance")
                .Select(entry => entry.Method)
                .ToArray());
    }

    [Fact]
    public async Task ProofHostDoesNotExposeReceiptExitGateProviderOrDirectPosBehavior()
    {
        await using var host = await StartHostAsync(FiscalUiProofScenario.Recorded);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };

        foreach (var path in new[]
        {
            "/v1/receipts",
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

    private static async Task<InteractiveCentralPmsFiscalProofHost> StartHostAsync(FiscalUiProofScenario scenario)
    {
        var host = InteractiveCentralPmsFiscalProofHost.Start(scenario);
        var cancellation = new CancellationTokenSource();
        _ = Task.Run(() => host.RunUntilCancelledAsync(cancellation.Token));
        await Task.Delay(50);
        return host;
    }

    private static async Task<TerminalCashFiscalIssuanceResponse> ExecuteFiscalPostAsync(FiscalUiProofScenario scenario)
    {
        using var response = await ExecuteFiscalPostResponseAsync(scenario);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<TerminalCashFiscalIssuanceResponse>();
        return payload!;
    }

    private static async Task<HttpResponseMessage> ExecuteFiscalPostResponseAsync(FiscalUiProofScenario scenario)
    {
        await using var host = await StartHostAsync(scenario);
        using var client = new HttpClient { BaseAddress = host.BaseUrl };
        var request = PaymentRequest();
        await PostCashPaymentAsync(client, request);

        return await PostFiscalAsync(client, request.TerminalCashTenderId);
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
