using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;

var proofOptions = PreviewProofArguments.Parse(args);
var databasePath = proofOptions.DatabasePath
    ?? Path.Combine(Path.GetTempPath(), $"exitpass-apt-receipt-preview-proof-{Guid.NewGuid():N}.db");
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fullDatabasePath = Path.GetFullPath(databasePath);
if (fullDatabasePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Proof database path must be outside the Git repository.");
}

if (proofOptions.Interactive)
{
    var tenderId = await SeedScenarioAsync(fullDatabasePath, proofOptions.Scenario, useResolvedTicketReference: true).ConfigureAwait(false);
    var seededDatabaseExists = File.Exists(fullDatabasePath);
    Require(seededDatabaseExists, "Interactive proof database was not preserved after seeding.");
    Console.WriteLine($"Selected scenario: {proofOptions.Scenario}");
    Console.WriteLine($"Temporary database: {fullDatabasePath}");
    Console.WriteLine("Set the following environment for WPF/WebView2 manual validation:");
    Console.WriteLine($"$env:APT_LOCAL_DB_PATH = \"{fullDatabasePath}\"");
    Console.WriteLine("$env:APT_ENABLE_NON_LIVE_CASH_CAPTURE = \"true\"");
    Console.WriteLine("$env:APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION = \"true\"");
    Console.WriteLine("$env:APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE = \"true\"");
    Console.WriteLine("$env:APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL = \"true\"");
    Console.WriteLine("$env:APT_ENABLE_RECEIPT_PREVIEW = \"true\"");
    Console.WriteLine($"$env:APT_RECEIPT_PAPER_WIDTH_MM = \"{proofOptions.PaperWidthMm ?? "57"}\"");
    Console.WriteLine("$env:CENTRAL_PMS_BASE_URL = \"http://127.0.0.1:9\"");
    Console.WriteLine("Launch:");
    Console.WriteLine("dotnet run --project src\\AssistedPaymentTerminal.Desktop -- --profile=CASHIER_ASSISTED_TERMINAL --packaged-assets");
    Console.WriteLine($"Seeded terminal cash tender: {tenderId}");
    Console.WriteLine($"Seeded database exists: {seededDatabaseExists}");
    Console.WriteLine("Copy-ready validation command:");
    Console.WriteLine("Test-Path \"$env:APT_LOCAL_DB_PATH\"");
    Console.WriteLine("Cleanup command for after manual testing:");
    Console.WriteLine($"Remove-Item \"{fullDatabasePath}*\" -Force -ErrorAction SilentlyContinue");
    Console.WriteLine("No local host is required. The preview uses the persisted authoritative snapshot only.");
    return;
}

try
{
    await RunAutomatedProofAsync(fullDatabasePath).ConfigureAwait(false);
}
finally
{
    DeleteIfExists(fullDatabasePath);
    DeleteIfExists($"{fullDatabasePath}-wal");
    DeleteIfExists($"{fullDatabasePath}-shm");
}

static async Task RunAutomatedProofAsync(string databasePath)
{
    var availableTenderId = await SeedScenarioAsync(databasePath, ReceiptPreviewProofScenario.Available).ConfigureAwait(false);
    var first = await PreviewAsync(databasePath, availableTenderId, receiptPreviewEnabled: true, paperWidthMm: null).ConfigureAwait(false);
    Require(!first.GetProperty("ok").GetBoolean(), "Incomplete authoritative preview unexpectedly succeeded.");
    Require(
        first.GetProperty("error").GetProperty("code").GetString() == "receipt_preview_incomplete_authoritative_payload",
        "Incomplete authoritative payload did not block preview.");
    Require(!first.GetRawText().Contains("[REGISTERED BUSINESS NAME]", StringComparison.Ordinal), "Placeholder leaked into blocked preview response.");
    var storedPayload = await ReadAuthoritativePayloadAsync(databasePath, availableTenderId).ConfigureAwait(false);
    Require(!storedPayload.Contains("[REGISTERED BUSINESS NAME]", StringComparison.Ordinal), "Placeholder was written into the persisted authoritative payload.");

    var completeTenderId = await SeedScenarioAsync(databasePath, ReceiptPreviewProofScenario.Complete).ConfigureAwait(false);
    var complete = await PreviewAsync(databasePath, completeTenderId, receiptPreviewEnabled: true, paperWidthMm: "57").ConfigureAwait(false);
    Require(complete.GetProperty("ok").GetBoolean(), "Complete authoritative preview did not succeed.");
    var completePreview = complete.GetProperty("payload").GetProperty("preview");
    var completeSections = completePreview.GetProperty("sections").GetRawText();
    Require(!completePreview.GetProperty("hasPlaceholders").GetBoolean(), "Complete authoritative fixture still reported placeholders.");
    Require(completePreview.GetProperty("configurationCompleteness").GetString() == "Complete", "Complete authoritative fixture did not report complete configuration.");
    Require(completeSections.Contains("GOVERNED REGISTERED BUSINESS NAME", StringComparison.Ordinal), "Actual governed registered business value was not displayed.");
    Require(completeSections.Contains("GOVERNED BIR ACCREDITATION DATE ISSUED", StringComparison.Ordinal), "Actual BIR accreditation issued date was not displayed.");
    Require(completeSections.Contains("GOVERNED BIR ACCREDITATION VALID UNTIL", StringComparison.Ordinal), "Actual BIR accreditation valid-until date was not displayed.");
    Require(completeSections.Contains("GOVERNED PTU DATE ISSUED", StringComparison.Ordinal), "Actual PTU issued date was not displayed.");
    Require(!completeSections.Contains("[REGISTERED BUSINESS NAME]", StringComparison.Ordinal), "Placeholder was shown alongside actual registered business value.");
    Require(!completeSections.Contains("[BIR ACCREDITATION VALID UNTIL]", StringComparison.Ordinal), "BIR validity placeholder was shown alongside actual value.");

    var hash = completePreview.GetProperty("authoritativePayloadHash").GetString();
    foreach (var width in new string?[] { "57", "58", "80", "99" })
    {
        var preview = await PreviewAsync(databasePath, completeTenderId, receiptPreviewEnabled: true, paperWidthMm: width).ConfigureAwait(false);
        Require(preview.GetProperty("ok").GetBoolean(), $"Width {width ?? "missing"} preview failed.");
        var payload = preview.GetProperty("payload");
        var expectedWidth = width == "58" ? 58 : width == "80" ? 80 : 57;
        Require(payload.GetProperty("paperProfile").GetProperty("paperWidthMm").GetInt32() == expectedWidth, $"Width {width ?? "missing"} did not select the expected profile.");
        Require(payload.GetProperty("preview").GetProperty("authoritativePayloadHash").GetString() == hash, "Paper-width selection changed the authoritative payload hash.");
        Require(payload.GetProperty("preview").GetProperty("sections").GetRawText() == completeSections, "Paper-width selection altered receipt facts.");
    }

    var reopened = await PreviewAsync(databasePath, completeTenderId, receiptPreviewEnabled: true, paperWidthMm: "57").ConfigureAwait(false);
    Require(reopened.GetProperty("ok").GetBoolean(), "Restart-style preview did not use the persisted payload.");
    Require(await CountReceiptCommandsAsync(databasePath, completeTenderId).ConfigureAwait(false) == 1, "Preview created a duplicate receipt-retrieval record.");

    var disabled = await PreviewAsync(databasePath, completeTenderId, receiptPreviewEnabled: false, paperWidthMm: "57").ConfigureAwait(false);
    Require(!disabled.GetProperty("ok").GetBoolean(), "Disabled preview unexpectedly succeeded.");

    await ExpectBlockedAsync(databasePath, ReceiptPreviewProofScenario.UnsupportedVersion, "receipt_preview_unsupported_version").ConfigureAwait(false);
    await ExpectBlockedAsync(databasePath, ReceiptPreviewProofScenario.PayloadHashMismatch, "receipt_preview_integrity_failed").ConfigureAwait(false);
    await ExpectBlockedAsync(databasePath, ReceiptPreviewProofScenario.MalformedPayload, "receipt_preview_decode_failed").ConfigureAwait(false);

    var voidedTenderId = await SeedScenarioAsync(databasePath, ReceiptPreviewProofScenario.Voided).ConfigureAwait(false);
    var voided = await PreviewAsync(databasePath, voidedTenderId, receiptPreviewEnabled: true, paperWidthMm: "57").ConfigureAwait(false);
    Require(voided.GetProperty("ok").GetBoolean(), "Voided preview did not succeed.");
    Require(voided.GetProperty("payload").GetProperty("preview").GetProperty("voided").GetBoolean(), "Voided posture was not explicit.");
    Require(await ReceiptStatusIsPreviewReachableAsync(databasePath, voidedTenderId).ConfigureAwait(false), "Voided scenario did not preserve the prerequisite chain to preview.");

    Console.WriteLine("Available receipt snapshot exists.");
    Console.WriteLine("Preview action can decode the approved Sales Invoice structure.");
    Console.WriteLine("Corrected BIR accreditation and PTU registration fields are represented distinctly.");
    Console.WriteLine("Missing statutory, site, terminal, parking, and transaction fields block preview without rendering placeholders.");
    Console.WriteLine("Placeholders are not inserted into the stored authoritative payload, preview response, or payload hash.");
    Console.WriteLine("Actual governed values replace placeholders and report complete configuration.");
    Console.WriteLine("Receipt preview maps governed fields to customer-facing labels without raw contract property labels.");
    Console.WriteLine("Lines, taxes, totals, and CASH tender are returned through the preview model.");
    Console.WriteLine("Raw JSON is not returned to React.");
    Console.WriteLine("Payload hash is validated before preview.");
    Console.WriteLine("Restart uses the same payload without receipt retrieval.");
    Console.WriteLine("Unsupported version, hash mismatch, and malformed payload are blocked.");
    Console.WriteLine("Voided fiscal posture is explicit.");
    Console.WriteLine("57 mm is the default; 57, 58, and 80 mm profiles are controlled; unsupported width falls back to 57 mm.");
    Console.WriteLine("Receipt data and payload hash remain unchanged across width profiles.");
    Console.WriteLine("No print job, printed state, receipt network retrieval, exit, gate, provider, Central PMS, or POS Server behavior occurred during preview.");
    Console.WriteLine("Central PMS cash receipt preview UI proof completed successfully.");
}

static async Task ExpectBlockedAsync(string databasePath, ReceiptPreviewProofScenario scenario, string expectedCode)
{
    var tenderId = await SeedScenarioAsync(databasePath, scenario).ConfigureAwait(false);
    Require(await ReceiptStatusIsPreviewReachableAsync(databasePath, tenderId).ConfigureAwait(false), $"{scenario} did not preserve the prerequisite chain to preview.");
    var preview = await PreviewAsync(databasePath, tenderId, receiptPreviewEnabled: true, paperWidthMm: "57").ConfigureAwait(false);
    Require(!preview.GetProperty("ok").GetBoolean(), $"{scenario} preview unexpectedly succeeded.");
    Require(preview.GetProperty("error").GetProperty("code").GetString() == expectedCode, $"{scenario} did not map to {expectedCode}.");
}

static async Task<bool> ReceiptStatusIsPreviewReachableAsync(string databasePath, Guid tenderId)
{
    await using var dbContext = new CashJournalService(Options(databasePath)).CreateDbContext();
    var receipt = await dbContext.TerminalCashReceiptRetrievalCommands
        .SingleOrDefaultAsync(command => command.TerminalCashTenderId == tenderId).ConfigureAwait(false);
    if (receipt is null
        || receipt.Status is not (TerminalCashReceiptRetrievalStatus.Available or TerminalCashReceiptRetrievalStatus.Voided)
        || string.IsNullOrWhiteSpace(receipt.AuthoritativePresentationJson))
    {
        return false;
    }

    var fiscal = await dbContext.TerminalCashFiscalOutboxCommands
        .SingleOrDefaultAsync(command => command.TerminalCashTenderId == tenderId).ConfigureAwait(false);
    var payment = await dbContext.TerminalCashPaymentOutboxCommands
        .SingleOrDefaultAsync(command => command.TerminalCashTenderId == tenderId).ConfigureAwait(false);

    return fiscal?.Status == TerminalCashFiscalCommandStatus.Recorded
        && payment?.Status == TerminalCashPaymentCommandStatus.Confirmed
        && receipt.CanonicalPaymentAttemptId != Guid.Empty
        && receipt.CanonicalPaymentConfirmationId != Guid.Empty
        && receipt.FiscalIssuanceReferenceId != Guid.Empty
        && receipt.PosFiscalDocumentId != Guid.Empty;
}

static async Task<Guid> SeedScenarioAsync(
    string databasePath,
    ReceiptPreviewProofScenario scenario,
    bool useResolvedTicketReference = false)
{
    var options = Options(databasePath);
    var journal = new CashJournalService(options);
    var session = await journal.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
        CashierId: "cashier-preview",
        AuthenticatedCashierSessionReference: "auth-preview",
        CashierShiftId: "shift-preview",
        TerminalId: "terminal-preview",
        SiteId: "11111111-1111-4111-8111-111111111111",
        SiteGroupId: "22222222-2222-4222-8222-222222222222",
        PosServerId: "pos-preview",
        OpeningCashAmount: 0m)).ConfigureAwait(false);
    Require(session.IsSuccess, "Cash-custody session was not created.");

    var parkingSessionId = useResolvedTicketReference || scenario == ReceiptPreviewProofScenario.Available
        ? "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001"
        : Guid.NewGuid().ToString("D");
    var tender = await journal.StartCashTenderAsync(new StartCashTenderRequest(
        session.Value!.Id,
        ParkingSessionId: parkingSessionId,
        TariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
        Currency: "PHP",
        AmountDue: 125m,
        AmountTendered: 150m,
        CorrelationId: Guid.NewGuid().ToString("D"),
        LocalIdempotencyIdentity: $"preview-proof:{scenario}:{Guid.NewGuid():N}")).ConfigureAwait(false);
    Require(tender.IsSuccess, "Cash tender was not created.");

    var received = await journal.CommitCashReceivedAsync(new CommitCashReceivedRequest(
        tender.Value!.Id,
        CashierAttested: true,
        Denominations: [new CashDenominationLine("PHP-100", 100m, 1)],
        CentralPmsTarget: "http://127.0.0.1:9")).ConfigureAwait(false);
    Require(received.IsSuccess, "CASH_RECEIVED was not committed.");

    var paymentCommand = await journal.GetTerminalCashPaymentOutboxCommandByTenderAsync(tender.Value.Id).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Cash-payment outbox command was not created.");
    await new TerminalCashPaymentSubmissionService(new PreviewCashClient(), options)
        .SubmitOrReadbackAsync(paymentCommand.Id).ConfigureAwait(false);

    var fiscal = await new TerminalCashFiscalSubmissionService(new PreviewFiscalClient(), options)
        .GetFiscalCommandByTenderAsync(paymentCommand.TerminalCashTenderId).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Fiscal command was not created.");
    await new TerminalCashFiscalSubmissionService(new PreviewFiscalClient(), options)
        .SubmitOrReadbackFiscalAsync(fiscal.Id).ConfigureAwait(false);

    var receipt = await new TerminalCashReceiptRetrievalService(
            new PreviewReceiptClient(scenario),
            options)
        .EnsureForRecordedFiscalAsync(paymentCommand.TerminalCashTenderId).ConfigureAwait(false);
    var retrieved = await new TerminalCashReceiptRetrievalService(
            new PreviewReceiptClient(scenario),
            options)
        .RetrieveReceiptAsync(receipt.Id).ConfigureAwait(false);

    await ApplyScenarioMutationAsync(databasePath, retrieved.TerminalCashTenderId, scenario).ConfigureAwait(false);
    return retrieved.TerminalCashTenderId;
}

static async Task ApplyScenarioMutationAsync(string databasePath, Guid terminalCashTenderId, ReceiptPreviewProofScenario scenario)
{
    if (scenario is ReceiptPreviewProofScenario.Available or ReceiptPreviewProofScenario.Voided or ReceiptPreviewProofScenario.Complete)
    {
        return;
    }

    await using var dbContext = new CashJournalService(Options(databasePath)).CreateDbContext();
    var command = await dbContext.TerminalCashReceiptRetrievalCommands
        .SingleAsync(value => value.TerminalCashTenderId == terminalCashTenderId).ConfigureAwait(false);

    switch (scenario)
    {
        case ReceiptPreviewProofScenario.UnsupportedVersion:
            command.PresentationVersion = "digital-sales-invoice-presentation-json-v2";
            break;
        case ReceiptPreviewProofScenario.PayloadHashMismatch:
            command.AuthoritativePayloadHash = "sha256:mismatch";
            break;
        case ReceiptPreviewProofScenario.MalformedPayload:
            command.AuthoritativePresentationJson = "{\"presentation\":";
            command.AuthoritativePayloadHash = TerminalCashReceiptPayloadFactory.ComputeHash(command.AuthoritativePresentationJson);
            break;
    }

    command.UpdatedAt = DateTimeOffset.UtcNow;
    await dbContext.SaveChangesAsync().ConfigureAwait(false);
}

static async Task<JsonElement> PreviewAsync(string databasePath, Guid terminalCashTenderId, bool receiptPreviewEnabled, string? paperWidthMm)
{
    var options = Options(databasePath);
    var handler = new LocalJournalBridgeHandler(
        new CashJournalService(options),
        enabled: true,
        centralPmsCashSubmissionEnabled: true,
        centralPmsFiscalIssuanceEnabled: true,
        centralPmsReceiptRetrievalEnabled: true,
        receiptPreviewEnabled: receiptPreviewEnabled,
        receiptPaperWidthMm: paperWidthMm,
        centralPmsBaseUrl: "http://127.0.0.1:9",
        submissionService: new TerminalCashPaymentSubmissionService(new NoNetworkCashClient(), options),
        fiscalService: new TerminalCashFiscalSubmissionService(new NoNetworkFiscalClient(), options),
        receiptService: new TerminalCashReceiptRetrievalService(new NoNetworkReceiptClient(), options));

    using var response = await SendAsync(handler, LocalJournalBridgeCommand.CentralPmsCashReceiptGetPreview, Guid.NewGuid().ToString("D"), new
    {
        localCashTenderId = terminalCashTenderId
    }).ConfigureAwait(false);
    return response.RootElement.Clone();
}

static async Task<JsonDocument> SendAsync(LocalJournalBridgeHandler handler, string command, string correlationId, object payload)
{
    var request = JsonSerializer.Serialize(
        new { source = LocalJournalBridgeCommand.Source, command, correlationId, payload },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var response = await handler.HandleWebMessageAsync(request).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Bridge did not return a response.");
    return JsonDocument.Parse(response);
}

static async Task<int> CountReceiptCommandsAsync(string databasePath, Guid tenderId)
{
    await using var dbContext = new CashJournalService(Options(databasePath)).CreateDbContext();
    return await dbContext.TerminalCashReceiptRetrievalCommands.CountAsync(command => command.TerminalCashTenderId == tenderId).ConfigureAwait(false);
}

static async Task<string> ReadAuthoritativePayloadAsync(string databasePath, Guid tenderId)
{
    await using var dbContext = new CashJournalService(Options(databasePath)).CreateDbContext();
    var command = await dbContext.TerminalCashReceiptRetrievalCommands
        .SingleAsync(command => command.TerminalCashTenderId == tenderId).ConfigureAwait(false);
    return command.AuthoritativePresentationJson ?? "";
}

static LocalOperationsDatabaseOptions Options(string databasePath) =>
    new(
        databasePath,
        CentralPmsBaseUrl: "http://127.0.0.1:9",
        EnableCentralPmsCashSubmission: true,
        EnableCentralPmsFiscalIssuance: true,
        EnableCentralPmsReceiptRetrieval: true);

static void DeleteIfExists(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public enum ReceiptPreviewProofScenario
{
    Available,
    Complete,
    Voided,
    UnsupportedVersion,
    PayloadHashMismatch,
    MalformedPayload
}

public sealed record PreviewProofArguments(
    bool Interactive,
    ReceiptPreviewProofScenario Scenario,
    string? PaperWidthMm,
    string? DatabasePath)
{
    public static PreviewProofArguments Parse(string[] args)
    {
        var interactive = false;
        var scenario = ReceiptPreviewProofScenario.Available;
        string? paperWidthMm = null;
        string? databasePath = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--interactive", StringComparison.OrdinalIgnoreCase))
            {
                interactive = true;
                continue;
            }

            if (string.Equals(args[index], "--scenario", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!Enum.TryParse<ReceiptPreviewProofScenario>(args[++index], ignoreCase: true, out scenario))
                {
                    throw new ArgumentException($"Unsupported scenario '{args[index]}'. Supported scenarios: {string.Join(", ", Enum.GetNames<ReceiptPreviewProofScenario>())}.");
                }

                continue;
            }

            if (string.Equals(args[index], "--paper-width-mm", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                paperWidthMm = args[++index];
                continue;
            }

            if (string.Equals(args[index], "--database-path", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                databasePath = Path.GetFullPath(args[++index]);
            }
        }

        return new PreviewProofArguments(interactive, scenario, paperWidthMm, databasePath);
    }
}

internal sealed class PreviewCashClient : ICentralPmsTerminalCashPaymentClient
{
    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(
        Uri baseUri,
        TerminalCashPaymentRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>.Confirmed(
            new TerminalCashPaymentResponse(
                payload.TerminalCashTenderId,
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                "CONFIRMED",
                "CREATED",
                "scope",
                "terminal-cash-payment:sha256:v1",
                DateTimeOffset.Parse("2026-07-15T00:03:00Z"),
                DateTimeOffset.Parse("2026-07-15T00:03:00Z"),
                DateTimeOffset.Parse("2026-07-15T00:03:00Z"),
                Guid.Parse(correlationId),
                "NOT_STARTED_IN_THIS_SLICE"),
            201));

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview proof must not require cash-payment readback.");
}

internal sealed class PreviewFiscalClient : ICentralPmsTerminalCashFiscalClient
{
    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> SubmitAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        TerminalCashFiscalIssuanceRequest payload,
        string idempotencyKey,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>.Recorded(
            new TerminalCashFiscalIssuanceResponse(
                terminalCashTenderId,
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                Guid.Parse("55555555-5555-4555-8555-555555555555"),
                "FISCAL_ISSUANCE_RECORDED",
                "NEWLY_CREATED",
                Guid.Parse("66666666-6666-4666-8666-666666666666"),
                "SI-000001",
                DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
                "terminal-cash-fiscal-issuance:sha256:v1",
                DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
                DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
                Guid.Parse(correlationId),
                null,
                null,
                PosServerCallAttempted: true,
                ExitAuthorizationIssued: false,
                GateBehaviorTriggered: false),
            200));

    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview proof must not require fiscal readback.");
}

internal sealed class PreviewReceiptClient(ReceiptPreviewProofScenario scenario) : ICentralPmsTerminalCashReceiptClient
{
    public Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(
        Uri baseUri,
        Guid terminalCashTenderId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var voided = scenario == ReceiptPreviewProofScenario.Voided;
        var payload = scenario is ReceiptPreviewProofScenario.Complete or ReceiptPreviewProofScenario.Voided
            ? """
              {
                "presentation": {
                  "registeredBusinessName": "GOVERNED REGISTERED BUSINESS NAME",
                  "registeredBusinessAddress": "GOVERNED REGISTERED BUSINESS ADDRESS",
                  "tin": "GOVERNED TIN",
                  "posSerialNumber": "GOVERNED POS SERIAL NUMBER",
                  "machineIdentificationNumber": "GOVERNED MACHINE IDENTIFICATION NUMBER",
                  "parkingLocation": "GOVERNED PARKING LOCATION",
                  "terminalId": "GOVERNED TERMINAL ID",
                  "fiscalDocumentNumber": "SI-000001",
                  "issuedAt": "GOVERNED ISSUED DATE",
                  "plateNumber": "GOVERNED PLATE NUMBER",
                  "entryTime": "GOVERNED ENTRY TIME",
                  "exitTime": "GOVERNED EXIT TIME",
                  "durationDisplay": "GOVERNED DURATION",
                  "lines": [
                    { "description": "Parking fee - cash", "quantity": "1", "unitPriceDisplay": "PHP 125.00", "displayAmount": "PHP 125.00" }
                  ],
                  "subtotalDisplay": "PHP 125.00",
                  "discounts": [
                    { "description": "None", "displayAmount": "PHP 0.00" }
                  ],
                  "vatableSalesDisplay": "PHP 125.00",
                  "outputVatDisplay": "PHP 0.00",
                  "vatExemptSalesDisplay": "PHP 0.00",
                  "zeroRatedSalesDisplay": "PHP 0.00",
                  "tenders": [
                    { "tenderType": "CASH", "provider": "not_applicable", "displayAmount": "PHP 150.00", "changeDisplay": "PHP 25.00" }
                  ],
                  "salesInvoiceStatement": "THIS SERVES AS YOUR SALES INVOICE",
                  "footer": { "message": "THANK YOU FOR CHOOSING OUR SERVICE" },
                  "birAccreditationNumber": "GOVERNED BIR ACCREDITATION NO.",
                  "birAccreditationIssuedDateDisplay": "GOVERNED BIR ACCREDITATION DATE ISSUED",
                  "birAccreditationValidUntilDisplay": "GOVERNED BIR ACCREDITATION VALID UNTIL",
                  "ptuNumber": "GOVERNED PTU NO.",
                  "ptuIssuedDateDisplay": "GOVERNED PTU DATE ISSUED"
                }
              }
              """
            : """
              {
                "presentation": {
                  "fiscalDocumentNumber": "SI-000001",
                  "lines": [
                    { "description": "Parking fee - cash", "quantity": "1", "displayAmount": "PHP 125.00" }
                  ],
                  "taxes": [
                    { "taxType": "VAT", "displayAmount": "PHP 0.00" }
                  ],
                  "totals": [
                    { "totalType": "grand_total", "displayAmount": "PHP 125.00" }
                  ],
                  "tenders": [
                    { "tenderType": "CASH", "displayAmount": "PHP 150.00", "changeDisplay": "PHP 25.00" }
                  ]
                }
              }
              """;

        using var document = JsonDocument.Parse(payload);

        return Task.FromResult(CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>.Available(
            new TerminalCashReceiptPresentationResponse(
                terminalCashTenderId,
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                "CONFIRMED",
                Guid.Parse("55555555-5555-4555-8555-555555555555"),
                "FISCAL_ISSUANCE_RECORDED",
                Guid.Parse("66666666-6666-4666-8666-666666666666"),
                "SI-000001",
                voided ? "voided" : "recorded",
                voided ? "VOIDED_PRESENTATION_AVAILABLE" : "AVAILABLE",
                ReceiptPreviewContract.PresentationVersion,
                ReceiptPreviewContract.TemplateVersion,
                "sha256:fiscal-semantic",
                "pos-server-semantic-hash:sha256:v1",
                "MATCHED",
                ReceiptPreviewContract.ContentType,
                document.RootElement.Clone(),
                voided ? "voided" : null,
                voided ? "operator_void" : null,
                voided ? DateTimeOffset.Parse("2026-07-15T00:06:00Z") : null,
                DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
                DateTimeOffset.Parse("2026-07-15T00:05:00Z"),
                Guid.Parse(correlationId)),
            200,
            Guid.Parse(correlationId)));
    }
}

internal sealed class NoNetworkCashClient : ICentralPmsTerminalCashPaymentClient
{
    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentResponse>> SubmitAsync(Uri baseUri, TerminalCashPaymentRequest payload, string idempotencyKey, string correlationId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview must not submit cash payment.");

    public Task<CentralPmsTerminalCashPaymentResult<TerminalCashPaymentReadbackResponse>> ReadbackAsync(Uri baseUri, Guid terminalCashTenderId, string correlationId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview must not read cash payment.");
}

internal sealed class NoNetworkFiscalClient : ICentralPmsTerminalCashFiscalClient
{
    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> SubmitAsync(Uri baseUri, Guid terminalCashTenderId, TerminalCashFiscalIssuanceRequest payload, string idempotencyKey, string correlationId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview must not submit fiscal issuance.");

    public Task<CentralPmsTerminalCashFiscalResult<TerminalCashFiscalIssuanceResponse>> ReadbackAsync(Uri baseUri, Guid terminalCashTenderId, string correlationId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview must not read fiscal issuance.");
}

internal sealed class NoNetworkReceiptClient : ICentralPmsTerminalCashReceiptClient
{
    public Task<CentralPmsTerminalCashReceiptResult<TerminalCashReceiptPresentationResponse>> RetrieveAsync(Uri baseUri, Guid terminalCashTenderId, string correlationId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Receipt preview must not retrieve receipt presentation over the network.");
}
