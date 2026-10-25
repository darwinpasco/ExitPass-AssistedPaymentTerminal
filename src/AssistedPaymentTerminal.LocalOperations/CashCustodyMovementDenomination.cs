namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashCustodyMovementDenomination
{
    public Guid Id { get; set; }
    public Guid MovementId { get; set; }
    public CashCustodyMovement? Movement { get; set; }
    public required string DenominationCode { get; set; }
    public int DenominationValueMinorUnits { get; set; }
    public int Quantity { get; set; }
    public long CalculatedAmountMinorUnits { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
