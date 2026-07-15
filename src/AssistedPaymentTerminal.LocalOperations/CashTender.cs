namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashTender
{
    public Guid Id { get; set; }

    public Guid CashCustodySessionId { get; set; }

    public CashCustodySession? CashCustodySession { get; set; }

    public required string ParkingSessionId { get; set; }

    public required string TariffSnapshotId { get; set; }

    public required string Currency { get; set; }

    public decimal AmountDue { get; set; }

    public decimal AmountTendered { get; set; }

    public decimal ChangeDue { get; set; }

    public required string CorrelationId { get; set; }

    public required string LocalIdempotencyIdentity { get; set; }

    public CashTenderState CurrentLocalState { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<CashTenderEvent> Events { get; } = new List<CashTenderEvent>();
}
