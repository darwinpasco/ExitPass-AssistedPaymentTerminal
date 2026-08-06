using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class StatutoryEvidenceBridgeHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"exitpass-j006-{Guid.NewGuid():N}");
    private readonly Guid _decisionId = Guid.Parse("77777777-7777-4777-8777-777777770777");

    public StatutoryEvidenceBridgeHandlerTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("synthetic.jpg", "image/jpeg")]
    [InlineData("synthetic.png", "image/png")]
    public async Task SelectFileReturnsOnlySafeTransientMetadata(string fileName, string contentType)
    {
        var path = WriteFile(fileName, [1, 2, 3, 4]);
        var client = new FakeEvidenceClient();
        var handler = new StatutoryEvidenceBridgeHandler(
            client,
            new FakePicker(new StatutoryEvidenceFileCandidate(path, fileName, contentType, 4)));

        var response = await SendAsync(handler, StatutoryEvidenceBridgeCommand.SelectFile, new { statutoryDiscountDecisionCommandId = _decisionId });

        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var json = response.RootElement.GetRawText();
        Assert.Contains(fileName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checksum", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucket", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("synthetic.pdf", "application/pdf", 4, "UNSUPPORTED_MEDIA_TYPE")]
    [InlineData("empty.jpg", "image/jpeg", 0, "EMPTY_FILE")]
    [InlineData("large.jpg", "image/jpeg", 6_000_000, "FILE_TOO_LARGE")]
    public async Task SelectFileRejectsInvalidLocalCandidate(string fileName, string contentType, long length, string expectedCode)
    {
        var path = WriteFile(fileName, length > 0 ? [1] : []);
        var handler = new StatutoryEvidenceBridgeHandler(
            new FakeEvidenceClient(),
            new FakePicker(new StatutoryEvidenceFileCandidate(path, fileName, contentType, length)));

        var response = await SendAsync(handler, StatutoryEvidenceBridgeCommand.SelectFile, new { statutoryDiscountDecisionCommandId = _decisionId });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UploadUsesOpaqueSessionAndStreamsOriginalFileWithoutPublicChecksum()
    {
        var bytes = Enumerable.Range(0, 2048).Select(value => (byte)(value % 251)).ToArray();
        var path = WriteFile("synthetic.jpg", bytes);
        var client = new FakeEvidenceClient();
        var handler = new StatutoryEvidenceBridgeHandler(
            client,
            new FakePicker(new StatutoryEvidenceFileCandidate(path, "synthetic.jpg", "image/jpeg", bytes.Length)));
        var selected = await SendAsync(handler, StatutoryEvidenceBridgeCommand.SelectFile, new { statutoryDiscountDecisionCommandId = _decisionId });
        var selectionReference = selected.RootElement.GetProperty("payload").GetProperty("selectionReference").GetGuid();

        var authorized = await SendAsync(handler, StatutoryEvidenceBridgeCommand.CreateUploadSession, new
        {
            statutoryDiscountDecisionCommandId = _decisionId,
            selectionReference,
            clientOperationKey = "synthetic-operation"
        });
        var uploadReference = authorized.RootElement.GetProperty("payload").GetProperty("opaqueUploadSessionReference").GetGuid();
        var uploaded = await SendAsync(handler, StatutoryEvidenceBridgeCommand.Upload, new { opaqueUploadSessionReference = uploadReference });

        Assert.True(uploaded.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(bytes, client.UploadedBytes);
        Assert.NotNull(client.LastUploadSessionRequest?.DeclaredChecksumSha256);
        Assert.DoesNotContain(client.LastUploadSessionRequest!.DeclaredChecksumSha256, uploaded.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task FinalizationDropsTransientFileAuthorityAndReplayRemainsServerOwned()
    {
        var path = WriteFile("synthetic.png", [8, 9, 10]);
        var client = new FakeEvidenceClient();
        var handler = new StatutoryEvidenceBridgeHandler(
            client,
            new FakePicker(new StatutoryEvidenceFileCandidate(path, "synthetic.png", "image/png", 3)));
        var selected = await SendAsync(handler, StatutoryEvidenceBridgeCommand.SelectFile, new { statutoryDiscountDecisionCommandId = _decisionId });
        var selectionReference = selected.RootElement.GetProperty("payload").GetProperty("selectionReference").GetGuid();
        var authorized = await SendAsync(handler, StatutoryEvidenceBridgeCommand.CreateUploadSession, new { statutoryDiscountDecisionCommandId = _decisionId, selectionReference, clientOperationKey = "operation" });
        var uploadReference = authorized.RootElement.GetProperty("payload").GetProperty("opaqueUploadSessionReference").GetGuid();

        var finalized = await SendAsync(handler, StatutoryEvidenceBridgeCommand.Finalize, new { opaqueUploadSessionReference = uploadReference, clientOperationKey = "finalize" });
        var staleSelection = await SendAsync(handler, StatutoryEvidenceBridgeCommand.CreateUploadSession, new { statutoryDiscountDecisionCommandId = _decisionId, selectionReference, clientOperationKey = "operation" });

        Assert.True(finalized.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("VALIDATION_PENDING", finalized.RootElement.GetProperty("payload").GetProperty("lifecycleClassification").GetString());
        Assert.False(staleSelection.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("FILE_RESELECTION_REQUIRED", staleSelection.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RevalidateReturnsAuthoritativeAppliedReadiness()
    {
        var client = new FakeEvidenceClient { Status = FakeEvidenceClient.Channel("APPLIED", readyForCash: true) };
        var handler = new StatutoryEvidenceBridgeHandler(client, new FakePicker(null));

        var response = await SendAsync(handler, StatutoryEvidenceBridgeCommand.Revalidate, new { statutoryDiscountDecisionCommandId = _decisionId });

        Assert.True(response.RootElement.GetProperty("payload").GetProperty("readyForAptPreCash").GetBoolean());
        Assert.Equal("APPLIED", response.RootElement.GetProperty("payload").GetProperty("lifecycleClassification").GetString());
    }

    [Fact]
    public async Task MissingDecisionAndUnknownCommandsFailSafely()
    {
        var handler = new StatutoryEvidenceBridgeHandler(new FakeEvidenceClient(), new FakePicker(null));
        var invalid = await SendAsync(handler, StatutoryEvidenceBridgeCommand.Bootstrap, new { statutoryDiscountDecisionCommandId = Guid.Empty });
        var unknown = await SendAsync(handler, "statutoryEvidence.providerList", new { });

        Assert.Equal("INVALID_DECISION_REFERENCE", invalid.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("UNSUPPORTED_COMMAND", unknown.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("stack", invalid.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedClientFailureDoesNotEscapeOrExposeExceptionText()
    {
        var handler = new StatutoryEvidenceBridgeHandler(new ThrowingEvidenceClient(), new FakePicker(null));

        var response = await SendAsync(handler, StatutoryEvidenceBridgeCommand.Bootstrap, new { statutoryDiscountDecisionCommandId = _decisionId });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("UNEXPECTED_FAILURE", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("sensitive", response.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", response.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<JsonDocument> SendAsync(StatutoryEvidenceBridgeHandler handler, string command, object payload)
    {
        var correlationId = Guid.NewGuid().ToString("D");
        var request = JsonSerializer.Serialize(new { source = StatutoryEvidenceBridgeCommand.Source, command, correlationId, payload });
        var response = await handler.HandleWebMessageAsync(request);
        return JsonDocument.Parse(Assert.IsType<string>(response));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakePicker(StatutoryEvidenceFileCandidate? candidate) : IStatutoryEvidenceFilePicker
    {
        public StatutoryEvidenceFileCandidate? SelectSingleImage() => candidate;
    }

    private sealed class FakeEvidenceClient : ICentralPmsStatutoryEvidenceClient
    {
        private static readonly Guid UploadReference = Guid.Parse("44444444-4444-4444-8444-444444440001");
        public StatutoryEvidenceChannelResponse Status { get; set; } = Channel("REQUIRED_NOT_STARTED", readyForCash: false);
        public StatutoryEvidenceUploadSessionRequest? LastUploadSessionRequest { get; private set; }
        public byte[] UploadedBytes { get; private set; } = [];

        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> BootstrapAsync(Guid decisionCommandId, string? clientOperationKey, Guid correlationId, CancellationToken cancellationToken) => Success(Status);
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> GetStatusAsync(Guid decisionCommandId, Guid correlationId, CancellationToken cancellationToken) => Success(Status);
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> RevalidateAsync(Guid decisionCommandId, Guid correlationId, CancellationToken cancellationToken) => Success(Status);

        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> CreateUploadSessionAsync(StatutoryEvidenceUploadSessionRequest request, Guid correlationId, CancellationToken cancellationToken)
        {
            LastUploadSessionRequest = request;
            return Success(new StatutoryEvidenceUploadSessionResponse("ISSUED", false, null, correlationId, UploadReference, "PUT", DateTimeOffset.UtcNow.AddMinutes(5), request.DeclaredContentType, 5_000_000));
        }

        public async Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> UploadAsync(Guid opaqueUploadSessionReference, Stream content, string contentType, long contentLength, Guid correlationId, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            UploadedBytes = buffer.ToArray();
            return StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>.Success(new("ACCEPTED", false, null, correlationId, UploadReference, "PUT", DateTimeOffset.UtcNow.AddMinutes(5), contentType, 5_000_000));
        }

        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> FinalizeAsync(Guid opaqueUploadSessionReference, string? clientOperationKey, Guid correlationId, CancellationToken cancellationToken) =>
            Success(Channel("VALIDATION_PENDING", readyForCash: false));

        public static StatutoryEvidenceChannelResponse Channel(string lifecycle, bool readyForCash) => new(
            "RESOLVED", false, null, Guid.NewGuid(), "ASSISTED_PAYMENT_TERMINAL", true,
            Guid.Parse("11111111-1111-4111-8111-111111110001"), Guid.Parse("22222222-2222-4222-8222-222222220001"),
            ["image/jpeg", "image/png"], 5_000_000, 4096, 4096, 16_000_000, "STATUTORY_ID_IMAGE", "PRIMARY_IDENTITY_EVIDENCE",
            lifecycle, lifecycle == "APPLIED" ? "REPLACEMENT_NOT_ALLOWED" : "REPLACEMENT_ALLOWED", lifecycle is "REVIEWABLE" or "APPLIED", readyForCash,
            readyForCash ? null : "STATUTORY_EVIDENCE_NOT_READY", DateTimeOffset.UtcNow);

        private static Task<StatutoryEvidenceClientResult<T>> Success<T>(T payload) =>
            Task.FromResult(StatutoryEvidenceClientResult<T>.Success(payload));
    }

    private sealed class ThrowingEvidenceClient : ICentralPmsStatutoryEvidenceClient
    {
        private static Task<StatutoryEvidenceClientResult<T>> Throw<T>() =>
            throw new InvalidOperationException("sensitive provider exception");

        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> BootstrapAsync(Guid decisionCommandId, string? clientOperationKey, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceChannelResponse>();
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> GetStatusAsync(Guid decisionCommandId, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceChannelResponse>();
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> RevalidateAsync(Guid decisionCommandId, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceChannelResponse>();
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> CreateUploadSessionAsync(StatutoryEvidenceUploadSessionRequest request, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceUploadSessionResponse>();
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceUploadSessionResponse>> UploadAsync(Guid opaqueUploadSessionReference, Stream content, string contentType, long contentLength, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceUploadSessionResponse>();
        public Task<StatutoryEvidenceClientResult<StatutoryEvidenceChannelResponse>> FinalizeAsync(Guid opaqueUploadSessionReference, string? clientOperationKey, Guid correlationId, CancellationToken cancellationToken) => Throw<StatutoryEvidenceChannelResponse>();
    }
}
