import { describe, expect, it, vi } from "vitest";
import { LiveCentralPmsClient } from "./centralPmsClient";
import type { PayableBasisResponse, StatutoryOrdinanceAvailabilityResponse } from "./centralPmsTypes";
import { mode1Config } from "../test/testConfig";
import { maskStatutoryId } from "../statutoryIdMasking";

describe("LiveCentralPmsClient", () => {
  it("uses the host-authorized bridge for payable-basis requests inside WebView2", async () => {
    let listener: ((event: { data: unknown }) => void) | undefined;
    const fetchMock = vi.fn() as unknown as typeof fetch;
    window.chrome = {
      webview: {
        postMessage: (message) => {
          const request = JSON.parse(message) as { command: string; correlationId: string };
          listener?.({
            data: JSON.stringify({
              ok: true,
              command: request.command,
              correlationId: request.correlationId,
              payload: { statusCode: 200, body: payableBasisPayload({ correlationId: request.correlationId }) },
            }),
          });
        },
        addEventListener: (_type, callback) => { listener = callback; },
        removeEventListener: vi.fn(),
      },
    };

    try {
      const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
      const result = await client.resolvePayableBasis("plate", "NO-SESSION", "11111111-2222-4333-8444-555555555555");

      expect(result.ok).toBe(true);
      expect(fetchMock).not.toHaveBeenCalled();
    } finally {
      delete window.chrome;
    }
  });

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

  it("resolves APT ordinance availability using one authoritative parking-session lookup and no privileged UI headers", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ordinanceAvailabilityPayload() })) as unknown as typeof fetch;
    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);

    const result = await client.resolveStatutoryOrdinanceAvailability!(payableBasisPayload(), "SENIOR_CITIZEN", "ordinance-resolve-corr");

    expect(result.ok).toBe(true);
    const [url, init] = vi.mocked(fetchMock).mock.calls[0];
    expect(url).toContain("/v1/apt/statutory-discounts/ordinance-availability/resolve");
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    expect(body).toEqual(expect.objectContaining({
      parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
      siteId: "11111111-1111-1111-1111-111111111111",
      siteGroupId: "22222222-2222-2222-2222-222222222222",
      entitlementType: "SENIOR_CITIZEN",
    }));
    expect(body).not.toHaveProperty("ticketReference");
    expect(body).not.toHaveProperty("plateNumber");
    expect(init?.headers).not.toHaveProperty("Authorization");
    expect(init?.headers).not.toHaveProperty("X-ExitPass-Permissions");
    expect(init?.headers).not.toHaveProperty("X-ExitPass-Service-Identity-Id");
  });

  it("uses the dedicated immediate ordinance revalidation route and preserves canonical pass fields", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ordinanceAvailabilityPayload({ operation: "REVALIDATE", revalidationOutcome: "PASSED_UNCHANGED", preCashRevalidationPassed: true, readyForStatutoryCashFlow: true }) })) as unknown as typeof fetch;
    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);

    const result = await client.revalidateStatutoryOrdinanceAvailability!(payableBasisPayload(), "PWD", "ordinance-revalidate-corr");

    expect(result).toEqual(expect.objectContaining({ ok: true }));
    if (result.ok) {
      expect(result.response.revalidationOutcome).toBe("PASSED_UNCHANGED");
      expect(result.response.readyForStatutoryCashFlow).toBe(true);
    }
    expect(vi.mocked(fetchMock).mock.calls[0][0]).toContain("/v1/apt/statutory-discounts/ordinance-availability/revalidate");
  });

  it("fails closed on malformed ordinance availability without exposing the raw payload", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ classification: "AVAILABLE", internalPolicyId: "must-not-escape" }) })) as unknown as typeof fetch;
    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);

    const result = await client.resolveStatutoryOrdinanceAvailability!(payableBasisPayload(), "PWD", "ordinance-malformed-corr");

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe("malformed_response");
      expect(JSON.stringify(result.error)).not.toContain("internalPolicyId");
      expect(JSON.stringify(result.error)).not.toContain("must-not-escape");
    }
  });

  it("fails closed on contradictory AVAILABLE flags instead of enabling the statutory path", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => ordinanceAvailabilityPayload({ ordinanceCoverageAvailable: false }),
    })) as unknown as typeof fetch;
    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);

    const result = await client.resolveStatutoryOrdinanceAvailability!(payableBasisPayload(), "PWD", "ordinance-contradictory-corr");

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe("malformed_response");
      expect(result.error.retryable).toBe(false);
    }
  });


  it("includes statutory decision anchor when resolving and revalidating an applied statutory basis", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => payableBasisPayload({ correlationId: "stat-corr" }),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    await client.resolvePayableBasis("ticket", "APT-ACTIVE-1001", "stat-corr", "77777777-7777-4777-8777-777777770777");
    await client.revalidatePayableBasis(payableBasisPayload({
      statutoryDiscountReadiness: {
        applicable: true,
        ready: true,
        statutoryDiscountDecisionCommandId: "77777777-7777-4777-8777-777777770777",
        payableBasisReady: true,
        payableBasisReadinessStatus: "APPLIED",
        retryable: false,
        message: "Applied",
      },
    }), "stat-revalidate");

    expect(vi.mocked(fetchMock).mock.calls[0][1]?.body).toContain('"statutoryDiscountDecisionCommandId":"77777777-7777-4777-8777-777777770777"');
    expect(vi.mocked(fetchMock).mock.calls[1][1]?.body).toContain('"statutoryDiscountDecisionCommandId":"77777777-7777-4777-8777-777777770777"');
    expect(vi.mocked(fetchMock).mock.calls[0][0]).not.toContain("/v1/webpay/parking-session");
  });

  it("submits statutory decisions through the shared route without reviewer or calculated fields", async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => statutoryDecisionPayload(),
    })) as unknown as typeof fetch;

    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);
    const result = await client.submitStatutoryDiscountDecision!({
      requestReference: "99999999-9999-4999-8999-999999990001",
      sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
      parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
      siteId: "11111111-1111-1111-1111-111111111111",
      siteGroupId: "22222222-2222-2222-2222-222222222222",
      ticketReference: "APT-ACTIVE-1001",
      plateNumber: "NCR-4421",
      entitlementType: "SENIOR_CITIZEN",
      idDocumentType: "OSCA_ID",
      issuingAuthority: "OSCA",
      expiryDate: null,
      maskedIdReference: maskStatutoryId("AB1234567890"),
      evidenceCaptureRequested: false,
      evidenceReferences: null,
      requesterAttestation: true,
      attestationNotes: null,
      reasonCode: null,
      applyPayableBasis: false,
      originalTariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    }, "stat-submit-corr", "idem-stat-submit");

    expect(result.ok).toBe(true);
    expect(vi.mocked(fetchMock).mock.calls[0][0]).toContain("/v1/statutory-discounts/decisions");
    expect(vi.mocked(fetchMock).mock.calls[0][1]).toEqual(expect.objectContaining({ method: "POST" }));
    expect(vi.mocked(fetchMock).mock.calls[0][1]?.headers).toEqual(expect.objectContaining({
      "Idempotency-Key": "idem-stat-submit",
      "X-Correlation-Id": "stat-submit-corr",
      "X-Site-Id": "11111111-1111-1111-1111-111111111111",
    }));
    const body = String(vi.mocked(fetchMock).mock.calls[0][1]?.body);
    expect(body).toContain('"applyPayableBasis":false');
    expect(body).toContain('"maskedIdReference":"AB******7890"');
    expect(body).not.toContain("reviewerUserId");
    expect(body).not.toContain("reviewerAttestation");
    expect(body).not.toContain("statutoryDiscountAmountMinorUnits");
    expect(body).not.toContain("vatAmountMinorUnits");
  });

  it("reads statutory decision status with GET only", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => statutoryDecisionPayload({ payableBasisReadinessStatus: "AWAITING_REVIEW" }) })) as unknown as typeof fetch;
    const client = new LiveCentralPmsClient({ ...mode1Config(), centralPmsConnectionMode: "live" }, fetchMock);

    const result = await client.getStatutoryDiscountDecision!("77777777-7777-4777-8777-777777770777", "stat-get-corr");

    expect(result.ok).toBe(true);
    expect(vi.mocked(fetchMock).mock.calls[0][0]).toContain("/v1/statutory-discounts/decisions/77777777-7777-4777-8777-777777770777");
    expect(vi.mocked(fetchMock).mock.calls[0][1]).toEqual(expect.objectContaining({ method: "GET", body: undefined }));
  });  it("maps malformed success payload to contract failure", async () => {
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

function statutoryDecisionPayload(overrides: Partial<import("./centralPmsTypes").StatutoryDiscountDecisionResponse> = {}): import("./centralPmsTypes").StatutoryDiscountDecisionResponse {
  return {
    statutoryDiscountDecisionCommandId: "77777777-7777-4777-8777-777777770777",
    requestReference: "99999999-9999-4999-8999-999999990001",
    statutoryDiscountValidationId: "66666666-6666-4666-8666-666666660001",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
    entitlementType: "SENIOR_CITIZEN",
    decisionStatus: "AWAITING_REVIEW",
    policyResolutionBasis: null,
    appliedPolicyReferenceId: null,
    fallbackPolicyReferenceId: null,
    localOrdinanceApplied: false,
    grossAmountMinorUnits: 12500,
    statutoryDiscountAmountMinorUnits: null,
    netPayableAmountMinorUnits: null,
    currency: null,
    evidenceRequired: false,
    evidenceRecorded: false,
    reasonCode: null,
    errorCode: null,
    correlationId: "stat-corr",
    createdAt: new Date().toISOString(),
    decidedAt: null,
    appliedAt: null,
    originalTariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    appliedTariffSnapshotId: null,
    commandStatus: "AWAITING_REVIEW",
    clientResultStatus: "ACCEPTED",
    resultClassification: "AWAITING_REVIEW",
    semanticHashSourceVersion: "statutory-discount-decision-v1",
    retryable: false,
    recoveryClassification: "NONE",
    recoveryAction: "POLL_READBACK",
    safeErrorCode: null,
    decisionCommandStatus: "AWAITING_REVIEW",
    decisionResultStatus: "NOT_DECIDED",
    decisionRetryable: false,
    decisionRecoveryClassification: "NONE",
    decisionRecoveryAction: null,
    statutoryDiscountPayableBasisApplicationCommandId: null,
    applicationRequested: false,
    applicationCommandStatus: "NOT_REQUESTED",
    applicationResultClassification: "NOT_REQUESTED",
    applicationSemanticHashSourceVersion: null,
    applicationRetryable: false,
    applicationRecoveryClassification: "NONE",
    applicationRecoveryAction: null,
    overallResultClassification: "NOT_READY",
    oneShotComplete: false,
    siteId: "11111111-1111-1111-1111-111111111111",
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    vatExclusiveBasisAmountMinorUnits: null,
    vatAmountMinorUnits: null,
    vatTreatment: null,
    payableBasisReady: false,
    payableBasisReadinessStatus: "AWAITING_REVIEW",
    payableBasisReadinessAction: "POLL_READBACK",
    ...overrides,
  };
}

function ordinanceAvailabilityPayload(overrides: Partial<StatutoryOrdinanceAvailabilityResponse> = {}): StatutoryOrdinanceAvailabilityResponse {
  return {
    operation: "RESOLVE",
    revalidationOutcome: null,
    classification: "AVAILABLE",
    entitlementType: "PWD",
    ordinanceCoverageAvailable: true,
    statutoryRequestAllowed: true,
    preCashRevalidationPassed: false,
    readyForStatutoryCashFlow: true,
    ordinaryPaymentPreserved: true,
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    siteId: "11111111-1111-1111-1111-111111111111",
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    resolvedScopeType: "SITE",
    coverageClassification: "ACTIVE_COVERED",
    policyStatusClassification: "ACTIVE",
    effectiveFrom: "2026-01-01T00:00:00Z",
    effectiveTo: null,
    authorityClassification: "CENTRAL_PMS_STATUTORY_POLICY",
    jurisdictionDisplayName: "Synthetic locality",
    supportReference: "ordinance-support-reference",
    correlationId: "ordinance-correlation",
    evaluatedAt: "2026-08-03T00:00:00Z",
    authoritativeUpdatedAt: "2026-08-01T00:00:00Z",
    retryable: false,
    safeMessage: "Coverage is available; customer entitlement still requires review.",
    ...overrides,
  };
}
