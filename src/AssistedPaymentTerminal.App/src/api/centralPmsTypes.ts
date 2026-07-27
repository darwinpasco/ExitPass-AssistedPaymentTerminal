export type PayableBasisReferenceType = "ticket" | "plate";

export type ResolvePayableBasisRequest = {
  siteGroupId: string;
  siteId: string;
  sitePosServerId?: string;
  terminalId: string;
  vendorSystemId?: string;
  referenceType: PayableBasisReferenceType;
  ticketReference?: string;
  plateNumber?: string;
  correlationId: string;
};

export type RevalidatePayableBasisRequest = {
  parkingSessionId: string;
  tariffSnapshotId: string;
  siteGroupId: string;
  siteId: string;
  sitePosServerId?: string;
  terminalId: string;
  vendorSystemId?: string;
  ticketReference?: string | null;
  plateNumber?: string | null;
  expectedAmountMinorUnits: number;
  expectedCurrency: string;
  correlationId: string;
};

export type PayableBasisRevalidationOutcome =
  | "PASSED_UNCHANGED"
  | "AMOUNT_CHANGED"
  | "TARIFF_EXPIRED"
  | "SESSION_NOT_PAYABLE"
  | "SESSION_ALREADY_PAID"
  | "TERMINAL_CASH_UNAVAILABLE"
  | "FISCAL_READINESS_FAILED"
  | "VENDOR_PMS_TEMPORARILY_UNAVAILABLE"
  | "REVALIDATION_FAILED"
  | "UNKNOWN";

export type PayableBasisResponse = {
  operation?: "resolve" | "revalidate" | string;
  revalidationOutcome?: PayableBasisRevalidationOutcome | string | null;
  parkingSessionId: string;
  tariffSnapshotId: string;
  siteGroupId: string;
  siteId: string;
  sitePosServerId?: string | null;
  terminalId?: string | null;
  siteGroupName?: string | null;
  siteName?: string | null;
  lookupOutcome?: string | null;
  ticketReference?: string | null;
  plateNumber?: string | null;
  entryTimestamp?: string | null;
  entryTime?: string | null;
  currentFeeCalculationTime?: string | null;
  parkingStatus: string;
  paymentStatus: string;
  authoritativeAmountMinorUnits: number;
  netPayableMinorUnits?: number;
  currency: string;
  tariffCalculatedAt?: string | null;
  tariffValidUntil: string;
  tariffExpiresAt?: string;
  feeValidUntil?: string | null;
  vendorSystemId?: string | null;
  statutoryDiscountApplied?: boolean;
  statutoryDiscountValidationId?: string | null;
  statutoryDiscountApplicationId?: string | null;
  originalTariffSnapshotId?: string | null;
  effectiveTariffSnapshotId?: string | null;
  appliedTariffSnapshotId?: string | null;
  policyResolutionBasis?: string | null;
  benefitType?: string | null;
  readinessDimensions?: Record<string, string | boolean | null> | null;
  sessionReadiness?: string | null;
  tariffReadiness?: string | null;
  paymentEligibility?: string | null;
  terminalCashAvailability?: string | null;
  fiscalReadiness?: string | null;
  salesInvoiceConfigurationReadiness?: string | null;
  cashAcceptanceReadiness?: string | null;
  readyForCashAcceptance: boolean;
  blockingReasonCodes: string[];
  retryable: boolean;
  safeUserFacingClassification: string;
  safeMessage?: string | null;
  correlationId: string;
};

export type ResolveVendorParkingRequest = ResolvePayableBasisRequest;
export type ResolveVendorParkingResponse = PayableBasisResponse;

export type CentralPmsErrorResponse = {
  errorCode: string;
  message: string;
  correlationId: string;
  retryable: boolean;
  details?: unknown;
};

export type CentralPmsFailureKind =
  | "not_found"
  | "inactive"
  | "closed"
  | "already_paid"
  | "ambiguous"
  | "service_unavailable"
  | "timeout"
  | "malformed_response"
  | "invalid_request"
  | "tariff_expired"
  | "cash_unavailable"
  | "fiscal_unavailable"
  | "amount_changed"
  | "unauthorized"
  | "unknown";

export type CentralPmsResult =
  | { ok: true; response: PayableBasisResponse }
  | { ok: false; kind: CentralPmsFailureKind; error: CentralPmsErrorResponse };

export interface CentralPmsClient {
  resolvePayableBasis(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
  ): Promise<CentralPmsResult>;
  revalidatePayableBasis(displayedBasis: PayableBasisResponse, correlationId: string): Promise<CentralPmsResult>;
  resolveTicket?(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
  recalculateFee?(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
}
