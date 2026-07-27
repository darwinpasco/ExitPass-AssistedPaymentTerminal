import type { AptConfig } from "../config";
import type {
  CentralPmsClient,
  CentralPmsResult,
  PayableBasisReferenceType,
  PayableBasisResponse,
} from "./centralPmsTypes";

const ids = {
  activeSession: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  plateSession: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1002",
  expiredSession: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb2001",
  blockedSession: "cccccccc-cccc-4ccc-8ccc-cccccccc3001",
};

export class MockCentralPmsClient implements CentralPmsClient {
  constructor(private readonly config: AptConfig) {}

  async resolvePayableBasis(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
  ): Promise<CentralPmsResult> {
    return this.resolveScenario(referenceType, referenceValue.trim().toUpperCase(), correlationId);
  }

  async resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async recalculateFee(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async revalidatePayableBasis(displayedBasis: PayableBasisResponse, correlationId: string): Promise<CentralPmsResult> {
    await delay(120);
    const reference = (displayedBasis.ticketReference ?? displayedBasis.plateNumber ?? "").toUpperCase();

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
      return {
        ok: true,
        response: this.response({
          referenceType,
          referenceValue,
          correlationId,
          parkingSessionId: ids.blockedSession,
          ready: false,
          parkingStatus: "Inactive",
          blockingReasonCodes: ["SESSION_NOT_PAYABLE"],
          classification: "SESSION_NOT_PAYABLE",
        }),
      };
    }

    if (referenceValue === "APT-ALREADY-PAID") {
      return {
        ok: true,
        response: this.response({
          referenceType,
          referenceValue,
          correlationId,
          parkingSessionId: ids.blockedSession,
          ready: false,
          paymentStatus: "Paid",
          blockingReasonCodes: ["PAYMENT_ALREADY_FINAL"],
          classification: "SESSION_ALREADY_PAID",
        }),
      };
    }

    if (referenceValue === "APT-EXPIRED-2001") {
      return {
        ok: true,
        response: this.response({
          referenceType,
          referenceValue,
          correlationId,
          parkingSessionId: ids.expiredSession,
          ready: false,
          tariffMinutesFromNow: -5,
          blockingReasonCodes: ["STALE_TARIFF"],
          classification: "TARIFF_EXPIRED",
          tariffReadiness: "EXPIRED",
        }),
      };
    }

    if (referenceValue === "APT-CASH-BLOCKED") {
      return {
        ok: true,
        response: this.response({
          referenceType,
          referenceValue,
          correlationId,
          ready: false,
          blockingReasonCodes: ["CASH_PAYMENT_RAIL_NOT_CONFIGURED"],
          classification: "TERMINAL_CASH_UNAVAILABLE",
          terminalCashAvailability: "UNAVAILABLE",
        }),
      };
    }

    if (referenceValue === "APT-FISCAL-BLOCKED") {
      return {
        ok: true,
        response: this.response({
          referenceType,
          referenceValue,
          correlationId,
          ready: false,
          blockingReasonCodes: ["SALES_INVOICE_CONFIGURATION_NOT_READY"],
          classification: "FISCAL_READINESS_FAILED",
          fiscalReadiness: "NOT_READY",
          salesInvoiceConfigurationReadiness: "INCOMPLETE",
        }),
      };
    }

    const parkingSessionId = referenceType === "plate" ? ids.plateSession : ids.activeSession;
    return { ok: true, response: this.response({ referenceType, referenceValue, correlationId, parkingSessionId }) };
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
  }): PayableBasisResponse {
    const now = new Date();
    const entryTimestamp = new Date(now.getTime() - 2 * 60 * 60 * 1000);
    const calculatedAt = new Date(now.getTime() - 2 * 60 * 1000);
    const validUntil = new Date(now.getTime() + tariffMinutesFromNow * 60 * 1000);
    const snapshotSuffix = suffix ?? (referenceValue.includes("EXPIRED") ? "2001" : referenceType === "plate" ? "1002" : "1001");

    return {
      operation: revalidationOutcome ? "revalidate" : "resolve",
      revalidationOutcome,
      parkingSessionId,
      tariffSnapshotId: `dddddddd-dddd-4ddd-8ddd-dddddddd${snapshotSuffix}`,
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
      authoritativeAmountMinorUnits: amountMinorUnits,
      netPayableMinorUnits: amountMinorUnits,
      currency: "PHP",
      tariffCalculatedAt: calculatedAt.toISOString(),
      tariffValidUntil: validUntil.toISOString(),
      tariffExpiresAt: validUntil.toISOString(),
      feeValidUntil: validUntil.toISOString(),
      vendorSystemId: this.config.vendorSystemId,
      statutoryDiscountApplied: false,
      statutoryDiscountValidationId: null,
      statutoryDiscountApplicationId: null,
      originalTariffSnapshotId: null,
      effectiveTariffSnapshotId: `dddddddd-dddd-4ddd-8ddd-dddddddd${snapshotSuffix}`,
      appliedTariffSnapshotId: null,
      policyResolutionBasis: null,
      benefitType: null,
      readinessDimensions: null,
      sessionReadiness: parkingStatus === "Active" ? "RESOLVED_PAYABLE" : "NOT_PAYABLE",
      tariffReadiness,
      paymentEligibility: paymentStatus === "Paid" ? "PAYMENT_ALREADY_FINAL" : "ELIGIBLE",
      terminalCashAvailability,
      fiscalReadiness,
      salesInvoiceConfigurationReadiness,
      cashAcceptanceReadiness: ready ? "READY" : "BLOCKED",
      readyForCashAcceptance: ready,
      blockingReasonCodes,
      retryable: classification.includes("UNAVAILABLE"),
      safeUserFacingClassification: classification,
      safeMessage: ready ? "Cash may be accepted after immediate revalidation." : "Cash acceptance is blocked by Central PMS readiness.",
      correlationId,
    };
  }
}

function failure(
  kind: Exclude<CentralPmsResult, { ok: true }>["kind"],
  errorCode: string,
  message: string,
  correlationId: string,
  retryable: boolean,
): CentralPmsResult {
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
