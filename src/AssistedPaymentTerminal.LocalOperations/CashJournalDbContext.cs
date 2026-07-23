using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashJournalDbContext(DbContextOptions<CashJournalDbContext> options) : DbContext(options)
{
    public DbSet<CashCustodySession> CashCustodySessions => Set<CashCustodySession>();

    public DbSet<CashTender> CashTenders => Set<CashTender>();

    public DbSet<CashTenderEvent> CashTenderEvents => Set<CashTenderEvent>();

    public DbSet<CashDenominationEntry> CashDenominationEntries => Set<CashDenominationEntry>();

    public DbSet<TerminalCashPaymentOutboxCommand> TerminalCashPaymentOutboxCommands => Set<TerminalCashPaymentOutboxCommand>();

    public DbSet<TerminalCashPaymentSubmissionAttempt> TerminalCashPaymentSubmissionAttempts => Set<TerminalCashPaymentSubmissionAttempt>();

    public DbSet<TerminalCashFiscalOutboxCommand> TerminalCashFiscalOutboxCommands => Set<TerminalCashFiscalOutboxCommand>();

    public DbSet<TerminalCashFiscalAttempt> TerminalCashFiscalAttempts => Set<TerminalCashFiscalAttempt>();

    public DbSet<TerminalCashReceiptRetrievalCommand> TerminalCashReceiptRetrievalCommands => Set<TerminalCashReceiptRetrievalCommand>();

    public DbSet<TerminalCashReceiptRetrievalAttempt> TerminalCashReceiptRetrievalAttempts => Set<TerminalCashReceiptRetrievalAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcDateTime.Ticks,
            value => new DateTimeOffset(new DateTime(value, DateTimeKind.Utc)));

        modelBuilder.Entity<CashCustodySession>(entity =>
        {
            entity.ToTable("cash_custody_sessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.CashierId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.AuthenticatedCashierSessionReference).HasMaxLength(256).IsRequired();
            entity.Property(session => session.CashierShiftId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.TerminalId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.SiteId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.SiteGroupId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.PosServerId).HasMaxLength(128).IsRequired();
            entity.Property(session => session.OpeningCashAmount).HasPrecision(18, 2);
            entity.Property(session => session.OpenedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasIndex(session => new { session.TerminalId, session.CashierId, session.Status });
        });

        modelBuilder.Entity<CashTender>(entity =>
        {
            entity.ToTable("cash_tenders");
            entity.HasKey(tender => tender.Id);
            entity.Property(tender => tender.ParkingSessionId).HasMaxLength(128).IsRequired();
            entity.Property(tender => tender.TariffSnapshotId).HasMaxLength(128).IsRequired();
            entity.Property(tender => tender.Currency).HasMaxLength(3).IsRequired();
            entity.Property(tender => tender.AmountDue).HasPrecision(18, 2);
            entity.Property(tender => tender.AmountTendered).HasPrecision(18, 2);
            entity.Property(tender => tender.ChangeDue).HasPrecision(18, 2);
            entity.Property(tender => tender.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(tender => tender.LocalIdempotencyIdentity).HasMaxLength(128).IsRequired();
            entity.Property(tender => tender.CurrentLocalState).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(tender => tender.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(tender => tender.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(tender => tender.CashCustodySession)
                .WithMany(session => session.CashTenders)
                .HasForeignKey(tender => tender.CashCustodySessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(tender => tender.ParkingSessionId).IsUnique();
            entity.HasIndex(tender => tender.LocalIdempotencyIdentity).IsUnique();
        });

        modelBuilder.Entity<CashTenderEvent>(entity =>
        {
            entity.ToTable("cash_tender_events");
            entity.HasKey(cashEvent => cashEvent.Id);
            entity.Property(cashEvent => cashEvent.EventType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(cashEvent => cashEvent.OccurredAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(cashEvent => cashEvent.AmountTendered).HasPrecision(18, 2);
            entity.Property(cashEvent => cashEvent.ChangeDue).HasPrecision(18, 2);
            entity.Property(cashEvent => cashEvent.ActorCashierId).HasMaxLength(128).IsRequired();
            entity.Property(cashEvent => cashEvent.CorrelationId).HasMaxLength(128).IsRequired();
            entity.HasOne(cashEvent => cashEvent.CashTender)
                .WithMany(tender => tender.Events)
                .HasForeignKey(cashEvent => cashEvent.CashTenderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(cashEvent => new { cashEvent.CashTenderId, cashEvent.OccurredAt });
        });

        modelBuilder.Entity<CashDenominationEntry>(entity =>
        {
            entity.ToTable("cash_denomination_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.DenominationCode).HasMaxLength(64).IsRequired();
            entity.Property(entry => entry.DenominationValue).HasPrecision(18, 2);
            entity.Property(entry => entry.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(entry => entry.CashTenderEvent)
                .WithMany(cashEvent => cashEvent.DenominationEntries)
                .HasForeignKey(entry => entry.CashTenderEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TerminalCashPaymentOutboxCommand>(entity =>
        {
            entity.ToTable("terminal_cash_payment_outbox_commands");
            entity.HasKey(command => command.Id);
            entity.Property(command => command.RequestPayloadJson).IsRequired();
            entity.Property(command => command.RequestPayloadHash).HasMaxLength(128).IsRequired();
            entity.Property(command => command.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(command => command.OriginalCorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(command => command.CentralPmsTarget).HasMaxLength(512).IsRequired();
            entity.Property(command => command.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(command => command.LastSafeErrorCode).HasMaxLength(128);
            entity.Property(command => command.ResultClassification).HasMaxLength(128);
            entity.Property(command => command.FirstAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.LastAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.NextRetryAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.ConfirmedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasIndex(command => command.TerminalCashTenderId).IsUnique();
            entity.HasIndex(command => command.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<TerminalCashPaymentSubmissionAttempt>(entity =>
        {
            entity.ToTable("terminal_cash_payment_submission_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.OperationType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(attempt => attempt.OutcomeClassification).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(attempt => attempt.SafeErrorCode).HasMaxLength(128);
            entity.Property(attempt => attempt.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(attempt => attempt.StartedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(attempt => attempt.CompletedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(attempt => attempt.LocalCommand)
                .WithMany(command => command.Attempts)
                .HasForeignKey(attempt => attempt.LocalCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(attempt => new { attempt.LocalCommandId, attempt.AttemptSequence }).IsUnique();
        });

        modelBuilder.Entity<TerminalCashFiscalOutboxCommand>(entity =>
        {
            entity.ToTable("terminal_cash_fiscal_outbox_commands");
            entity.HasKey(command => command.Id);
            entity.Property(command => command.RequestRepresentationJson).IsRequired();
            entity.Property(command => command.RequestHash).HasMaxLength(128).IsRequired();
            entity.Property(command => command.FiscalIdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(command => command.FiscalCorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(command => command.CentralPmsTarget).HasMaxLength(512).IsRequired();
            entity.Property(command => command.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(command => command.LastSafeErrorCode).HasMaxLength(128);
            entity.Property(command => command.ResultClassification).HasMaxLength(128);
            entity.Property(command => command.FiscalIssuanceState).HasMaxLength(128);
            entity.Property(command => command.FiscalDocumentNumber).HasMaxLength(128);
            entity.Property(command => command.SemanticHashSourceVersion).HasMaxLength(128);
            entity.Property(command => command.FirstAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.LastAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.NextRetryAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.FiscalNumberAssignedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.RecordedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(command => command.RelatedCashPaymentOutboxCommand)
                .WithMany()
                .HasForeignKey(command => command.RelatedCashPaymentOutboxCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(command => command.TerminalCashTenderId).IsUnique();
            entity.HasIndex(command => command.RelatedCashPaymentOutboxCommandId).IsUnique();
            entity.HasIndex(command => command.FiscalIdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<TerminalCashFiscalAttempt>(entity =>
        {
            entity.ToTable("terminal_cash_fiscal_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.OperationType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(attempt => attempt.OutcomeClassification).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(attempt => attempt.SafeErrorCode).HasMaxLength(128);
            entity.Property(attempt => attempt.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(attempt => attempt.StartedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(attempt => attempt.CompletedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(attempt => attempt.LocalFiscalCommand)
                .WithMany(command => command.Attempts)
                .HasForeignKey(attempt => attempt.LocalFiscalCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(attempt => new { attempt.LocalFiscalCommandId, attempt.AttemptSequence }).IsUnique();
        });

        modelBuilder.Entity<TerminalCashReceiptRetrievalCommand>(entity =>
        {
            entity.ToTable("terminal_cash_receipt_retrieval_commands");
            entity.HasKey(command => command.Id);
            entity.Property(command => command.RetrievalCorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(command => command.CentralPmsTarget).HasMaxLength(512).IsRequired();
            entity.Property(command => command.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(command => command.CanonicalPaymentStatus).HasMaxLength(64);
            entity.Property(command => command.LastSafeErrorCode).HasMaxLength(128);
            entity.Property(command => command.LastCentralPmsCorrelationId).HasMaxLength(128);
            entity.Property(command => command.ResultClassification).HasMaxLength(128);
            entity.Property(command => command.ReceiptAvailabilityState).HasMaxLength(128);
            entity.Property(command => command.FiscalDocumentNumber).HasMaxLength(128);
            entity.Property(command => command.FiscalDocumentStatus).HasMaxLength(128);
            entity.Property(command => command.PresentationVersion).HasMaxLength(128);
            entity.Property(command => command.TemplateVersion).HasMaxLength(128);
            entity.Property(command => command.SemanticRequestHash).HasMaxLength(160);
            entity.Property(command => command.SemanticRequestHashVersion).HasMaxLength(128);
            entity.Property(command => command.SemanticRequestHashStatus).HasMaxLength(128);
            entity.Property(command => command.ContentType).HasMaxLength(128);
            entity.Property(command => command.AuthoritativePresentationJson);
            entity.Property(command => command.AuthoritativePayloadHash).HasMaxLength(128);
            entity.Property(command => command.VoidStatus).HasMaxLength(128);
            entity.Property(command => command.VoidReasonCode).HasMaxLength(128);
            entity.Property(command => command.FirstAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.LastAttemptedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.NextRetryAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.VoidedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.RetrievedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.LastUpdatedFromCentralPms).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(command => command.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(command => command.RelatedCashPaymentOutboxCommand)
                .WithMany()
                .HasForeignKey(command => command.RelatedCashPaymentOutboxCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(command => command.RelatedFiscalCommand)
                .WithMany()
                .HasForeignKey(command => command.RelatedFiscalCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(command => command.TerminalCashTenderId).IsUnique();
            entity.HasIndex(command => command.RelatedFiscalCommandId).IsUnique();
        });

        modelBuilder.Entity<TerminalCashReceiptRetrievalAttempt>(entity =>
        {
            entity.ToTable("terminal_cash_receipt_retrieval_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.OutcomeClassification).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(attempt => attempt.SafeErrorCode).HasMaxLength(128);
            entity.Property(attempt => attempt.CentralPmsCorrelationId).HasMaxLength(128);
            entity.Property(attempt => attempt.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(attempt => attempt.StartedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(attempt => attempt.CompletedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(attempt => attempt.LocalReceiptRetrieval)
                .WithMany(command => command.Attempts)
                .HasForeignKey(attempt => attempt.LocalReceiptRetrievalId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(attempt => new { attempt.LocalReceiptRetrievalId, attempt.AttemptSequence }).IsUnique();
        });
    }
}
