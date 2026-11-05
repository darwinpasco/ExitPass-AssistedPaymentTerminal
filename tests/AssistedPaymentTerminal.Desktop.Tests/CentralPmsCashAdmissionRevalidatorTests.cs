using System.Net;
using System.Net.Http;
using System.Text;
using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsCashAdmissionRevalidatorTests
{
    private static readonly Guid SiteId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SiteGroupId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid DeviceId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public async Task UnchangedOpenBasisIsAcceptedWithHostOwnedAuthority()
    {
        var transport = new ResponseHandler(HttpStatusCode.OK, Response(ready: true, "PASSED_UNCHANGED", 12500));
        var sut = Create(transport);

        var result = await sut.RevalidateAsync(Request(), Guid.NewGuid().ToString("D"), default);

        Assert.True(result.Allowed);
        Assert.Equal("ExitPass-HumanSession", transport.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("opaque-session", transport.Request.Headers.Authorization.Parameter);
        Assert.Equal(SiteId.ToString("D"), transport.Request.Headers.GetValues("X-Site-Id").Single());
    }

    [Theory]
    [InlineData(false, "PASSED_UNCHANGED", 12500)]
    [InlineData(true, "FISCAL_READINESS_FAILED", 12500)]
    [InlineData(true, "PASSED_UNCHANGED", 13000)]
    public async Task ClosedStaleOrChangedBasisIsRejected(bool ready, string outcome, long amount)
    {
        var sut = Create(new ResponseHandler(HttpStatusCode.OK, Response(ready, outcome, amount)));

        var result = await sut.RevalidateAsync(Request(), Guid.NewGuid().ToString("D"), default);

        Assert.False(result.Allowed);
        Assert.Equal("PAYABLE_BASIS_REVALIDATION_FAILED", result.Code);
    }

    [Fact]
    public async Task CentralPmsConflictFailsClosedAsBusinessDateNotOpen()
    {
        var sut = Create(new ResponseHandler(HttpStatusCode.Conflict, "{\"code\":\"FISCAL_BUSINESS_DATE_NOT_OPEN\"}"));

        var result = await sut.RevalidateAsync(Request(), Guid.NewGuid().ToString("D"), default);

        Assert.False(result.Allowed);
        Assert.Equal("FISCAL_BUSINESS_DATE_NOT_OPEN", result.Code);
    }

    private static CentralPmsCashAdmissionRevalidator Create(HttpMessageHandler transport) => new(
        new HttpClient(transport),
        "https://central-pms.example.test",
        new TestAuthority(new CentralPmsRequestCredential(DeviceId, SiteId, "opaque-session")));

    private static PayableBasisRevalidationPayload Request() => new(
        "parking-session-1", "tariff-1", SiteGroupId, SiteId, "pos-1", "apt-1", "hikcentral",
        "ticket-1", "ABC1234", 12500, "PHP", null);

    private static string Response(bool ready, string outcome, long amount) => $$"""
        {"readyForCashAcceptance":{{ready.ToString().ToLowerInvariant()}},"revalidationOutcome":"{{outcome}}","parkingSessionId":"parking-session-1","tariffSnapshotId":"tariff-1","authoritativeAmountMinorUnits":{{amount}},"currency":"PHP"}
        """;

    private sealed class TestAuthority(CentralPmsRequestCredential? credential) : ICentralPmsRequestAuthority
    {
        public Task<CentralPmsRequestCredential?> GetCurrentRequestCredentialAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
