using System.Net;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
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

    private sealed class StubAuthority(CentralPmsRequestCredential? credential) : ICentralPmsRequestAuthority
    {
        public Task<CentralPmsRequestCredential?> GetCurrentRequestCredentialAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);
    }

    private sealed class CapturingHandler : HttpMessageHandler
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

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"errorCode\":\"SESSION_NOT_FOUND\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
