using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashJournalDbContext(DbContextOptions<CashJournalDbContext> options) : DbContext(options)
{
    public DbSet<CashCustodySession> CashCustodySessions => Set<CashCustodySession>();

    public DbSet<CashTender> CashTenders => Set<CashTender>();

    public DbSet<CashTenderEvent> CashTenderEvents => Set<CashTenderEvent>();

    public DbSet<CashDenominationEntry> CashDenominationEntries => Set<CashDenominationEntry>();

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
    }
}
