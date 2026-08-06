using System.Net;
using System.Text;
using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsStatutoryEvidenceClientTests
{
    private static readonly Guid ServiceIdentityId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CorrelationId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid DecisionId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public async Task BootstrapUsesExactAptRouteAndHostOwnedServiceIdentity()
    {
        var handler = new RecordingHandler(ChannelResponseJson());
        var client = CreateClient(handler);

        var result = await client.BootstrapAsync(DecisionId, "synthetic-operation", CorrelationId, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal("/v1/apt/statutory-discounts/evidence/bootstrap", handler.RequestUri?.AbsolutePath);
        Assert.Equal(ServiceIdentityId.ToString("D"), handler.Headers["X-ExitPass-Service-Identity-Id"]);
        Assert.Equal(CorrelationId.ToString("D"), handler.Headers["X-Correlation-Id"]);
        Assert.DoesNotContain("Authorization", handler.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-ExitPass-Permissions", handler.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingServiceIdentityFailsBeforeNetworkRequest()
    {
        var handler = new RecordingHandler(ChannelResponseJson());
        var client = new CentralPmsStatutoryEvidenceClient(
            new HttpClient(handler),
            "https://central-pms.invalid",
            serviceIdentityId: null);

        var result = await client.GetStatusAsync(DecisionId, CorrelationId, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("APT_EVIDENCE_SERVICE_AUTH_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AccessDeniedIsMappedWithoutLeakingRawResponse()
    {
        var handler = new RecordingHandler(
            "{\"error\":\"sensitive internal authorization detail\"}",
            HttpStatusCode.Forbidden);
        var client = CreateClient(handler);

        var result = await client.GetStatusAsync(DecisionId, CorrelationId, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("ACCESS_DENIED", result.ErrorCode);
        Assert.Equal("Central PMS denied the secure APT evidence operation.", result.SafeMessage);
        Assert.DoesNotContain("sensitive", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedSuccessfulResponseFailsClosed()
    {
        var handler = new RecordingHandler("{\"classification\":\"RESOLVED\"}");
        var client = CreateClient(handler);

        var result = await client.GetStatusAsync(DecisionId, CorrelationId, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("MALFORMED_AUTHORITATIVE_STATE", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task UploadStreamsOnlyToOpaqueAptRoute()
    {
        var uploadSessionReference = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var handler = new RecordingHandler(UploadResponseJson(uploadSessionReference));
        var client = CreateClient(handler);
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var result = await client.UploadAsync(
            uploadSessionReference,
            content,
            "image/png",
            4,
            CorrelationId,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Put, handler.RequestMethod);
        Assert.Equal(
            $"/v1/apt/statutory-discounts/evidence/upload-sessions/{uploadSessionReference:D}",
            handler.RequestUri?.AbsolutePath);
        Assert.Equal("central-pms.invalid", handler.RequestUri?.Host);
        Assert.Equal("image/png", handler.ContentType);
        Assert.Equal(4, handler.ContentLength);
        Assert.Equal([1, 2, 3, 4], handler.RequestBody);
        Assert.DoesNotContain("minio", handler.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3", handler.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SOURCE_UNAVAILABLE", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, "SOURCE_UNAVAILABLE", true)]
    [InlineData(HttpStatusCode.InternalServerError, "REQUEST_REJECTED", false)]
    public async Task FailureResponsesUseSafeClassifications(
        HttpStatusCode statusCode,
        string expectedCode,
        bool expectedRetryable)
    {
        var handler = new RecordingHandler("not-json", statusCode);
        var client = CreateClient(handler);

        var result = await client.RevalidateAsync(DecisionId, CorrelationId, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(expectedRetryable, result.Retryable);
        Assert.DoesNotContain("not-json", result.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    private static CentralPmsStatutoryEvidenceClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            "https://central-pms.invalid",
            ServiceIdentityId.ToString("D"));

    private static string ChannelResponseJson() => $$"""
        {
          "classification": "RESOLVED",
          "retryable": false,
          "errorCode": null,
          "correlationId": "{{CorrelationId:D}}",
          "sourceChannel": "APT",
          "evidenceRequired": true,
          "evidenceSetReference": "55555555-5555-4555-8555-555555555555",
          "evidenceItemReference": "66666666-6666-4666-8666-666666666666",
          "allowedContentTypes": ["image/jpeg", "image/png"],
          "maximumContentLengthBytes": 5242880,
          "maximumImageWidth": 4096,
          "maximumImageHeight": 4096,
          "maximumImagePixelCount": 12000000,
          "requiredDocumentType": "STATUTORY_ID",
          "requiredItemRole": "IDENTITY_EVIDENCE",
          "lifecycleClassification": "REQUIRED_NOT_STARTED",
          "replacementPosture": "REPLACEMENT_ALLOWED",
          "readyForReview": false,
          "readyForAptPreCash": false,
          "blockingReasonCode": "EVIDENCE_REQUIRED",
          "evaluatedAt": "2026-08-05T00:00:00Z"
        }
        """;

    private static string UploadResponseJson(Guid uploadSessionReference) => $$"""
        {
          "classification": "ACCEPTED",
          "retryable": false,
          "errorCode": null,
          "correlationId": "{{CorrelationId:D}}",
          "opaqueUploadSessionReference": "{{uploadSessionReference:D}}",
          "method": "PUT",
          "expiresAt": "2026-08-05T00:05:00Z",
          "acceptedContentType": "image/png",
          "maximumContentLengthBytes": 5242880
        }
        """;

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode responseStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ContentType { get; private set; }
        public long? ContentLength { get; private set; }
        public byte[] RequestBody { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content is not null)
            {
                ContentType = request.Content.Headers.ContentType?.MediaType;
                ContentLength = request.Content.Headers.ContentLength;
                RequestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            return new HttpResponseMessage(responseStatus)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
