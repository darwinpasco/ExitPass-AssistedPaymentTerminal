namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashDenominationEntry
{
    public Guid Id { get; set; }

    public Guid CashTenderEventId { get; set; }

    public CashTenderEvent? CashTenderEvent { get; set; }

    public required string DenominationCode { get; set; }

    public decimal DenominationValue { get; set; }

    public int Quantity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
