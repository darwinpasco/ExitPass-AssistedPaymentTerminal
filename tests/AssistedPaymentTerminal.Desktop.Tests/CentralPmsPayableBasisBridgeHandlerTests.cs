using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsPayableBasisBridgeHandlerTests
{
    private static readonly Guid DeviceId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid SiteId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task ResolveUsesFixedEndpointAndHostOwnedSessionAuthority()
    {
        var transport = new CapturingHandler();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session-token")));

        var result = await handler.HandleWebMessageAsync(Request("payableBasis.resolve"));

        Assert.NotNull(result);
        Assert.Equal(HttpMethod.Post, transport.Request!.Method);
        Assert.Equal("https://central-pms.example.test/v1/terminal-cash-payments/payable-basis/resolve", transport.Request.RequestUri!.ToString());
        Assert.Equal("ExitPass-HumanSession", transport.Request.Headers.Authorization!.Scheme);
        Assert.Equal("opaque-session-token", transport.Request.Headers.Authorization.Parameter);
        Assert.Equal(DeviceId.ToString("D"), transport.Request.Headers.GetValues("X-ExitPass-Service-Identity-Id").Single());
        Assert.Equal(SiteId.ToString("D"), transport.Request.Headers.GetValues("X-Site-Id").Single());
        Assert.DoesNotContain("opaque-session-token", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatutoryReceiptUsesFixedReadOnlyEndpointAndHostOwnedSessionAuthority()
    {
        var transport = new CapturingHandler();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session-token")));

        var result = await handler.HandleWebMessageAsync(ReceiptRequest());

        Assert.NotNull(result);
        Assert.Equal(HttpMethod.Get, transport.Request!.Method);
        Assert.Equal(
            "https://central-pms.example.test/v1/webpay/statutory-applications/44444444-4444-4444-8444-444444444444/receipt-presentation?decisionCommandId=55555555-5555-4555-8555-555555555555&parkingSessionId=66666666-6666-4666-8666-666666666666",
            transport.Request.RequestUri!.ToString());
        Assert.Null(transport.Request.Content);
        Assert.Equal("ExitPass-HumanSession", transport.Request.Headers.Authorization!.Scheme);
        Assert.Equal("opaque-session-token", transport.Request.Headers.Authorization.Parameter);
        Assert.Equal(SiteId.ToString("D"), transport.Request.Headers.GetValues("X-Site-Id").Single());
        Assert.DoesNotContain("opaque-session-token", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatutoryReceiptPrintSubmitsVerifiedZeroPayableOriginalToNativePrinter()
    {
        var transport = new CapturingHandler(HttpStatusCode.OK, StatutoryReceiptPayload());
        var printer = new ControlledReceiptPrinter();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session-token")),
            receiptPrintingEnabled: true,
            receiptPrinterName: "TSC TTP-225",
            receiptPaperWidthMm: "57",
            receiptPrinter: printer);

        var result = await handler.HandleWebMessageAsync(ReceiptRequest(
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentationPrint));

        Assert.NotNull(result);
        using var response = JsonDocument.Parse(result);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var body = response.RootElement.GetProperty("payload").GetProperty("body");
        Assert.True(body.GetProperty("submitted").GetBoolean());
        Assert.Equal("TSC TTP-225", body.GetProperty("printerName").GetString());
        Assert.Equal("SI-00000073", body.GetProperty("fiscalDocumentNumber").GetString());
        Assert.Single(printer.SubmittedDocuments);
        var document = printer.SubmittedDocuments.Single();
        Assert.Null(document.TerminalCashTenderId);
        Assert.Equal(TerminalCashReceiptPrintClassification.Original, document.Classification);
        Assert.Equal("SI-00000073", document.FiscalDocumentNumber);
        Assert.Equal("14741195731701", document.AptTicketNumber);
        Assert.Contains("NOTHING FOLLOWS", document.Lines);
    }

    [Fact]
    public async Task StatutoryReceiptPrintRejectsPaymentAncestryAndDoesNotTouchPrinter()
    {
        var transport = new CapturingHandler(
            HttpStatusCode.OK,
            StatutoryReceiptPayload(paymentAttemptId: Guid.Parse("77777777-7777-4777-8777-777777777777")));
        var printer = new ControlledReceiptPrinter();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session-token")),
            receiptPrintingEnabled: true,
            receiptPrinterName: "TSC TTP-225",
            receiptPrinter: printer);

        var result = await handler.HandleWebMessageAsync(ReceiptRequest(
            CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentationPrint));

        Assert.Contains("STATUTORY_RECEIPT_NOT_AUTHORITATIVE", result, StringComparison.Ordinal);
        Assert.Empty(printer.SubmittedDocuments);
    }

    [Fact]
    public async Task MissingHumanSessionFailsClosedWithoutCallingCentralPms()
    {
        var transport = new CapturingHandler();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(null));

        var result = await handler.HandleWebMessageAsync(Request("payableBasis.resolve"));

        Assert.Null(transport.Request);
        Assert.Contains("HUMAN_SESSION_REQUIRED", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedCommandCannotBecomeAnAuthenticatedCentralPmsRequest()
    {
        var transport = new CapturingHandler();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session-token")));

        var result = await handler.HandleWebMessageAsync(Request("payment.create"));

        Assert.Null(transport.Request);
        Assert.Contains("INVALID_PAYABLE_BASIS_REQUEST", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserCannotChangeTheDeviceBoundSiteScope()
    {
        var transport = new CapturingHandler();
        var handler = new CentralPmsPayableBasisBridgeHandler(
            new HttpClient(transport),
            "https://central-pms.example.test",
            new StubAuthority(new CentralPmsRequestCredential(
                DeviceId,
                Guid.Parse("99999999-9999-4999-8999-999999999999"),
                "opaque-session-token")));

        var result = await handler.HandleWebMessageAsync(Request("payableBasis.resolve"));

        Assert.Null(transport.Request);
        Assert.Contains("FORBIDDEN_SITE", result, StringComparison.Ordinal);
    }

    private static string Request(string command) => JsonSerializer.Serialize(new
    {
        source = CentralPmsPayableBasisBridgeCommand.Source,
        command,
        correlationId = CorrelationId.ToString("D"),
        siteId = SiteId.ToString("D"),
        body = new { referenceType = "plate", plateNumber = "NO-SESSION" }
    });

    private static string ReceiptRequest(
        string command = CentralPmsPayableBasisBridgeCommand.StatutoryReceiptPresentation) => JsonSerializer.Serialize(new
    {
        source = CentralPmsPayableBasisBridgeCommand.Source,
        command,
        correlationId = CorrelationId.ToString("D"),
        siteId = SiteId.ToString("D"),
        body = new
        {
            applicationCommandId = "44444444-4444-4444-8444-444444444444",
            decisionCommandId = "55555555-5555-4555-8555-555555555555",
            parkingSessionId = "66666666-6666-4666-8666-666666666666"
        }
    });

    private static string StatutoryReceiptPayload(Guid? paymentAttemptId = null)
    {
        const string canonicalText = "SALES INVOICE\r\nSI No: SI-00000073\r\nTicket Number: 14741195731701\r\nPHP 0.00\r\nNOTHING FOLLOWS\r\n";
        var hash = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant()}";
        return JsonSerializer.Serialize(new
        {
            paymentAttemptId,
            paymentConfirmationId = (Guid?)null,
            fiscalIssuanceReferenceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2001",
            fiscalIssuanceState = "FISCAL_ISSUANCE_RECORDED",
            posFiscalDocumentId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb2001",
            fiscalDocumentNumber = "SI-00000073",
            fiscalDocumentStatus = "issued",
            receiptAvailabilityState = "AVAILABLE",
            authoritativePresentation = new
            {
                fiscalDocumentId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb2001",
                fiscalDocumentNumber = "SI-00000073",
                canonicalTextAuthority = "persisted_original_electronic_journal",
                canonicalText,
                canonicalTextHash = hash,
                presentation = new
                {
                    sections = new[]
                    {
                        new
                        {
                            rows = new[]
                            {
                                new
                                {
                                    key = "parkingPaymentReferences.ticketNumber",
                                    displayValue = "14741195731701"
                                }
                            }
                        }
                    }
                }
            },
            createdAt = "2026-10-09T06:02:23Z",
            updatedAt = "2026-10-09T06:02:24Z",
            correlationId = CorrelationId
        });
    }

    private sealed class StubAuthority(CentralPmsRequestCredential? credential) : ICentralPmsRequestAuthority
    {
        public Task<CentralPmsRequestCredential?> GetCurrentRequestCredentialAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);
    }

    private sealed class CapturingHandler(
        HttpStatusCode statusCode = HttpStatusCode.NotFound,
        string? responseBody = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                Request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (request.Content is not null)
            {
                Request.Content = new StringContent(
                    await request.Content.ReadAsStringAsync(cancellationToken),
                    Encoding.UTF8,
                    "application/json");
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody ?? "{\"errorCode\":\"SESSION_NOT_FOUND\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
