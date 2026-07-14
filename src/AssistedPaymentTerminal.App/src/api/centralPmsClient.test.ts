import { describe, expect, it, vi } from "vitest";
import { LiveCentralPmsClient } from "./centralPmsClient";
import { mode1Config } from "../test/testConfig";

describe("LiveCentralPmsClient", () => {
  it("propagates X-Correlation-Id when resolving a ticket", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => ({
        parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
        tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
        siteGroupId: "22222222-2222-2222-2222-222222222222",
        siteId: "11111111-1111-1111-1111-111111111111",
        lookupOutcome: "resolved",
        plateNumber: "NCR-4421",
        ticketReference: "APT-ACTIVE-1001",
        netPayableMinorUnits: 12500,
        currency: "PHP",
        tariffExpiresAt: new Date(Date.now() + 600000).toISOString(),
        feeValidUntil: new Date(Date.now() + 600000).toISOString(),
        parkingStatus: "Active",
        paymentStatus: "Not Started",
        statutoryDiscountApplied: false,
        vendorSystemId: "VENDOR-PMS-DEV",
        correlationId: "11111111-2222-4333-8444-555555555555",
      }),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.resolveTicket("APT-ACTIVE-1001", "11111111-2222-4333-8444-555555555555");

    expect(result.ok).toBe(true);
    expect(vi.mocked(fetchMock)).toHaveBeenCalledWith(
      expect.stringContaining("/v1/vendor-parking/resolve"),
      expect.objectContaining({
        headers: expect.objectContaining({ "X-Correlation-Id": "11111111-2222-4333-8444-555555555555" }),
      }),
    );
  });

  it("maps malformed success payload to contract failure", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => ({ parkingSessionId: "missing-required-fields" }),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.resolveTicket("APT-MALFORMED-502", "11111111-2222-4333-8444-555555555555");

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe("malformed_response");
      expect(result.error.correlationId).toBe("11111111-2222-4333-8444-555555555555");
    }
  });
});
