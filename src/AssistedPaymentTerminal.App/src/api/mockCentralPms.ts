import type { AptConfig } from "../config";
import type {
  CentralPmsClient,
  CentralPmsResult,
  PayableBasisReferenceType,
  PayableBasisResponse,
  StatutoryDiscountDecisionResponse,
  StatutoryDiscountDecisionResult,
  StatutoryDiscountDecisionSubmitRequest,
  StatutoryDiscountReadiness,
} from "./centralPmsTypes";

const ids = {
  activeSession: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  plateSession: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1002",
  expiredSession: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb2001",
  blockedSession: "cccccccc-cccc-4ccc-8ccc-cccccccc3001",
  statutoryDecision: "77777777-7777-4777-8777-777777770001",
  statutoryApplication: "88888888-8888-4888-8888-888888880001",
  statutoryAppliedSnapshot: "99999999-9999-4999-8999-999999990001",
};

export class MockCentralPmsClient implements CentralPmsClient {
  private readonly statutoryByDecisionId = new Map<string, StatutoryDiscountDecisionResponse>();

  constructor(private readonly config: AptConfig) {}

  async resolvePayableBasis(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
    statutoryDiscountDecisionCommandId?: string | null,
  ): Promise<CentralPmsResult> {
    return this.resolveScenario(referenceType, referenceValue.trim().toUpperCase(), correlationId, statutoryDiscountDecisionCommandId);
  }

  async resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async recalculateFee(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async submitStatutoryDiscountDecision(
    request: StatutoryDiscountDecisionSubmitRequest,
    correlationId: string,
    idempotencyKey: string,
  ): Promise<StatutoryDiscountDecisionResult> {
    await delay(120);
    if (!idempotencyKey.trim()) {
      return failure("invalid_request", "INVALID_REQUEST", "Idempotency-Key is required.", correlationId, false);
    }

    const existing = [...this.statutoryByDecisionId.values()].find((value) => value.requestReference === request.requestReference && value.parkingSessionId === request.parkingSessionId);
    if (existing && !request.applyPayableBasis) {
      return { ok: true, response: { ...existing, correlationId, resultClassification: "IDEMPOTENT_REPLAY" } };
    }

    const scenario = (request.maskedIdReference || request.entitlementType).toUpperCase();
    const decisionId = existing?.statutoryDiscountDecisionCommandId ?? ids.statutoryDecision;
    let response: StatutoryDiscountDecisionResponse;

    if (!request.applyPayableBasis) {
      response = this.decisionResponse({
        request,
        correlationId,
        decisionId,
        status: scenario.includes("REJECT") ? "rejected" : scenario.includes("RETRY") ? "retryable" : scenario.includes("TERMINAL") ? "terminal" : "awaiting",
      });
    } else {
      if (scenario.includes("MISSING")) {
        response = this.decisionResponse({ request, correlationId, decisionId, status: "missing", applicationId: ids.statutoryApplication });
      } else if (scenario.includes("PROCESSING")) {
        response = this.decisionResponse({ request, correlationId, decisionId, status: "processing", applicationId: ids.statutoryApplication });
      } else {
        response = this.decisionResponse({ request, correlationId, decisionId, status: "applied", applicationId: ids.statutoryApplication });
      }
    }

    this.statutoryByDecisionId.set(response.statutoryDiscountDecisionCommandId, response);
    return { ok: true, response };
  }

  async getStatutoryDiscountDecision(decisionCommandId: string, correlationId: string): Promise<StatutoryDiscountDecisionResult> {
    await delay(120);
    const current = this.statutoryByDecisionId.get(decisionCommandId) ?? this.seededDecision(decisionCommandId, correlationId);
    if (!current) {
      return failure("not_found", "STATUTORY_DISCOUNT_DECISION_NOT_FOUND", "Statutory discount decision was not found.", correlationId, false);
    }

    if (current.applicationCommandStatus === "PROCESSING") {
      const applied = this.decisionResponse({
        request: requestFromDecision(current),
        correlationId,
        decisionId: current.statutoryDiscountDecisionCommandId,
        status: "applied",
        applicationId: current.statutoryDiscountPayableBasisApplicationCommandId ?? ids.statutoryApplication,
      });
      this.statutoryByDecisionId.set(applied.statutoryDiscountDecisionCommandId, applied);
      return { ok: true, response: applied };
    }

    return { ok: true, response: { ...current, correlationId, lastReadbackAt: undefined } as StatutoryDiscountDecisionResponse };
  }

  async revalidatePayableBasis(displayedBasis: PayableBasisResponse, correlationId: string): Promise<CentralPmsResult> {
    await delay(120);
    const reference = (displayedBasis.ticketReference ?? displayedBasis.plateNumber ?? "").toUpperCase();

    if (displayedBasis.statutoryDiscountReadiness?.applicable && !displayedBasis.statutoryDiscountReadiness.ready) {
      return {
        ok: true,
        response: {
          ...displayedBasis,
          operation: "revalidate",
          revalidationOutcome: "STATUTORY_DISCOUNT_BLOCKED",
          readyForCashAcceptance: false,
          blockingReasonCodes: [displayedBasis.statutoryDiscountReadiness.blockingReasonCode ?? "STATUTORY_DISCOUNT_AWAITING_REVIEW"],
          retryable: Boolean(displayedBasis.statutoryDiscountReadiness.retryable),
          safeUserFacingClassification: "STATUTORY_DISCOUNT_BLOCKED",
          correlationId,
        },
      };
    }

    if (reference.includes("AMOUNT-CHANGED")) {
      return {
        ok: true,
        response: this.response({
          referenceType: displayedBasis.ticketReference ? "ticket" : "plate",
          referenceValue: reference,
          correlationId,
          parkingSessionId: displayedBasis.parkingSessionId,
          amountMinorUnits: displayedBasis.authoritativeAmountMinorUnits + 2500,
          revalidationOutcome: "AMOUNT_CHANGED",
          ready: false,
          blockingReasonCodes: ["AMOUNT_CHANGED"],
          classification: "AMOUNT_CHANGED",
          tariffMinutesFromNow: 15,
          suffix: "9002",
        }),
      };
    }

    if (reference.includes("STATUTORY-BLOCKED")) {
      return {
        ok: true,
        response: {
          ...displayedBasis,
          operation: "revalidate",
          revalidationOutcome: "STATUTORY_DISCOUNT_BLOCKED",
          readyForCashAcceptance: false,
          blockingReasonCodes: ["STATUTORY_DISCOUNT_APPLICATION_PROCESSING"],
          retryable: true,
          safeUserFacingClassification: "STATUTORY_DISCOUNT_BLOCKED",
          safeMessage: "Statutory request is no longer ready for cash acceptance.",
          correlationId,
          statutoryDiscountReadiness: displayedBasis.statutoryDiscountReadiness
            ? {
                ...displayedBasis.statutoryDiscountReadiness,
                ready: false,
                payableBasisReady: false,
                payableBasisReadinessStatus: "APPLICATION_PROCESSING",
                payableBasisReadinessAction: "POLL_READBACK",
                blockingReasonCode: "STATUTORY_DISCOUNT_APPLICATION_PROCESSING",
                message: "Statutory payable basis is being applied.",
              }
            : displayedBasis.statutoryDiscountReadiness,
        },
      };
    }

    if (reference.includes("REVAL-FAIL")) {
      return failure("service_unavailable", "VENDOR_PMS_UNAVAILABLE", "Central PMS could not revalidate the payable basis. Try again before accepting cash.", correlationId, true);
    }

    if (reference.includes("ALREADY-PAID")) {
      return {
        ok: true,
        response: this.response({
          referenceType: displayedBasis.ticketReference ? "ticket" : "plate",
          referenceValue: reference,
          correlationId,
          parkingSessionId: displayedBasis.parkingSessionId,
          revalidationOutcome: "SESSION_ALREADY_PAID",
          ready: false,
          blockingReasonCodes: ["PAYMENT_ALREADY_FINAL"],
          classification: "SESSION_ALREADY_PAID",
          paymentStatus: "Paid",
        }),
      };
    }

    return {
      ok: true,
      response: {
        ...displayedBasis,
        operation: "revalidate",
        revalidationOutcome: "PASSED_UNCHANGED",
        readyForCashAcceptance: true,
        blockingReasonCodes: [],
        retryable: false,
        safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
        correlationId,
      },
    };
  }

  private async resolveScenario(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
    statutoryDiscountDecisionCommandId?: string | null,
  ): Promise<CentralPmsResult> {
    await delay(120);

    if (!referenceValue) {
      return failure("invalid_request", "INVALID_REQUEST", "Enter one ticket or plate reference.", correlationId, false);
    }

    if (referenceValue === "APT-NOTFOUND-404" || referenceValue === "PLATE-NOTFOUND") {
      return failure("not_found", "SESSION_NOT_FOUND", "Parking session was not found.", correlationId, false);
    }

    if (referenceValue === "APT-AMBIG-409" || referenceValue === "PLATE-AMBIG") {
      return failure("ambiguous", "VENDOR_SESSION_AMBIGUOUS", "Multiple matching parking sessions require review.", correlationId, false);
    }

    if (referenceValue === "APT-UNAVAILABLE-503" || referenceValue === "PLATE-UNAVAILABLE") {
      return failure("service_unavailable", "VENDOR_PMS_UNAVAILABLE", "Vendor PMS is temporarily unavailable.", correlationId, true);
    }

    if (referenceValue === "APT-MALFORMED-502") {
      return failure("malformed_response", "MALFORMED_VENDOR_RESPONSE", "Vendor PMS returned a malformed response.", correlationId, false);
    }

    if (referenceValue === "APT-INACTIVE-3001") {
      return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, parkingSessionId: ids.blockedSession, ready: false, parkingStatus: "Inactive", blockingReasonCodes: ["SESSION_NOT_PAYABLE"], classification: "SESSION_NOT_PAYABLE" }) };
    }

    if (referenceValue === "APT-ALREADY-PAID") {
      return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, parkingSessionId: ids.blockedSession, ready: false, paymentStatus: "Paid", blockingReasonCodes: ["PAYMENT_ALREADY_FINAL"], classification: "SESSION_ALREADY_PAID" }) };
    }

    if (referenceValue === "APT-EXPIRED-2001") {
      return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, parkingSessionId: ids.expiredSession, ready: false, tariffMinutesFromNow: -5, blockingReasonCodes: ["STALE_TARIFF"], classification: "TARIFF_EXPIRED", tariffReadiness: "EXPIRED" }) };
    }

    if (referenceValue === "APT-CASH-BLOCKED") {
      return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, ready: false, blockingReasonCodes: ["CASH_PAYMENT_RAIL_NOT_CONFIGURED"], classification: "TERMINAL_CASH_UNAVAILABLE", terminalCashAvailability: "UNAVAILABLE" }) };
    }

    if (referenceValue === "APT-FISCAL-BLOCKED") {
      return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, ready: false, blockingReasonCodes: ["SALES_INVOICE_CONFIGURATION_NOT_READY"], classification: "FISCAL_READINESS_FAILED", fiscalReadiness: "NOT_READY", salesInvoiceConfigurationReadiness: "INCOMPLETE" }) };
    }

    const parkingSessionId = referenceType === "plate" ? ids.plateSession : ids.activeSession;
    return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, parkingSessionId, statutoryDiscountDecisionCommandId }) };
  }

  private response({
    referenceType,
    referenceValue,
    correlationId,
    parkingSessionId = ids.activeSession,
    amountMinorUnits = 12500,
    tariffMinutesFromNow = 18,
    revalidationOutcome = null,
    ready = true,
    blockingReasonCodes = [],
    classification = ready ? "READY_FOR_CASH_ACCEPTANCE" : "CASH_ACCEPTANCE_BLOCKED",
    parkingStatus = "Active",
    paymentStatus = "Unpaid",
    tariffReadiness = ready ? "CURRENT" : "BLOCKED",
    terminalCashAvailability = ready ? "AVAILABLE" : "BLOCKED",
    fiscalReadiness = ready ? "READY" : "BLOCKED",
    salesInvoiceConfigurationReadiness = ready ? "READY" : "BLOCKED",
    suffix,
    statutoryDiscountDecisionCommandId,
  }: {
    referenceType: PayableBasisReferenceType;
    referenceValue: string;
    correlationId: string;
    parkingSessionId?: string;
    amountMinorUnits?: number;
    tariffMinutesFromNow?: number;
    revalidationOutcome?: string | null;
    ready?: boolean;
    blockingReasonCodes?: string[];
    classification?: string;
    parkingStatus?: string;
    paymentStatus?: string;
    tariffReadiness?: string;
    terminalCashAvailability?: string;
    fiscalReadiness?: string;
    salesInvoiceConfigurationReadiness?: string;
    suffix?: string;
    statutoryDiscountDecisionCommandId?: string | null;
  }): PayableBasisResponse {
    const now = new Date();
    const entryTimestamp = new Date(now.getTime() - 2 * 60 * 60 * 1000);
    const calculatedAt = new Date(now.getTime() - 2 * 60 * 1000);
    const validUntil = new Date(now.getTime() + tariffMinutesFromNow * 60 * 1000);
    const snapshotSuffix = suffix ?? (referenceValue.includes("EXPIRED") ? "2001" : referenceType === "plate" ? "1002" : "1001");
    const statutory = statutoryDiscountDecisionCommandId ? this.statutoryReadiness(statutoryDiscountDecisionCommandId) : null;
    const statutoryReady = !statutory || statutory.ready;
    const effectiveReady = ready && statutoryReady;
    const statutoryBlocker = statutory && !statutory.ready ? [statutory.blockingReasonCode ?? "STATUTORY_DISCOUNT_AWAITING_REVIEW"] : [];
    const appliedAmount = statutory?.ready && statutory.finalPayableAmountMinorUnits != null ? statutory.finalPayableAmountMinorUnits : amountMinorUnits;
    const appliedSnapshot = statutory?.ready && statutory.appliedTariffSnapshotId ? statutory.appliedTariffSnapshotId : `dddddddd-dddd-4ddd-8ddd-dddddddd${snapshotSuffix}`;

    return {
      operation: revalidationOutcome ? "revalidate" : "resolve",
      revalidationOutcome,
      parkingSessionId,
      tariffSnapshotId: appliedSnapshot,
      siteGroupId: this.config.siteGroupId,
      siteId: this.config.siteId,
      sitePosServerId: this.config.posServerId,
      terminalId: this.config.terminalId,
      siteGroupName: "ExitPass Development Group",
      lookupOutcome: "resolved",
      siteName: this.config.siteName,
      ticketReference: referenceType === "ticket" ? referenceValue : null,
      plateNumber: referenceType === "plate" ? referenceValue : "NCR-4421",
      entryTimestamp: entryTimestamp.toISOString(),
      entryTime: entryTimestamp.toISOString(),
      currentFeeCalculationTime: calculatedAt.toISOString(),
      parkingStatus,
      paymentStatus,
      authoritativeAmountMinorUnits: appliedAmount,
      netPayableMinorUnits: appliedAmount,
      currency: statutory?.currency ?? "PHP",
      tariffCalculatedAt: calculatedAt.toISOString(),
      tariffValidUntil: validUntil.toISOString(),
      tariffExpiresAt: validUntil.toISOString(),
      feeValidUntil: validUntil.toISOString(),
      vendorSystemId: this.config.vendorSystemId,
      statutoryDiscountApplied: Boolean(statutory?.ready),
      statutoryDiscountValidationId: statutory?.statutoryDiscountValidationId ?? null,
      statutoryDiscountApplicationId: statutory?.statutoryDiscountPayableBasisApplicationCommandId ?? null,
      statutoryDiscountReadiness: statutory,
      originalTariffSnapshotId: statutory?.originalTariffSnapshotId ?? null,
      effectiveTariffSnapshotId: appliedSnapshot,
      appliedTariffSnapshotId: statutory?.appliedTariffSnapshotId ?? null,
      policyResolutionBasis: null,
      benefitType: statutory?.entitlementType ?? null,
      readinessDimensions: null,
      sessionReadiness: parkingStatus === "Active" ? "RESOLVED_PAYABLE" : "NOT_PAYABLE",
      tariffReadiness,
      paymentEligibility: paymentStatus === "Paid" ? "PAYMENT_ALREADY_FINAL" : "ELIGIBLE",
      terminalCashAvailability,
      fiscalReadiness,
      salesInvoiceConfigurationReadiness,
      cashAcceptanceReadiness: effectiveReady ? "READY" : "BLOCKED",
      readyForCashAcceptance: effectiveReady,
      blockingReasonCodes: [...blockingReasonCodes, ...statutoryBlocker],
      retryable: classification.includes("UNAVAILABLE") || Boolean(statutory?.retryable),
      safeUserFacingClassification: effectiveReady ? classification : statutory?.blockingReasonCode ?? classification,
      safeMessage: effectiveReady ? "Cash may be accepted after immediate revalidation." : "Cash acceptance is blocked by Central PMS readiness.",
      correlationId,
    };
  }

  private statutoryReadiness(decisionCommandId: string): StatutoryDiscountReadiness | null {
    const response = this.statutoryByDecisionId.get(decisionCommandId) ?? this.seededDecision(decisionCommandId, crypto.randomUUID());
    if (!response) return null;
    const status = response.payableBasisReadinessStatus;
    const ready = response.payableBasisReady && response.applicationCommandStatus === "APPLIED" && Boolean(response.appliedTariffSnapshotId) && response.netPayableAmountMinorUnits != null && Boolean(response.currency);
    return {
      applicable: true,
      ready,
      statutoryDiscountDecisionCommandId: response.statutoryDiscountDecisionCommandId,
      statutoryDiscountValidationId: response.statutoryDiscountValidationId,
      statutoryDiscountPayableBasisApplicationCommandId: response.statutoryDiscountPayableBasisApplicationCommandId,
      entitlementType: response.entitlementType,
      decisionStatus: response.decisionStatus,
      decisionResultStatus: response.decisionResultStatus,
      decisionCommandStatus: response.decisionCommandStatus,
      applicationCommandStatus: response.applicationCommandStatus,
      applicationResultClassification: response.applicationResultClassification,
      payableBasisReady: response.payableBasisReady,
      payableBasisReadinessStatus: response.payableBasisReadinessStatus,
      payableBasisReadinessAction: response.payableBasisReadinessAction,
      originalTariffSnapshotId: response.originalTariffSnapshotId,
      appliedTariffSnapshotId: response.appliedTariffSnapshotId,
      originalAmountMinorUnits: response.grossAmountMinorUnits,
      vatExclusiveBasisAmountMinorUnits: response.vatExclusiveBasisAmountMinorUnits,
      vatAmountMinorUnits: response.vatAmountMinorUnits,
      vatTreatment: response.vatTreatment,
      statutoryDiscountAmountMinorUnits: response.statutoryDiscountAmountMinorUnits,
      finalPayableAmountMinorUnits: response.netPayableAmountMinorUnits,
      currency: response.currency,
      retryable: response.retryable,
      recoveryClassification: response.recoveryClassification,
      recoveryAction: response.recoveryAction,
      safeErrorCode: response.safeErrorCode,
      blockingReasonCode: ready ? null : blockerForReadinessStatus(status),
      message: ready ? "Statutory payable basis is applied." : friendlyStatus(status),
    };
  }

  private seededDecision(decisionCommandId: string, correlationId: string): StatutoryDiscountDecisionResponse | null {
    if (decisionCommandId === "77777777-7777-4777-8777-777777770777") {
      const response = this.decisionResponse({ request: baseDecisionRequest(this.config), correlationId, decisionId: decisionCommandId, status: "applied", applicationId: ids.statutoryApplication });
      this.statutoryByDecisionId.set(decisionCommandId, response);
      return response;
    }
    return null;
  }

  private decisionResponse({
    request,
    correlationId,
    decisionId,
    status,
    applicationId = null,
  }: {
    request: StatutoryDiscountDecisionSubmitRequest;
    correlationId: string;
    decisionId: string;
    status: "awaiting" | "rejected" | "retryable" | "terminal" | "processing" | "applied" | "missing";
    applicationId?: string | null;
  }): StatutoryDiscountDecisionResponse {
    const now = new Date().toISOString();
    const approved = status === "processing" || status === "applied" || status === "missing";
    const rejected = status === "rejected";
    const retryable = status === "retryable";
    const terminal = status === "terminal";
    const applied = status === "applied";
    const missing = status === "missing";
    const processing = status === "processing";
    const readinessStatus = applied ? "APPLIED" : processing ? "APPLICATION_PROCESSING" : missing ? "REQUIRED_FACTS_UNAVAILABLE" : rejected ? "DECISION_REJECTED" : retryable ? "RETRYABLE_FAILURE" : terminal ? "TERMINAL_FAILURE" : approved ? "DECISION_APPROVED_APPLICATION_NOT_REQUESTED" : "AWAITING_REVIEW";

    return {
      statutoryDiscountDecisionCommandId: decisionId,
      requestReference: request.requestReference,
      statutoryDiscountValidationId: "66666666-6666-4666-8666-666666660001",
      parkingSessionId: request.parkingSessionId,
      sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
      entitlementType: request.entitlementType,
      decisionStatus: rejected || approved ? "COMPLETED" : retryable || terminal ? "FAILED" : "AWAITING_REVIEW",
      policyResolutionBasis: applied ? "CENTRAL_PMS_STATUTORY_POLICY" : null,
      appliedPolicyReferenceId: applied ? "55555555-5555-4555-8555-555555550001" : null,
      fallbackPolicyReferenceId: null,
      localOrdinanceApplied: false,
      grossAmountMinorUnits: 12500,
      statutoryDiscountAmountMinorUnits: applied || missing ? (missing ? null : 2500) : null,
      netPayableAmountMinorUnits: applied ? 10000 : null,
      currency: applied || missing ? (missing ? null : "PHP") : null,
      evidenceRequired: false,
      evidenceRecorded: Boolean(request.evidenceReferences?.length),
      reasonCode: request.reasonCode ?? null,
      errorCode: retryable ? "STATUTORY_DISCOUNT_DECISION_TEMPORARILY_UNAVAILABLE" : terminal ? "STATUTORY_DISCOUNT_DECISION_TERMINAL_FAILURE" : missing ? "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE" : null,
      correlationId,
      createdAt: now,
      decidedAt: approved || rejected ? now : null,
      appliedAt: applied ? now : null,
      originalTariffSnapshotId: request.originalTariffSnapshotId ?? "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
      appliedTariffSnapshotId: applied ? ids.statutoryAppliedSnapshot : null,
      commandStatus: retryable || terminal ? "FAILED" : rejected || approved ? "COMPLETED" : "AWAITING_REVIEW",
      clientResultStatus: retryable ? "RETRYABLE_FAILURE" : terminal ? "TERMINAL_FAILURE" : "ACCEPTED",
      resultClassification: retryable ? "RETRYABLE_FAILURE" : terminal ? "TERMINAL_FAILURE" : rejected ? "DECISION_REJECTED" : applied ? "APPLIED" : processing ? "APPLICATION_PROCESSING" : "AWAITING_REVIEW",
      semanticHashSourceVersion: "statutory-discount-decision-v1",
      retryable,
      recoveryClassification: retryable ? "RETRY_ORIGINAL_IDEMPOTENCY_KEY" : "NONE",
      recoveryAction: retryable ? "WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY" : rejected || terminal ? "DO_NOT_RETRY" : readinessAction(readinessStatus),
      safeErrorCode: retryable ? "STATUTORY_DISCOUNT_RETRYABLE_FAILURE" : terminal ? "STATUTORY_DISCOUNT_TERMINAL_FAILURE" : missing ? "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE" : null,
      decisionCommandStatus: retryable || terminal ? "FAILED" : rejected || approved ? "COMPLETED" : "AWAITING_REVIEW",
      decisionResultStatus: rejected ? "REJECTED" : approved ? "APPROVED" : "NOT_DECIDED",
      decisionRetryable: retryable,
      decisionRecoveryClassification: retryable ? "RETRY_ORIGINAL_IDEMPOTENCY_KEY" : "NONE",
      decisionRecoveryAction: retryable ? "WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY" : null,
      statutoryDiscountPayableBasisApplicationCommandId: applicationId,
      applicationRequested: Boolean(applicationId),
      applicationCommandStatus: applied ? "APPLIED" : processing || missing ? "PROCESSING" : "NOT_REQUESTED",
      applicationResultClassification: applied ? "APPLIED" : processing ? "APPLICATION_PROCESSING" : missing ? "REQUIRED_FACTS_UNAVAILABLE" : "NOT_REQUESTED",
      applicationSemanticHashSourceVersion: applicationId ? "statutory-discount-payable-basis-application-v1" : null,
      applicationRetryable: false,
      applicationRecoveryClassification: "NONE",
      applicationRecoveryAction: null,
      overallResultClassification: applied ? "ACCEPTED" : "NOT_READY",
      oneShotComplete: applied,
      siteId: request.siteId ?? this.config.siteId,
      siteGroupId: request.siteGroupId ?? this.config.siteGroupId,
      vatExclusiveBasisAmountMinorUnits: applied ? 8929 : null,
      vatAmountMinorUnits: applied ? 1071 : null,
      vatTreatment: applied ? "VAT_EXEMPT_WITH_DISCOUNT" : null,
      payableBasisReady: applied,
      payableBasisReadinessStatus: readinessStatus,
      payableBasisReadinessAction: readinessAction(readinessStatus),
    };
  }
}

function baseDecisionRequest(config: AptConfig): StatutoryDiscountDecisionSubmitRequest {
  return {
    requestReference: "99999999-9999-4999-8999-999999990999",
    sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
    parkingSessionId: ids.activeSession,
    siteId: config.siteId,
    siteGroupId: config.siteGroupId,
    ticketReference: "APT-ACTIVE-1001",
    plateNumber: "NCR-4421",
    entitlementType: "SENIOR_CITIZEN",
    idDocumentType: "OSCA_ID",
    issuingAuthority: "OSCA",
    expiryDate: null,
    maskedIdReference: "SC-****-0001",
    evidenceCaptureRequested: false,
    evidenceReferences: null,
    requesterAttestation: true,
    attestationNotes: null,
    reasonCode: null,
    applyPayableBasis: false,
    originalTariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
  };
}

function requestFromDecision(response: StatutoryDiscountDecisionResponse): StatutoryDiscountDecisionSubmitRequest {
  return {
    ...baseDecisionRequest({ siteId: response.siteId ?? "11111111-1111-1111-1111-111111111111", siteGroupId: response.siteGroupId ?? "22222222-2222-2222-2222-222222222222" } as AptConfig),
    requestReference: response.requestReference,
    parkingSessionId: response.parkingSessionId,
    entitlementType: response.entitlementType,
    originalTariffSnapshotId: response.originalTariffSnapshotId,
    applyPayableBasis: true,
  };
}

function readinessAction(status: string): string | null {
  const actions: Record<string, string | null> = {
    AWAITING_REVIEW: "POLL_READBACK",
    DECISION_APPROVED_APPLICATION_NOT_REQUESTED: "SUBMIT_APPLICATION_INTENT",
    APPLICATION_PROCESSING: "POLL_READBACK",
    DECISION_REJECTED: "DO_NOT_RETRY",
    RETRYABLE_FAILURE: "WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY",
    TERMINAL_FAILURE: "DO_NOT_RETRY",
    REQUIRED_FACTS_UNAVAILABLE: "DO_NOT_RETRY",
    APPLIED: null,
  };
  return actions[status] ?? null;
}

function blockerForReadinessStatus(status: string): string {
  const blockers: Record<string, string> = {
    AWAITING_REVIEW: "STATUTORY_DISCOUNT_AWAITING_REVIEW",
    DECISION_APPROVED_APPLICATION_NOT_REQUESTED: "STATUTORY_DISCOUNT_APPLICATION_NOT_REQUESTED",
    APPLICATION_PROCESSING: "STATUTORY_DISCOUNT_APPLICATION_PROCESSING",
    DECISION_REJECTED: "STATUTORY_DISCOUNT_DECISION_REJECTED",
    RETRYABLE_FAILURE: "STATUTORY_DISCOUNT_RETRYABLE_FAILURE",
    TERMINAL_FAILURE: "STATUTORY_DISCOUNT_TERMINAL_FAILURE",
    REQUIRED_FACTS_UNAVAILABLE: "STATUTORY_DISCOUNT_REQUIRED_FACTS_UNAVAILABLE",
  };
  return blockers[status] ?? "STATUTORY_DISCOUNT_STATE_INCONSISTENT";
}

function friendlyStatus(status: string): string {
  return status.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}

function failure(
  kind: Exclude<CentralPmsResult, { ok: true }>["kind"],
  errorCode: string,
  message: string,
  correlationId: string,
  retryable: boolean,
): Exclude<CentralPmsResult, { ok: true }> {
  return {
    ok: false,
    kind,
    error: {
      errorCode,
      message,
      correlationId,
      retryable,
    },
  };
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}
