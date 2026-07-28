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

    public string? StatutoryDiscountDecisionCommandId { get; set; }

    public string? StatutoryDiscountPayableBasisApplicationCommandId { get; set; }

    public string? StatutoryDiscountValidationId { get; set; }

    public string? StatutoryOriginalTariffSnapshotId { get; set; }

    public string? StatutoryAppliedTariffSnapshotId { get; set; }

    public long? StatutoryOriginalAmountMinorUnits { get; set; }

    public long? StatutoryFinalAmountMinorUnits { get; set; }

    public string? StatutoryCurrency { get; set; }

    public bool? StatutoryAmountAcknowledged { get; set; }

    public DateTimeOffset? StatutoryAmountAcknowledgedAt { get; set; }

    public string? StatutoryImmediateRevalidationOutcome { get; set; }

    public DateTimeOffset? StatutoryImmediateRevalidatedAt { get; set; }

    public string? StatutoryCorrelationId { get; set; }

    public string? StatutoryReadinessStatus { get; set; }

    public string? StatutoryReadinessAction { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<CashTenderEvent> Events { get; } = new List<CashTenderEvent>();
}
