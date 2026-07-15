namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashTenderEvent
{
    public Guid Id { get; set; }

    public Guid CashTenderId { get; set; }

    public CashTender? CashTender { get; set; }

    public CashTenderEventType EventType { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public decimal AmountTendered { get; set; }

    public decimal ChangeDue { get; set; }

    public bool CashierAttested { get; set; }

    public required string ActorCashierId { get; set; }

    public required string CorrelationId { get; set; }

    public ICollection<CashDenominationEntry> DenominationEntries { get; } = new List<CashDenominationEntry>();
}
