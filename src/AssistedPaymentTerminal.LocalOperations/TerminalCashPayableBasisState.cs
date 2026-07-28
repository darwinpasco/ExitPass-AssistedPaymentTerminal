namespace AssistedPaymentTerminal.LocalOperations;

public sealed class TerminalCashPayableBasisState
{
    public Guid Id { get; set; }

    public string LocalWorkflowId { get; set; } = string.Empty;

    public string LookupReferenceType { get; set; } = string.Empty;

    public string LookupReferenceValue { get; set; } = string.Empty;

    public string ParkingSessionId { get; set; } = string.Empty;

    public string TariffSnapshotId { get; set; } = string.Empty;

    public string SiteId { get; set; } = string.Empty;

    public string SiteGroupId { get; set; } = string.Empty;

    public string? SitePosServerId { get; set; }

    public string TerminalId { get; set; } = string.Empty;

    public long AuthoritativeAmountMinorUnits { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset? TariffCalculatedAt { get; set; }

    public DateTimeOffset TariffValidUntil { get; set; }

    public DateTimeOffset? FeeValidUntil { get; set; }

    public string ParkingStatus { get; set; } = string.Empty;

    public string PaymentStatus { get; set; } = string.Empty;

    public string? SessionReadiness { get; set; }

    public string? TariffReadiness { get; set; }

    public string? PaymentEligibility { get; set; }

    public string? TerminalCashAvailability { get; set; }

    public string? FiscalReadiness { get; set; }

    public string? SalesInvoiceConfigurationReadiness { get; set; }

    public string? CashAcceptanceReadiness { get; set; }

    public bool ReadyForCashAcceptance { get; set; }

    public string BlockingReasonCodesJson { get; set; } = string.Empty;

    public bool Retryable { get; set; }

    public string SafeUserFacingClassification { get; set; } = string.Empty;

    public string CentralPmsCorrelationId { get; set; } = string.Empty;

    public string? RevalidationOutcome { get; set; }

    public bool CashierAcknowledgementRequired { get; set; }

    public bool AmountChanged { get; set; }

    public long? PriorDisplayedAmountMinorUnits { get; set; }

    public string? StatutoryDiscountStateJson { get; set; }

    public DateTimeOffset ResolvedAt { get; set; }

    public DateTimeOffset? LastRevalidatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
