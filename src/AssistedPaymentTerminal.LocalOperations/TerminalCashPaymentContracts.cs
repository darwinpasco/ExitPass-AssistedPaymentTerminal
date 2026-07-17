using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed record TerminalCashPaymentRequest(
    Guid TerminalCashTenderId,
    Guid CashCustodySessionId,
    Guid ParkingSessionId,
    Guid TariffSnapshotId,
    string CashierId,
    string CashierSessionReference,
    string CashierShiftId,
    string TerminalId,
    Guid SiteId,
    Guid SiteGroupId,
    string PosServerId,
    string Currency,
    long AmountDueMinorUnits,
    long AmountTenderedMinorUnits,
    long ChangeDueMinorUnits,
    DateTimeOffset CashReceivedAt,
    IReadOnlyList<TerminalCashDenominationEntry> DenominationEntries,
    string LocalEventReference);

public sealed record TerminalCashDenominationEntry(
    string DenominationCode,
    long DenominationValueMinorUnits,
    int Quantity);

public sealed record TerminalCashPaymentResponse(
    Guid TerminalCashTenderId,
    Guid PaymentAttemptId,
    Guid PaymentConfirmationId,
    string CanonicalPaymentStatus,
    string ResultClassification,
    string IdempotencyScope,
    string SemanticHashSourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset LastUpdatedAt,
    Guid CorrelationId,
    string FiscalStatus);

public sealed record TerminalCashPaymentReadbackResponse(
    Guid TerminalCashTenderId,
    Guid PaymentAttemptId,
    Guid CashCustodySessionId,
    Guid ParkingSessionId,
    Guid TariffSnapshotId,
    string TerminalId,
    Guid SiteId,
    Guid SiteGroupId,
    string PosServerId,
    string CashierId,
    string CashierShiftId,
    string Currency,
    long AmountDueMinorUnits,
    long AmountTenderedMinorUnits,
    long ChangeDueMinorUnits,
    string CanonicalPaymentStatus,
    Guid PaymentConfirmationId,
    string ResultClassification,
    string IdempotencyScope,
    string SemanticHashSourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset LastUpdatedAt,
    Guid CorrelationId,
    string FiscalStatus);

public sealed record TerminalCashFiscalIssuanceRequest();

public sealed record TerminalCashFiscalIssuanceResponse(
    Guid TerminalCashTenderId,
    Guid PaymentAttemptId,
    Guid PaymentConfirmationId,
    Guid FiscalIssuanceReferenceId,
    string FiscalIssuanceState,
    string? ResultClassification,
    Guid? PosFiscalDocumentId,
    string? FiscalDocumentNumber,
    DateTimeOffset? FiscalNumberAssignedAt,
    string? SemanticHashSourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CorrelationId,
    string? SafeErrorCode,
    string? SafeErrorPosture,
    bool PosServerCallAttempted,
    bool ExitAuthorizationIssued,
    bool GateBehaviorTriggered);

public sealed record TerminalCashReceiptPresentationResponse(
    Guid TerminalCashTenderId,
    Guid PaymentAttemptId,
    Guid PaymentConfirmationId,
    Guid FiscalIssuanceReferenceId,
    string FiscalIssuanceState,
    Guid PosFiscalDocumentId,
    string? FiscalDocumentNumber,
    string? FiscalDocumentStatus,
    string ReceiptAvailabilityState,
    string? PresentationVersion,
    string? TemplateVersion,
    string? ContentType,
    JsonElement AuthoritativePresentation,
    string? VoidStatus,
    string? VoidReasonCode,
    DateTimeOffset? VoidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid CorrelationId);

public sealed record CentralPmsSafeError(
    string ErrorCode,
    string Message,
    Guid CorrelationId,
    bool Retryable);
