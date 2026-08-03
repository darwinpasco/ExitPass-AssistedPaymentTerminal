export type PayableBasisReferenceType = "ticket" | "plate";

export type StatutoryEntitlementType = "SENIOR_CITIZEN" | "PWD";

export type StatutoryOrdinanceAvailabilityClassification =
  | "AVAILABLE"
  | "NOT_AVAILABLE"
  | "NO_CONFIGURED_POLICY"
  | "NOT_YET_EFFECTIVE"
  | "EXPIRED"
  | "INACTIVE"
  | "AMBIGUOUS_SCOPE"
  | "SESSION_NOT_FOUND"
  | "AMBIGUOUS_SESSION"
  | "SOURCE_UNAVAILABLE"
  | "MALFORMED_AUTHORITATIVE_STATE"
  | "ACCESS_DENIED"
  | "UNEXPECTED_FAILURE";

export type StatutoryOrdinanceAvailabilityRequest = {
  siteGroupId: string;
  siteId: string;
  terminalId: string;
  vendorSystemId?: string;
  parkingSessionId: string;
  entitlementType: StatutoryEntitlementType;
  correlationId: string;
};

export type StatutoryOrdinanceAvailabilityResponse = {
  operation: "RESOLVE" | "REVALIDATE";
  revalidationOutcome?: "PASSED_UNCHANGED" | "FAILED" | null;
  classification: StatutoryOrdinanceAvailabilityClassification;
  entitlementType: StatutoryEntitlementType;
  ordinanceCoverageAvailable: boolean;
  statutoryRequestAllowed: boolean;
  preCashRevalidationPassed: boolean;
  readyForStatutoryCashFlow: boolean;
  ordinaryPaymentPreserved: boolean;
  parkingSessionId: string;
  siteId: string;
  siteGroupId: string;
  resolvedScopeType: string;
  coverageClassification: string;
  policyStatusClassification: string;
  effectiveFrom?: string | null;
  effectiveTo?: string | null;
  authorityClassification?: string | null;
  jurisdictionDisplayName?: string | null;
  supportReference: string;
  correlationId: string;
  evaluatedAt: string;
  authoritativeUpdatedAt?: string | null;
  retryable: boolean;
  safeMessage: string;
};

export type StatutoryOrdinanceAvailabilityResult =
  | { ok: true; response: StatutoryOrdinanceAvailabilityResponse }
  | { ok: false; kind: CentralPmsFailureKind; error: CentralPmsErrorResponse };

export type StatutoryOrdinanceAvailabilitySnapshot = {
  authoritative: false;
  parkingSessionId: string;
  siteId: string;
  siteGroupId: string;
  recordedAt: string;
  seniorCitizen?: StatutoryOrdinanceAvailabilityResponse | null;
  pwd?: StatutoryOrdinanceAvailabilityResponse | null;
};

export type StatutoryOrdinanceAvailabilityViewState =
  | { status: "idle" }
  | { status: "loading"; parkingSessionId: string; siteId: string; restoredRefresh: boolean }
  | {
      status: "ready";
      parkingSessionId: string;
      siteId: string;
      restoredRefresh: boolean;
      seniorCitizen: StatutoryOrdinanceAvailabilityResponse;
      pwd: StatutoryOrdinanceAvailabilityResponse;
    };

export type ResolvePayableBasisRequest = {
  siteGroupId: string;
  siteId: string;
  sitePosServerId?: string;
  terminalId: string;
  vendorSystemId?: string;
  referenceType: PayableBasisReferenceType;
  ticketReference?: string;
  plateNumber?: string;
  statutoryDiscountDecisionCommandId?: string | null;
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
  statutoryDiscountDecisionCommandId?: string | null;
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
  | "STATUTORY_DISCOUNT_BLOCKED"
  | "UNKNOWN";

export type StatutoryDiscountReadiness = {
  applicable: boolean;
  ready: boolean;
  statutoryDiscountDecisionCommandId?: string | null;
  statutoryDiscountValidationId?: string | null;
  statutoryDiscountPayableBasisApplicationCommandId?: string | null;
  entitlementType?: string | null;
  decisionStatus?: string | null;
  decisionResultStatus?: string | null;
  decisionCommandStatus?: string | null;
  applicationCommandStatus?: string | null;
  applicationResultClassification?: string | null;
  payableBasisReady: boolean;
  payableBasisReadinessStatus: string;
  payableBasisReadinessAction?: string | null;
  originalTariffSnapshotId?: string | null;
  appliedTariffSnapshotId?: string | null;
  originalAmountMinorUnits?: number | null;
  vatExclusiveBasisAmountMinorUnits?: number | null;
  vatAmountMinorUnits?: number | null;
  vatTreatment?: string | null;
  statutoryDiscountAmountMinorUnits?: number | null;
  finalPayableAmountMinorUnits?: number | null;
  currency?: string | null;
  retryable: boolean;
  recoveryClassification?: string | null;
  recoveryAction?: string | null;
  safeErrorCode?: string | null;
  blockingReasonCode?: string | null;
  message: string;
};

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
  statutoryDiscountReadiness?: StatutoryDiscountReadiness | null;
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

export type StatutoryDiscountEvidenceReference = {
  evidenceType: string;
  captureMethod: string;
  fileName?: string | null;
  contentType?: string | null;
  sizeBytes?: number | null;
  storageReference?: string | null;
  referenceNumberMasked?: string | null;
  verificationStatus?: string | null;
};

export type StatutoryDiscountDecisionSubmitRequest = {
  requestReference: string;
  sourceChannel: "ASSISTED_PAYMENT_TERMINAL";
  parkingSessionId: string;
  siteId?: string | null;
  siteGroupId?: string | null;
  ticketReference?: string | null;
  plateNumber?: string | null;
  entitlementType: string;
  idDocumentType: string;
  issuingAuthority: string;
  expiryDate?: string | null;
  maskedIdReference: string;
  evidenceCaptureRequested: boolean;
  evidenceReferences?: StatutoryDiscountEvidenceReference[] | null;
  requesterAttestation: boolean;
  attestationNotes?: string | null;
  reasonCode?: string | null;
  applyPayableBasis: boolean;
  originalTariffSnapshotId?: string | null;
};

export type StatutoryDiscountDecisionResponse = {
  statutoryDiscountDecisionCommandId: string;
  requestReference: string;
  statutoryDiscountValidationId?: string | null;
  parkingSessionId: string;
  sourceChannel: string;
  entitlementType: string;
  decisionStatus: string;
  policyResolutionBasis?: string | null;
  appliedPolicyReferenceId?: string | null;
  fallbackPolicyReferenceId?: string | null;
  localOrdinanceApplied: boolean;
  grossAmountMinorUnits?: number | null;
  statutoryDiscountAmountMinorUnits?: number | null;
  netPayableAmountMinorUnits?: number | null;
  currency?: string | null;
  evidenceRequired: boolean;
  evidenceRecorded: boolean;
  reasonCode?: string | null;
  errorCode?: string | null;
  correlationId: string;
  createdAt: string;
  decidedAt?: string | null;
  appliedAt?: string | null;
  originalTariffSnapshotId?: string | null;
  appliedTariffSnapshotId?: string | null;
  commandStatus: string;
  clientResultStatus: string;
  resultClassification: string;
  semanticHashSourceVersion: string;
  retryable: boolean;
  recoveryClassification: string;
  recoveryAction?: string | null;
  safeErrorCode?: string | null;
  decisionCommandStatus: string;
  decisionResultStatus?: string | null;
  decisionRetryable: boolean;
  decisionRecoveryClassification: string;
  decisionRecoveryAction?: string | null;
  statutoryDiscountPayableBasisApplicationCommandId?: string | null;
  applicationRequested: boolean;
  applicationCommandStatus: string;
  applicationResultClassification: string;
  applicationSemanticHashSourceVersion?: string | null;
  applicationRetryable: boolean;
  applicationRecoveryClassification: string;
  applicationRecoveryAction?: string | null;
  overallResultClassification: string;
  oneShotComplete: boolean;
  siteId?: string | null;
  siteGroupId?: string | null;
  vatExclusiveBasisAmountMinorUnits?: number | null;
  vatAmountMinorUnits?: number | null;
  vatTreatment?: string | null;
  payableBasisReady: boolean;
  payableBasisReadinessStatus: string;
  payableBasisReadinessAction?: string | null;
};

export type StatutoryDiscountWorkflowStatus =
  | "none"
  | "draft"
  | "submitting"
  | "awaiting_review"
  | "approved_application_not_requested"
  | "application_submitting"
  | "application_processing"
  | "applied"
  | "rejected"
  | "retryable_failure"
  | "terminal_failure"
  | "required_facts_unavailable";

export type StatutoryDiscountWorkflowState = {
  status: StatutoryDiscountWorkflowStatus;
  entitlementType?: string | null;
  maskedIdReference?: string | null;
  idDocumentType?: string | null;
  issuingAuthority?: string | null;
  expiryDate?: string | null;
  safeEvidenceReference?: string | null;
  requesterAttested?: boolean;
  attestationNotes?: string | null;
  requestReference?: string | null;
  decisionIdempotencyKey?: string | null;
  applicationIdempotencyKey?: string | null;
  statutoryDiscountDecisionCommandId?: string | null;
  statutoryDiscountPayableBasisApplicationCommandId?: string | null;
  decisionStatus?: string | null;
  decisionResultStatus?: string | null;
  applicationCommandStatus?: string | null;
  applicationResultClassification?: string | null;
  retryable?: boolean;
  recoveryClassification?: string | null;
  recoveryAction?: string | null;
  safeErrorCode?: string | null;
  originalTariffSnapshotId?: string | null;
  appliedTariffSnapshotId?: string | null;
  originalAmountMinorUnits?: number | null;
  vatExclusiveBasisAmountMinorUnits?: number | null;
  vatAmountMinorUnits?: number | null;
  vatTreatment?: string | null;
  statutoryDiscountAmountMinorUnits?: number | null;
  finalPayableAmountMinorUnits?: number | null;
  currency?: string | null;
  payableBasisReady?: boolean;
  payableBasisReadinessStatus?: string | null;
  payableBasisReadinessAction?: string | null;
  correlationId?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  lastReadbackAt?: string | null;
  restoredAfterRestart?: boolean;
  amountAcknowledged?: boolean;
  ordinanceAvailability?: StatutoryOrdinanceAvailabilitySnapshot | null;
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
  | "statutory_blocked"
  | "unknown";

export type CentralPmsResult =
  | { ok: true; response: PayableBasisResponse }
  | { ok: false; kind: CentralPmsFailureKind; error: CentralPmsErrorResponse };

export type StatutoryDiscountDecisionResult =
  | { ok: true; response: StatutoryDiscountDecisionResponse }
  | { ok: false; kind: CentralPmsFailureKind; error: CentralPmsErrorResponse };

export interface CentralPmsClient {
  resolvePayableBasis(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
    statutoryDiscountDecisionCommandId?: string | null,
  ): Promise<CentralPmsResult>;
  revalidatePayableBasis(displayedBasis: PayableBasisResponse, correlationId: string): Promise<CentralPmsResult>;
  resolveStatutoryOrdinanceAvailability?(
    displayedBasis: PayableBasisResponse,
    entitlementType: StatutoryEntitlementType,
    correlationId: string,
  ): Promise<StatutoryOrdinanceAvailabilityResult>;
  revalidateStatutoryOrdinanceAvailability?(
    displayedBasis: PayableBasisResponse,
    entitlementType: StatutoryEntitlementType,
    correlationId: string,
  ): Promise<StatutoryOrdinanceAvailabilityResult>;
  submitStatutoryDiscountDecision?(
    request: StatutoryDiscountDecisionSubmitRequest,
    correlationId: string,
    idempotencyKey: string,
  ): Promise<StatutoryDiscountDecisionResult>;
  getStatutoryDiscountDecision?(decisionCommandId: string, correlationId: string): Promise<StatutoryDiscountDecisionResult>;
  resolveTicket?(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
  recalculateFee?(ticketReference: string, correlationId: string): Promise<CentralPmsResult>;
}
