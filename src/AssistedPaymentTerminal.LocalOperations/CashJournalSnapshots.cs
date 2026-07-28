namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CashCustodySessionSnapshot(
    Guid Id,
    string CashierId,
    string AuthenticatedCashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    string SiteId,
    string SiteGroupId,
    string PosServerId,
    decimal OpeningCashAmount,
    DateTimeOffset OpenedAt,
    CashCustodySessionStatus Status)
{
    public static CashCustodySessionSnapshot FromEntity(CashCustodySession session) =>
        new(
            session.Id,
            session.CashierId,
            session.AuthenticatedCashierSessionReference,
            session.CashierShiftId,
            session.TerminalId,
            session.SiteId,
            session.SiteGroupId,
            session.PosServerId,
            session.OpeningCashAmount,
            session.OpenedAt,
            session.Status);
}

public sealed record CashTenderSnapshot(
    Guid Id,
    Guid CashCustodySessionId,
    string ParkingSessionId,
    string TariffSnapshotId,
    string Currency,
    decimal AmountDue,
    decimal AmountTendered,
    decimal ChangeDue,
    string CorrelationId,
    string LocalIdempotencyIdentity,
    CashTenderState CurrentLocalState,
    string? StatutoryDiscountDecisionCommandId,
    string? StatutoryDiscountPayableBasisApplicationCommandId,
    string? StatutoryDiscountValidationId,
    string? StatutoryOriginalTariffSnapshotId,
    string? StatutoryAppliedTariffSnapshotId,
    long? StatutoryOriginalAmountMinorUnits,
    long? StatutoryFinalAmountMinorUnits,
    string? StatutoryCurrency,
    bool? StatutoryAmountAcknowledged,
    DateTimeOffset? StatutoryAmountAcknowledgedAt,
    string? StatutoryImmediateRevalidationOutcome,
    DateTimeOffset? StatutoryImmediateRevalidatedAt,
    string? StatutoryCorrelationId,
    string? StatutoryReadinessStatus,
    string? StatutoryReadinessAction,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CashTenderSnapshot FromEntity(CashTender tender) =>
        new(
            tender.Id,
            tender.CashCustodySessionId,
            tender.ParkingSessionId,
            tender.TariffSnapshotId,
            tender.Currency,
            tender.AmountDue,
            tender.AmountTendered,
            tender.ChangeDue,
            tender.CorrelationId,
            tender.LocalIdempotencyIdentity,
            tender.CurrentLocalState,
            tender.StatutoryDiscountDecisionCommandId,
            tender.StatutoryDiscountPayableBasisApplicationCommandId,
            tender.StatutoryDiscountValidationId,
            tender.StatutoryOriginalTariffSnapshotId,
            tender.StatutoryAppliedTariffSnapshotId,
            tender.StatutoryOriginalAmountMinorUnits,
            tender.StatutoryFinalAmountMinorUnits,
            tender.StatutoryCurrency,
            tender.StatutoryAmountAcknowledged,
            tender.StatutoryAmountAcknowledgedAt,
            tender.StatutoryImmediateRevalidationOutcome,
            tender.StatutoryImmediateRevalidatedAt,
            tender.StatutoryCorrelationId,
            tender.StatutoryReadinessStatus,
            tender.StatutoryReadinessAction,
            tender.CreatedAt,
            tender.UpdatedAt);
}

public sealed record CashTenderEventSnapshot(
    Guid Id,
    Guid CashTenderId,
    CashTenderEventType EventType,
    DateTimeOffset OccurredAt,
    decimal AmountTendered,
    decimal ChangeDue,
    bool CashierAttested,
    string ActorCashierId,
    string CorrelationId,
    IReadOnlyList<CashDenominationEntrySnapshot> DenominationEntries)
{
    public static CashTenderEventSnapshot FromEntity(CashTenderEvent cashEvent) =>
        new(
            cashEvent.Id,
            cashEvent.CashTenderId,
            cashEvent.EventType,
            cashEvent.OccurredAt,
            cashEvent.AmountTendered,
            cashEvent.ChangeDue,
            cashEvent.CashierAttested,
            cashEvent.ActorCashierId,
            cashEvent.CorrelationId,
            cashEvent.DenominationEntries
                .OrderBy(entry => entry.DenominationValue)
                .Select(CashDenominationEntrySnapshot.FromEntity)
                .ToArray());
}

public sealed record CashDenominationEntrySnapshot(
    Guid Id,
    string DenominationCode,
    decimal DenominationValue,
    int Quantity)
{
    public static CashDenominationEntrySnapshot FromEntity(CashDenominationEntry entry) =>
        new(entry.Id, entry.DenominationCode, entry.DenominationValue, entry.Quantity);
}

public sealed record PayableBasisStateSnapshot(
    Guid Id,
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
    string? StatutoryDiscountStateJson,
    DateTimeOffset ResolvedAt,
    DateTimeOffset? LastRevalidatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PayableBasisStateSnapshot FromEntity(TerminalCashPayableBasisState state) =>
        new(
            state.Id,
            state.LocalWorkflowId,
            state.LookupReferenceType,
            state.LookupReferenceValue,
            state.ParkingSessionId,
            state.TariffSnapshotId,
            state.SiteId,
            state.SiteGroupId,
            state.SitePosServerId,
            state.TerminalId,
            state.AuthoritativeAmountMinorUnits,
            state.Currency,
            state.TariffCalculatedAt,
            state.TariffValidUntil,
            state.FeeValidUntil,
            state.ParkingStatus,
            state.PaymentStatus,
            state.SessionReadiness,
            state.TariffReadiness,
            state.PaymentEligibility,
            state.TerminalCashAvailability,
            state.FiscalReadiness,
            state.SalesInvoiceConfigurationReadiness,
            state.CashAcceptanceReadiness,
            state.ReadyForCashAcceptance,
            System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<string>>(state.BlockingReasonCodesJson) ?? Array.Empty<string>(),
            state.Retryable,
            state.SafeUserFacingClassification,
            state.CentralPmsCorrelationId,
            state.RevalidationOutcome,
            state.CashierAcknowledgementRequired,
            state.AmountChanged,
            state.PriorDisplayedAmountMinorUnits,
            state.StatutoryDiscountStateJson,
            state.ResolvedAt,
            state.LastRevalidatedAt,
            state.UpdatedAt);
}
