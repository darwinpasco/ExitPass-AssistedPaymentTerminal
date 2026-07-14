export type ResolveVendorParkingRequest = {
  siteGroupId: string;
  siteId: string;
  vendorSystemId: string;
  plateNumber?: string;
  ticketReference?: string;
  correlationId: string;
};

export type ResolveVendorParkingResponse = {
  parkingSessionId: string;
  tariffSnapshotId: string;
  siteGroupId: string;
  siteId: string;
  siteGroupName?: string | null;
  siteName?: string | null;
  lookupOutcome: string;
  plateNumber?: string | null;
  ticketReference?: string | null;
  entryTime?: string | null;
  currentFeeCalculationTime?: string | null;
  netPayableMinorUnits: number;
  currency: string;
  tariffExpiresAt: string;
  feeValidUntil: string;
  parkingStatus: string;
  paymentStatus: string;
  statutoryDiscountApplied: boolean;
  statutoryDiscountValidationId?: string | null;
  statutoryDiscountApplicationId?: string | null;
  originalTariffSnapshotId?: string | null;
  effectiveTariffSnapshotId?: string | null;
  appliedTariffSnapshotId?: string | null;
  policyResolutionBasis?: string | null;
  benefitType?: string | null;
  vendorSystemId: string;
  correlationId: string;
};

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
  | "ambiguous"
  | "service_unavailable"
  | "timeout"
  | "malformed_response"
  | "invalid_request"
  | "recalculation_pending"
  | "unknown";

export type CentralPmsResult =
  | { ok: true; response: ResolveVendorParkingResponse }
  | { ok: false; kind: CentralPmsFailureKind; error: CentralPmsErrorResponse };

export interface CentralPmsClient {
  resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
  recalculateFee(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
}
