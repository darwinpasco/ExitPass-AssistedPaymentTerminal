import type { AptConfig } from "../config";
import type { CentralPmsClient, CentralPmsResult, ResolveVendorParkingResponse } from "./centralPmsTypes";

const ids = {
  activeSession: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  expiredSession: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb2001",
  inactiveSession: "cccccccc-cccc-4ccc-8ccc-cccccccc3001",
};

export class MockCentralPmsClient implements CentralPmsClient {
  constructor(private readonly config: AptConfig) {}

  async resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolveScenario(ticketReference.trim().toUpperCase(), correlationId, false);
  }

  async recalculateFee(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolveScenario(ticketReference.trim().toUpperCase(), correlationId, true);
  }

  private async resolveScenario(ticketReference: string, correlationId: string, recalculation: boolean): Promise<CentralPmsResult> {
    await delay(120);

    if (ticketReference === "APT-NOTFOUND-404") {
      return failure("not_found", "SESSION_NOT_FOUND", "Vendor parking session was not found.", correlationId, false);
    }

    if (ticketReference === "APT-INACTIVE-3001") {
      return failure("inactive", "VENDOR_REJECTED", "Vendor parking lookup was rejected because the session is inactive.", correlationId, false);
    }

    if (ticketReference === "APT-AMBIG-409") {
      return failure("ambiguous", "AMBIGUOUS_MATCH", "Vendor parking lookup returned multiple matching sessions.", correlationId, false);
    }

    if (ticketReference === "APT-UNAVAILABLE-503") {
      return failure("service_unavailable", "RETRYABLE_UNAVAILABLE", "Vendor parking resolution is temporarily unavailable.", correlationId, true);
    }

    if (ticketReference === "APT-MALFORMED-502") {
      return failure("malformed_response", "MALFORMED_VENDOR_RESPONSE", "Vendor parking response was malformed.", correlationId, false);
    }

    if (ticketReference === "APT-RECALC-FAIL" && recalculation) {
      return failure("service_unavailable", "RECALCULATION_UNAVAILABLE", "Mock recalculation service is temporarily unavailable.", correlationId, true);
    }

    if (ticketReference === "APT-EXPIRED-2001" && !recalculation) {
      return { ok: true, response: this.response(ticketReference, correlationId, ids.expiredSession, -5, 18750, "ABC-2198") };
    }

    if (ticketReference === "APT-EXPIRED-2001" && recalculation) {
      return { ok: true, response: this.response(ticketReference, correlationId, ids.expiredSession, 15, 20500, "ABC-2198", true) };
    }

    if (ticketReference === "APT-RECALC-FAIL") {
      return { ok: true, response: this.response(ticketReference, correlationId, ids.expiredSession, -10, 14200, "RCL-FAIL") };
    }

    return { ok: true, response: this.response(ticketReference || "APT-ACTIVE-1001", correlationId, ids.activeSession, 18, 12500, "NCR-4421") };
  }

  private response(
    ticketReference: string,
    correlationId: string,
    parkingSessionId: string,
    expiryMinutesFromNow: number,
    netPayableMinorUnits: number,
    plateNumber: string,
    recalculated = false,
  ): ResolveVendorParkingResponse {
    const now = new Date();
    const entryTime = new Date(now.getTime() - 2 * 60 * 60 * 1000);
    const calculatedAt = new Date(now.getTime() - (recalculated ? 0 : 2) * 60 * 1000);
    const expiry = new Date(now.getTime() + expiryMinutesFromNow * 60 * 1000);
    const suffix = recalculated ? "9002" : ticketReference.includes("EXPIRED") ? "2001" : "1001";

    return {
      parkingSessionId,
      tariffSnapshotId: `dddddddd-dddd-4ddd-8ddd-dddddddd${suffix}`,
      siteGroupId: this.config.siteGroupId,
      siteId: this.config.siteId,
      siteGroupName: "ExitPass Development Group",
      siteName: this.config.siteName,
      lookupOutcome: "resolved",
      plateNumber,
      ticketReference,
      entryTime: entryTime.toISOString(),
      currentFeeCalculationTime: calculatedAt.toISOString(),
      netPayableMinorUnits,
      currency: "PHP",
      tariffExpiresAt: expiry.toISOString(),
      feeValidUntil: expiry.toISOString(),
      parkingStatus: "Active",
      paymentStatus: "Not Started",
      statutoryDiscountApplied: false,
      statutoryDiscountValidationId: null,
      statutoryDiscountApplicationId: null,
      originalTariffSnapshotId: null,
      effectiveTariffSnapshotId: `dddddddd-dddd-4ddd-8ddd-dddddddd${suffix}`,
      appliedTariffSnapshotId: null,
      policyResolutionBasis: null,
      benefitType: null,
      vendorSystemId: this.config.vendorSystemId,
      correlationId,
    };
  }
}

function failure(
  kind: CentralPmsResult extends infer _ ? Exclude<CentralPmsResult, { ok: true }>["kind"] : never,
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
