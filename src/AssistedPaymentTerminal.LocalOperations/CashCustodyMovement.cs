namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashCustodyMovement
{
    public Guid Id { get; set; }
    public Guid CashCustodySessionId { get; set; }
    public CashCustodySession? CashCustodySession { get; set; }
    public CashCustodyMovementType MovementType { get; set; }
    public long AmountMinorUnits { get; set; }
    public Guid? OriginalMovementId { get; set; }
    public CashCustodyMovement? OriginalMovement { get; set; }
    public required string InitiatingCashierId { get; set; }
    public required string InitiatingCashierSessionReference { get; set; }
    public required string IntendedCounterpartyReference { get; set; }
    public required string ExternalHandoverReference { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string CanonicalRequestPayloadHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<CashCustodyMovementDenomination> Denominations { get; } = new List<CashCustodyMovementDenomination>();
    public ICollection<CashCustodyMovementEvent> Events { get; } = new List<CashCustodyMovementEvent>();
    public ICollection<CashCustodyMovement> Returns { get; } = new List<CashCustodyMovement>();
}
