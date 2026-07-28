import type { AptConfig } from "../config";
import type {
  CentralPmsClient,
  CentralPmsErrorResponse,
  CentralPmsFailureKind,
  CentralPmsResult,
  PayableBasisReferenceType,
  PayableBasisResponse,
  RevalidatePayableBasisRequest,
  ResolvePayableBasisRequest,
  StatutoryDiscountDecisionResponse,
  StatutoryDiscountDecisionResult,
  StatutoryDiscountDecisionSubmitRequest,
} from "./centralPmsTypes";

export class LiveCentralPmsClient implements CentralPmsClient {
  constructor(private readonly config: AptConfig, private readonly fetchImpl: typeof fetch = fetch) {}

  async resolvePayableBasis(
    referenceType: PayableBasisReferenceType,
    referenceValue: string,
    correlationId: string,
    statutoryDiscountDecisionCommandId?: string | null,
  ): Promise<CentralPmsResult> {
    const trimmed = referenceValue.trim();
    const request: ResolvePayableBasisRequest = {
      siteGroupId: this.config.siteGroupId,
      siteId: this.config.siteId,
      sitePosServerId: this.config.posServerId,
      terminalId: this.config.terminalId,
      vendorSystemId: this.config.vendorSystemId,
      referenceType,
      ...(referenceType === "ticket" ? { ticketReference: trimmed } : { plateNumber: trimmed }),
      statutoryDiscountDecisionCommandId: statutoryDiscountDecisionCommandId ?? undefined,
      correlationId,
    };

    return this.postPayableBasis("/v1/terminal-cash-payments/payable-basis/resolve", request, correlationId);
  }

  async resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async recalculateFee(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return this.resolvePayableBasis("ticket", ticketReference, correlationId);
  }

  async revalidatePayableBasis(displayedBasis: PayableBasisResponse, correlationId: string): Promise<CentralPmsResult> {
    const request: RevalidatePayableBasisRequest = {
      parkingSessionId: displayedBasis.parkingSessionId,
      tariffSnapshotId: displayedBasis.tariffSnapshotId,
      siteGroupId: displayedBasis.siteGroupId,
      siteId: displayedBasis.siteId,
      sitePosServerId: displayedBasis.sitePosServerId ?? this.config.posServerId,
      terminalId: displayedBasis.terminalId ?? this.config.terminalId,
      vendorSystemId: displayedBasis.vendorSystemId ?? this.config.vendorSystemId,
      ticketReference: displayedBasis.ticketReference,
      plateNumber: displayedBasis.plateNumber,
      expectedAmountMinorUnits: displayedBasis.authoritativeAmountMinorUnits,
      expectedCurrency: displayedBasis.currency,
      statutoryDiscountDecisionCommandId: displayedBasis.statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId ?? undefined,
      correlationId,
    };

    return this.postPayableBasis("/v1/terminal-cash-payments/payable-basis/revalidate", request, correlationId);
  }

  async submitStatutoryDiscountDecision(
    request: StatutoryDiscountDecisionSubmitRequest,
    correlationId: string,
    idempotencyKey: string,
  ): Promise<StatutoryDiscountDecisionResult> {
    return this.requestStatutoryDiscountDecision("POST", "/v1/statutory-discounts/decisions", correlationId, idempotencyKey, request);
  }

  async getStatutoryDiscountDecision(decisionCommandId: string, correlationId: string): Promise<StatutoryDiscountDecisionResult> {
    return this.requestStatutoryDiscountDecision("GET", `/v1/statutory-discounts/decisions/${encodeURIComponent(decisionCommandId)}`, correlationId);
  }

  private async postPayableBasis(path: string, request: unknown, correlationId: string): Promise<CentralPmsResult> {
    const result = await this.send(path, "POST", correlationId, undefined, request);
    if (!result.ok) {
      return result;
    }

    if (!isPayableBasisResponse(result.payload)) {
      return malformed(correlationId);
    }

    return { ok: true, response: normalizePayableBasisResponse(result.payload) };
  }

  private async requestStatutoryDiscountDecision(
    method: "GET" | "POST",
    path: string,
    correlationId: string,
    idempotencyKey?: string,
    request?: unknown,
  ): Promise<StatutoryDiscountDecisionResult> {
    const result = await this.send(path, method, correlationId, idempotencyKey, request);
    if (!result.ok) {
      return result;
    }

    if (!isStatutoryDiscountDecisionResponse(result.payload)) {
      return {
        ok: false,
        kind: "malformed_response",
        error: {
          errorCode: "MALFORMED_STATUTORY_DISCOUNT_RESPONSE",
          message: "Central PMS statutory-discount response did not match the APT contract.",
          correlationId,
          retryable: false,
        },
      };
    }

    return { ok: true, response: normalizeStatutoryDiscountDecisionResponse(result.payload) };
  }

  private async send(
    path: string,
    method: "GET" | "POST",
    correlationId: string,
    idempotencyKey?: string,
    request?: unknown,
  ): Promise<{ ok: true; payload: Record<string, unknown> } | Exclude<CentralPmsResult, { ok: true }>> {
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 10000);

    try {
      const headers: Record<string, string> = {
        "X-Correlation-Id": correlationId,
        "X-Site-Id": this.config.siteId,
      };
      if (method === "POST") {
        headers["Content-Type"] = "application/json";
      }
      if (idempotencyKey) {
        headers["Idempotency-Key"] = idempotencyKey;
      }

      const response = await this.fetchImpl(`${this.config.centralPmsBaseUrl.replace(/\/$/, "")}${path}`, {
        method,
        headers,
        body: method === "POST" ? JSON.stringify(request) : undefined,
        signal: controller.signal,
      });

      const payload = await readJson(response);
      if (!response.ok) {
        return { ok: false, kind: mapFailureKind(response.status, typeof payload?.errorCode === "string" ? payload.errorCode : undefined), error: normalizeError(payload, correlationId) };
      }

      return { ok: true, payload: payload ?? {} };
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return failure("timeout", "CENTRAL_PMS_TIMEOUT", "Central PMS did not respond before the terminal timeout.", correlationId, true);
      }

      return failure("service_unavailable", "CENTRAL_PMS_UNAVAILABLE", "Central PMS is unavailable from this terminal.", correlationId, true);
    } finally {
      window.clearTimeout(timeout);
    }
  }
}

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  try {
    return (await response.json()) as Record<string, unknown>;
  } catch {
    return null;
  }
}

export function isPayableBasisResponse(payload: unknown): payload is PayableBasisResponse {
  const candidate = payload as Partial<PayableBasisResponse> | null;
  return Boolean(
    candidate &&
      typeof candidate.parkingSessionId === "string" &&
      typeof candidate.tariffSnapshotId === "string" &&
      typeof candidate.siteGroupId === "string" &&
      typeof candidate.siteId === "string" &&
      typeof candidate.parkingStatus === "string" &&
      typeof candidate.paymentStatus === "string" &&
      typeof candidate.authoritativeAmountMinorUnits === "number" &&
      typeof candidate.currency === "string" &&
      typeof candidate.tariffValidUntil === "string" &&
      typeof candidate.readyForCashAcceptance === "boolean" &&
      Array.isArray(candidate.blockingReasonCodes) &&
      typeof candidate.retryable === "boolean" &&
      typeof candidate.safeUserFacingClassification === "string" &&
      typeof candidate.correlationId === "string",
  );
}

export const isResolveResponse = isPayableBasisResponse;

export function normalizePayableBasisResponse(payload: PayableBasisResponse): PayableBasisResponse {
  return {
    ...payload,
    blockingReasonCodes: payload.blockingReasonCodes ?? [],
    safeUserFacingClassification: payload.safeUserFacingClassification || "UNKNOWN",
    retryable: Boolean(payload.retryable),
    readyForCashAcceptance: Boolean(payload.readyForCashAcceptance),
    statutoryDiscountReadiness: payload.statutoryDiscountReadiness
      ? {
          ...payload.statutoryDiscountReadiness,
          applicable: Boolean(payload.statutoryDiscountReadiness.applicable),
          ready: Boolean(payload.statutoryDiscountReadiness.ready),
          payableBasisReady: Boolean(payload.statutoryDiscountReadiness.payableBasisReady),
          retryable: Boolean(payload.statutoryDiscountReadiness.retryable),
        }
      : null,
  };
}

export function isStatutoryDiscountDecisionResponse(payload: unknown): payload is StatutoryDiscountDecisionResponse {
  const candidate = payload as Partial<StatutoryDiscountDecisionResponse> | null;
  return Boolean(
    candidate &&
      typeof candidate.statutoryDiscountDecisionCommandId === "string" &&
      typeof candidate.requestReference === "string" &&
      typeof candidate.parkingSessionId === "string" &&
      typeof candidate.sourceChannel === "string" &&
      typeof candidate.entitlementType === "string" &&
      typeof candidate.decisionStatus === "string" &&
      typeof candidate.commandStatus === "string" &&
      typeof candidate.clientResultStatus === "string" &&
      typeof candidate.resultClassification === "string" &&
      typeof candidate.retryable === "boolean" &&
      typeof candidate.recoveryClassification === "string" &&
      typeof candidate.decisionCommandStatus === "string" &&
      typeof candidate.applicationCommandStatus === "string" &&
      typeof candidate.applicationResultClassification === "string" &&
      typeof candidate.payableBasisReady === "boolean" &&
      typeof candidate.payableBasisReadinessStatus === "string" &&
      typeof candidate.correlationId === "string",
  );
}

export function normalizeStatutoryDiscountDecisionResponse(payload: StatutoryDiscountDecisionResponse): StatutoryDiscountDecisionResponse {
  return {
    ...payload,
    retryable: Boolean(payload.retryable),
    decisionRetryable: Boolean(payload.decisionRetryable),
    applicationRetryable: Boolean(payload.applicationRetryable),
    applicationRequested: Boolean(payload.applicationRequested),
    oneShotComplete: Boolean(payload.oneShotComplete),
    payableBasisReady: Boolean(payload.payableBasisReady),
  };
}

export function mapFailureKind(status: number, errorCode?: string): CentralPmsFailureKind {
  if (status === 401 || status === 403 || errorCode === "FORBIDDEN_SITE") return "unauthorized";
  if (status === 404 || errorCode === "SESSION_NOT_FOUND") return "not_found";
  if (errorCode === "VENDOR_SESSION_AMBIGUOUS") return "ambiguous";
  if (errorCode === "PAYMENT_ALREADY_FINAL") return "already_paid";
  if (errorCode === "SESSION_NOT_PAYABLE") return "inactive";
  if (errorCode === "STALE_TARIFF") return "tariff_expired";
  if (errorCode === "CASH_PAYMENT_RAIL_NOT_CONFIGURED") return "cash_unavailable";
  if (errorCode === "SITE_POS_SERVER_NOT_CONFIGURED" || errorCode === "SALES_INVOICE_CONFIGURATION_NOT_READY" || errorCode === "FISCAL_PATH_UNAVAILABLE") return "fiscal_unavailable";
  if (errorCode === "AMOUNT_CHANGED" || errorCode === "PAYABLE_BASIS_MISMATCH") return "amount_changed";
  if (errorCode?.startsWith("STATUTORY_DISCOUNT_")) return "statutory_blocked";
  if (status === 409) return "inactive";
  if (status === 502 || errorCode === "MALFORMED_VENDOR_RESPONSE") return "malformed_response";
  if (status === 503 || errorCode === "VENDOR_PMS_UNAVAILABLE") return "service_unavailable";
  if (status === 400 || errorCode === "INVALID_REQUEST") return "invalid_request";
  return "unknown";
}

export function normalizeError(payload: unknown, correlationId: string): CentralPmsErrorResponse {
  const candidate = payload as Partial<CentralPmsErrorResponse> | null;
  return {
    errorCode: typeof candidate?.errorCode === "string" ? candidate.errorCode : "CENTRAL_PMS_ERROR",
    message: typeof candidate?.message === "string" ? candidate.message : "Central PMS returned a safe error.",
    correlationId: typeof candidate?.correlationId === "string" ? candidate.correlationId : correlationId,
    retryable: Boolean(candidate?.retryable),
  };
}

function malformed(correlationId: string): CentralPmsResult {
  return failure(
    "malformed_response",
    "MALFORMED_PAYABLE_BASIS_RESPONSE",
    "Central PMS response did not match the APT payable-basis readiness contract.",
    correlationId,
    false,
  );
}

function failure(
  kind: CentralPmsFailureKind,
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
