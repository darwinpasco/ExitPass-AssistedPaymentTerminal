namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashCustodyDenominationEntry
{
    public Guid Id { get; set; }

    public Guid CashCustodySessionId { get; set; }

    public CashCustodySession? CashCustodySession { get; set; }

    public CashCustodyDenominationCaptureType CaptureType { get; set; }

    public required string DenominationCode { get; set; }

    public int DenominationValueMinorUnits { get; set; }

    public int Quantity { get; set; }

    public long CalculatedAmountMinorUnits { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
