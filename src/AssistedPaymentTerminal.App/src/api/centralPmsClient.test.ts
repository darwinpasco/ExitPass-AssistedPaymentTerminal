import { describe, expect, it, vi } from "vitest";
import { LiveCentralPmsClient } from "./centralPmsClient";
import type { PayableBasisResponse } from "./centralPmsTypes";
import { mode1Config } from "../test/testConfig";

describe("LiveCentralPmsClient", () => {
  it("posts ticket resolves to the APT payable-basis facade with site and correlation headers", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => payableBasisPayload({ correlationId: "11111111-2222-4333-8444-555555555555" }),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.resolvePayableBasis("ticket", "APT-ACTIVE-1001", "11111111-2222-4333-8444-555555555555");

    expect(result.ok).toBe(true);
    expect(vi.mocked(fetchMock)).toHaveBeenCalledWith(
      expect.stringContaining("/v1/terminal-cash-payments/payable-basis/resolve"),
      expect.objectContaining({
        headers: expect.objectContaining({
          "X-Correlation-Id": "11111111-2222-4333-8444-555555555555",
          "X-Site-Id": "11111111-1111-1111-1111-111111111111",
        }),
        body: expect.stringContaining('"referenceType":"ticket"'),
      }),
    );
  });

  it("posts revalidation to the APT payable-basis facade without using WebPay routes", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => payableBasisPayload({ operation: "revalidate", revalidationOutcome: "PASSED_UNCHANGED", correlationId: "corr-revalidate" }),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.revalidatePayableBasis(payableBasisPayload({ correlationId: "corr-original" }), "corr-revalidate");

    expect(result.ok).toBe(true);
    expect(vi.mocked(fetchMock).mock.calls[0][0]).toContain("/v1/terminal-cash-payments/payable-basis/revalidate");
    expect(vi.mocked(fetchMock).mock.calls[0][0]).not.toContain("/v1/webpay/parking-session");
  });

  it("maps malformed success payload to contract failure", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ parkingSessionId: "missing-required-fields" }) })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.resolvePayableBasis("ticket", "APT-MALFORMED-502", "11111111-2222-4333-8444-555555555555");

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe("malformed_response");
      expect(result.error.correlationId).toBe("11111111-2222-4333-8444-555555555555");
      expect(result.error).not.toHaveProperty("details");
    }
  });
});

function payableBasisPayload(overrides: Partial<PayableBasisResponse> = {}): PayableBasisResponse {
  return {
    operation: "resolve",
    revalidationOutcome: null,
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    siteId: "11111111-1111-1111-1111-111111111111",
    sitePosServerId: "POS-DEV-001",
    terminalId: "APT-DEV-001",
    siteName: "ExitPass Demo Parking",
    ticketReference: "APT-ACTIVE-1001",
    plateNumber: "NCR-4421",
    entryTimestamp: new Date(Date.now() - 600000).toISOString(),
    parkingStatus: "Active",
    paymentStatus: "Unpaid",
    authoritativeAmountMinorUnits: 12500,
    currency: "PHP",
    tariffCalculatedAt: new Date().toISOString(),
    tariffValidUntil: new Date(Date.now() + 600000).toISOString(),
    feeValidUntil: new Date(Date.now() + 600000).toISOString(),
    vendorSystemId: "VENDOR-PMS-DEV",
    sessionReadiness: "RESOLVED_PAYABLE",
    tariffReadiness: "CURRENT",
    paymentEligibility: "ELIGIBLE",
    terminalCashAvailability: "AVAILABLE",
    fiscalReadiness: "READY",
    salesInvoiceConfigurationReadiness: "READY",
    cashAcceptanceReadiness: "READY",
    readyForCashAcceptance: true,
    blockingReasonCodes: [],
    retryable: false,
    safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
    correlationId: "11111111-2222-4333-8444-555555555555",
    ...overrides,
  };
}
