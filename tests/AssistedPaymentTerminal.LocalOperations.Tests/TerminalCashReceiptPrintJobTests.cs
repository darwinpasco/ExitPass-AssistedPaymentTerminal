using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class TerminalCashReceiptPrintJobTests
{
    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FirstAcceptedPrintIsOriginalAndLaterAcceptedPrintIsReprint()
    {
        using var database = TestDatabase.Create();
        var receipt = await SeedAvailableReceiptAsync(database);
        var service = new TerminalCashReceiptPrintJobService(database.Options);

        var original = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-original");
        original = await service.MarkSubmittedToSpoolerAsync(original.Id, "spooler-1");
        var reprint = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-reprint");

        Assert.Equal(TerminalCashReceiptPrintClassification.Original, original.Classification);
        Assert.Equal(TerminalCashReceiptPrintClassification.Reprint, reprint.Classification);
        Assert.Equal(2, reprint.CopySequence);
        Assert.Equal(receipt.PosFiscalDocumentId, reprint.PosFiscalDocumentId);
        Assert.Equal(receipt.AuthoritativePayloadHash, reprint.AuthoritativePayloadHash);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task FailedPreparationDoesNotConsumeOriginalPrint()
    {
        using var database = TestDatabase.Create();
        var receipt = await SeedAvailableReceiptAsync(database);
        var service = new TerminalCashReceiptPrintJobService(database.Options);

        var failed = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-failed");
        await service.MarkFailedAsync(failed.Id, TerminalCashReceiptPrintJobStatus.PreparationFailed, "PRINT_PREPARATION_FAILED", retryable: false);
        var next = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-next");

        Assert.Equal(TerminalCashReceiptPrintClassification.Original, next.Classification);
        Assert.Equal(2, next.CopySequence);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task InterruptedSubmissionBecomesUnknownAfterRestartWithoutResubmission()
    {
        using var database = TestDatabase.Create();
        var receipt = await SeedAvailableReceiptAsync(database);
        var service = new TerminalCashReceiptPrintJobService(database.Options);
        var job = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-unknown");
        await service.MarkPreparingAsync(job.Id);

        await new TerminalCashReceiptPrintJobService(database.Options).MarkInterruptedSubmissionsUnknownAsync();
        var jobs = await service.GetJobsForTenderAsync(receipt.TerminalCashTenderId);

        var stored = Assert.Single(jobs);
        Assert.Equal(TerminalCashReceiptPrintJobStatus.UnknownAfterRestart, stored.Status);
        Assert.False(stored.Retryable);
        Assert.Equal("SPOOLER_OUTCOME_UNKNOWN_AFTER_RESTART", stored.FailureClassification);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ReprintAcceptedTimestampsAreDurableAndDistinctAcrossRestart()
    {
        using var database = TestDatabase.Create();
        var receipt = await SeedAvailableReceiptAsync(database);
        var service = new TerminalCashReceiptPrintJobService(database.Options);
        var originalAcceptedAt = DateTimeOffset.Parse("2026-07-24T07:30:00Z");
        var firstReprintAcceptedAt = DateTimeOffset.Parse("2026-07-24T07:42:00Z");
        var secondReprintAcceptedAt = DateTimeOffset.Parse("2026-07-24T08:05:00Z");

        var original = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-original");
        await service.MarkSubmittedToSpoolerAsync(original.Id, "spooler-1", originalAcceptedAt);

        var firstReprint = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-reprint-1");
        firstReprint = await service.MarkSubmittedToSpoolerAsync(firstReprint.Id, "spooler-2", firstReprintAcceptedAt);

        var afterRestart = new TerminalCashReceiptPrintJobService(database.Options);
        var restored = await afterRestart.GetJobsForTenderAsync(receipt.TerminalCashTenderId);
        var restoredFirstReprint = restored.Single(job => job.Id == firstReprint.Id);

        Assert.Equal(TerminalCashReceiptPrintClassification.Reprint, restoredFirstReprint.Classification);
        Assert.Equal(firstReprintAcceptedAt, restoredFirstReprint.SubmittedToSpoolerAt);
        Assert.Equal(receipt.PosFiscalDocumentId, restoredFirstReprint.PosFiscalDocumentId);
        Assert.Equal(receipt.FiscalDocumentNumber, restoredFirstReprint.FiscalDocumentNumber);
        Assert.Equal(receipt.AuthoritativePayloadHash, restoredFirstReprint.AuthoritativePayloadHash);
        Assert.Equal(receipt.SemanticRequestHash, restoredFirstReprint.SemanticRequestHash);

        var secondReprint = await afterRestart.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-reprint-2");
        secondReprint = await afterRestart.MarkSubmittedToSpoolerAsync(secondReprint.Id, "spooler-3", secondReprintAcceptedAt);

        Assert.Equal(TerminalCashReceiptPrintClassification.Reprint, secondReprint.Classification);
        Assert.Equal(3, secondReprint.CopySequence);
        Assert.Equal(secondReprintAcceptedAt, secondReprint.SubmittedToSpoolerAt);
        Assert.NotEqual(firstReprint.SubmittedToSpoolerAt, secondReprint.SubmittedToSpoolerAt);
    }

    [Fact]
    [Trait("Category", "LocalOperations")]
    public async Task ReadOnlyHistoryQueriesAreBoundedAndDeterministicallyOrdered()
    {
        using var database = TestDatabase.Create();
        var receipt = await SeedAvailableReceiptAsync(database);
        var service = new TerminalCashReceiptPrintJobService(database.Options);

        var original = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 57, "receipt-paper-57", "corr-original");
        original = await service.MarkSubmittedToSpoolerAsync(original.Id, "spooler-1", DateTimeOffset.Parse("2026-07-24T07:30:00Z"));
        var reprint = await service.RequestPrintJobAsync(receipt, "APT Controlled Printer", 80, "receipt-paper-80", "corr-reprint");
        reprint = await service.MarkSubmittedToSpoolerAsync(reprint.Id, "spooler-2", DateTimeOffset.Parse("2026-07-24T07:42:00Z"));

        var byFiscalDocument = await service.GetJobsForFiscalDocumentAsync(receipt.PosFiscalDocumentId);
        var detail = await service.GetJobAsync(reprint.Id);
        var recent = await service.GetRecentJobsAsync(maxResults: 1);
        var byTender = await service.GetJobsForTenderAsync(receipt.TerminalCashTenderId);

        Assert.Collection(
            byFiscalDocument,
            job => Assert.Equal(1, job.CopySequence),
            job => Assert.Equal(2, job.CopySequence));
        Assert.Equal(reprint.Id, detail?.Id);
        var recentJob = Assert.Single(recent);
        Assert.Equal(reprint.Id, recentJob.Id);
        Assert.Collection(
            byTender,
            job => Assert.Equal(original.Id, job.Id),
            job => Assert.Equal(reprint.Id, job.Id));
    }

    private static async Task<TerminalCashReceiptRetrievalCommand> SeedAvailableReceiptAsync(TestDatabase database)
    {
        await using var dbContext = database.CreateService().CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();
        var paymentId = Guid.NewGuid();
        var tenderId = Guid.NewGuid();
        var paymentAttemptId = Guid.NewGuid();
        var paymentConfirmationId = Guid.NewGuid();
        var fiscalId = Guid.NewGuid();
        var fiscalIssuanceReferenceId = Guid.NewGuid();
        var posFiscalDocumentId = Guid.NewGuid();
        dbContext.TerminalCashPaymentOutboxCommands.Add(new TerminalCashPaymentOutboxCommand
        {
            Id = paymentId,
            TerminalCashTenderId = tenderId,
            CashCustodySessionId = Guid.NewGuid(),
            RequestPayloadJson = "{}",
            RequestPayloadHash = "sha256:payment",
            IdempotencyKey = $"idem-{Guid.NewGuid():N}",
            OriginalCorrelationId = Guid.NewGuid().ToString("D"),
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashPaymentCommandStatus.Confirmed,
            CanonicalPaymentAttemptId = paymentAttemptId,
            CanonicalPaymentConfirmationId = paymentConfirmationId,
            ConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        dbContext.TerminalCashFiscalOutboxCommands.Add(new TerminalCashFiscalOutboxCommand
        {
            Id = fiscalId,
            TerminalCashTenderId = tenderId,
            RelatedCashPaymentOutboxCommandId = paymentId,
            CanonicalPaymentAttemptId = paymentAttemptId,
            CanonicalPaymentConfirmationId = paymentConfirmationId,
            RequestRepresentationJson = "{}",
            RequestHash = "sha256:fiscal",
            FiscalIdempotencyKey = $"fiscal-{Guid.NewGuid():N}",
            FiscalCorrelationId = Guid.NewGuid().ToString("D"),
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashFiscalCommandStatus.Recorded,
            FiscalIssuanceReferenceId = fiscalIssuanceReferenceId,
            FiscalIssuanceState = "FISCAL_ISSUANCE_RECORDED",
            PosFiscalDocumentId = posFiscalDocumentId,
            FiscalDocumentNumber = "SI-000001",
            FiscalNumberAssignedAt = DateTimeOffset.UtcNow,
            RecordedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var receipt = new TerminalCashReceiptRetrievalCommand
        {
            Id = Guid.NewGuid(),
            TerminalCashTenderId = tenderId,
            RelatedCashPaymentOutboxCommandId = paymentId,
            RelatedFiscalCommandId = fiscalId,
            CanonicalPaymentAttemptId = paymentAttemptId,
            CanonicalPaymentConfirmationId = paymentConfirmationId,
            CanonicalPaymentStatus = "CONFIRMED",
            FiscalIssuanceReferenceId = fiscalIssuanceReferenceId,
            PosFiscalDocumentId = posFiscalDocumentId,
            RetrievalCorrelationId = Guid.NewGuid().ToString("D"),
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashReceiptRetrievalStatus.Available,
            ResultClassification = "AVAILABLE",
            ReceiptAvailabilityState = "AVAILABLE",
            FiscalDocumentNumber = "SI-000001",
            FiscalDocumentStatus = "RECORDED",
            PresentationVersion = "digital-sales-invoice-presentation-json-v1",
            TemplateVersion = "digital-sales-invoice-json-v1",
            SemanticRequestHash = "sha256:fiscal-semantic",
            SemanticRequestHashVersion = "pos-server-semantic-hash:sha256:v1",
            SemanticRequestHashStatus = "MATCHED",
            ContentType = "application/json",
            AuthoritativePresentationJson = "{\"presentation\":{\"fiscalDocumentNumber\":\"SI-000001\"}}",
            AuthoritativePayloadHash = "sha256:receipt-payload",
            RetrievedAt = DateTimeOffset.UtcNow,
            LastUpdatedFromCentralPms = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.TerminalCashReceiptRetrievalCommands.Add(receipt);
        await dbContext.SaveChangesAsync();
        return receipt;
    }
}
