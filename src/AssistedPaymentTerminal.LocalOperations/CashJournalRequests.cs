namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CreateCashCustodySessionRequest(
    string CashierId,
    string AuthenticatedCashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    string SiteId,
    string SiteGroupId,
    string PosServerId,
    decimal OpeningCashAmount,
    Guid? CashCustodySessionId = null,
    DateTimeOffset? OpenedAt = null);

public sealed record StartCashTenderRequest(
    Guid CashCustodySessionId,
    string ParkingSessionId,
    string TariffSnapshotId,
    string Currency,
    decimal AmountDue,
    decimal AmountTendered,
    string CorrelationId,
    string LocalIdempotencyIdentity,
    Guid? LocalCashTenderId = null,
    DateTimeOffset? StartedAt = null);

public sealed record CommitCashReceivedRequest(
    Guid LocalCashTenderId,
    bool CashierAttested,
    IReadOnlyCollection<CashDenominationLine> Denominations,
    DateTimeOffset? ReceivedAt = null,
    string CentralPmsTarget = "UNCONFIGURED_CENTRAL_PMS",
    bool SimulateOutboxCreationFailure = false);

public sealed record CashDenominationLine(
    string DenominationCode,
    decimal DenominationValue,
    int Quantity);

public sealed record SavePayableBasisStateRequest(
    string LocalWorkflowId,
    string LookupReferenceType,
    string LookupReferenceValue,
    string ParkingSessionId,
    string TariffSnapshotId,
    string SiteId,
    string SiteGroupId,
    string? SitePosServerId,
    string TerminalId,
    long AuthoritativeAmountMinorUnits,
    string Currency,
    DateTimeOffset? TariffCalculatedAt,
    DateTimeOffset TariffValidUntil,
    DateTimeOffset? FeeValidUntil,
    string ParkingStatus,
    string PaymentStatus,
    string? SessionReadiness,
    string? TariffReadiness,
    string? PaymentEligibility,
    string? TerminalCashAvailability,
    string? FiscalReadiness,
    string? SalesInvoiceConfigurationReadiness,
    string? CashAcceptanceReadiness,
    bool ReadyForCashAcceptance,
    IReadOnlyList<string> BlockingReasonCodes,
    bool Retryable,
    string SafeUserFacingClassification,
    string CentralPmsCorrelationId,
    string? RevalidationOutcome,
    bool CashierAcknowledgementRequired,
    bool AmountChanged,
    long? PriorDisplayedAmountMinorUnits,
    string? StatutoryDiscountStateJson = null,
    DateTimeOffset? RecordedAt = null);
