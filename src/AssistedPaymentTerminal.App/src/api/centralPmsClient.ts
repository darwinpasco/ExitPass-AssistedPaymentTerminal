import type { AptConfig } from "../config";
import type {
  CentralPmsClient,
  CentralPmsErrorResponse,
  CentralPmsFailureKind,
  CentralPmsResult,
  ResolveVendorParkingRequest,
  ResolveVendorParkingResponse,
} from "./centralPmsTypes";

export class LiveCentralPmsClient implements CentralPmsClient {
  constructor(private readonly config: AptConfig, private readonly fetchImpl: typeof fetch = fetch) {}

  async resolveTicket(ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    const request: ResolveVendorParkingRequest = {
      siteGroupId: this.config.siteGroupId,
      siteId: this.config.siteId,
      vendorSystemId: this.config.vendorSystemId,
      ticketReference,
      correlationId,
    };

    return this.postResolve(request, correlationId);
  }

  async recalculateFee(_ticketReference: string, correlationId: string): Promise<CentralPmsResult> {
    return {
      ok: false,
      kind: "recalculation_pending",
      error: {
        errorCode: "RECALCULATION_CONTRACT_PENDING",
        message: "Backend tariff recalculation integration is pending for the terminal.",
        correlationId,
        retryable: false,
      },
    };
  }

  private async postResolve(request: ResolveVendorParkingRequest, correlationId: string): Promise<CentralPmsResult> {
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 10000);

    try {
      const response = await this.fetchImpl(`${this.config.centralPmsBaseUrl.replace(/\/$/, "")}/v1/vendor-parking/resolve`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Correlation-Id": correlationId,
        },
        body: JSON.stringify(request),
        signal: controller.signal,
      });

      const payload = await response.json();
      if (!response.ok) {
        return { ok: false, kind: mapFailureKind(response.status, payload?.errorCode), error: normalizeError(payload, correlationId) };
      }

      if (!isResolveResponse(payload)) {
        return malformed(correlationId);
      }

      return { ok: true, response: payload };
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return {
          ok: false,
          kind: "timeout",
          error: {
            errorCode: "CENTRAL_PMS_TIMEOUT",
            message: "Central PMS did not respond before the terminal timeout.",
            correlationId,
            retryable: true,
          },
        };
      }

      return {
        ok: false,
        kind: "service_unavailable",
        error: {
          errorCode: "CENTRAL_PMS_UNAVAILABLE",
          message: "Central PMS is unavailable from this terminal.",
          correlationId,
          retryable: true,
        },
      };
    } finally {
      window.clearTimeout(timeout);
    }
  }
}

export function isResolveResponse(payload: unknown): payload is ResolveVendorParkingResponse {
  const candidate = payload as Partial<ResolveVendorParkingResponse> | null;
  return Boolean(
    candidate &&
      typeof candidate.parkingSessionId === "string" &&
      typeof candidate.tariffSnapshotId === "string" &&
      typeof candidate.ticketReference === "string" &&
      typeof candidate.netPayableMinorUnits === "number" &&
      typeof candidate.currency === "string" &&
      typeof candidate.tariffExpiresAt === "string" &&
      typeof candidate.paymentStatus === "string" &&
      typeof candidate.correlationId === "string",
  );
}

export function mapFailureKind(status: number, errorCode?: string): CentralPmsFailureKind {
  if (status === 404) return "not_found";
  if (status === 409 && errorCode === "AMBIGUOUS_MATCH") return "ambiguous";
  if (status === 409) return "inactive";
  if (status === 502) return "malformed_response";
  if (status === 503) return "service_unavailable";
  if (status === 400) return "invalid_request";
  return "unknown";
}

export function normalizeError(payload: unknown, correlationId: string): CentralPmsErrorResponse {
  const candidate = payload as Partial<CentralPmsErrorResponse> | null;
  return {
    errorCode: candidate?.errorCode || "CENTRAL_PMS_ERROR",
    message: candidate?.message || "Central PMS returned an error.",
    correlationId: candidate?.correlationId || correlationId,
    retryable: Boolean(candidate?.retryable),
    details: candidate?.details,
  };
}

function malformed(correlationId: string): CentralPmsResult {
  return {
    ok: false,
    kind: "malformed_response",
    error: {
      errorCode: "MALFORMED_VENDOR_RESPONSE",
      message: "Central PMS response did not match the inspected resolve contract.",
      correlationId,
      retryable: false,
    },
  };
}
