import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi, type Mock } from "vitest";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import type {
  CentralPmsClient,
  PayableBasisResponse,
  StatutoryDiscountDecisionResponse,
  StatutoryDiscountDecisionResult,
  StatutoryDiscountDecisionSubmitRequest,
  StatutoryDiscountWorkflowState,
} from "./api/centralPmsTypes";
import { buildTerminalContext } from "./terminalContext";
import { mode1Config } from "./test/testConfig";

describe("StatutoryDiscountPanel production actions", () => {
  it("submits an authenticated cashier request for review without applying the benefit", async () => {
    const submit = decisionSubmitMock(async () => ({ ok: true, response: decisionResponse() }));
    const onStateChange = vi.fn(async () => undefined);
    renderPanel({ status: "draft" }, submit, onStateChange);

    await userEvent.type(screen.getByLabelText("Statutory ID"), "AB1234567890");
    await userEvent.click(screen.getByLabelText(/residency requirement/i));
    await userEvent.click(screen.getByLabelText(/Cashier attests/i));
    await userEvent.click(screen.getByRole("button", { name: "Submit request for review" }));

    await waitFor(() => expect(submit).toHaveBeenCalledOnce());
    const [request, , idempotencyKey] = submit.mock.calls[0];
    expect(request).toEqual(expect.objectContaining({
      sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
      entitlementType: "SENIOR_CITIZEN",
      evidenceCaptureRequested: true,
      requesterAttestation: true,
      beneficiaryResidencySatisfied: true,
      applyPayableBasis: false,
    }));
    expect(request.maskedIdReference).toBe("AB******7890");
    expect(idempotencyKey).toBe("apt-statutory-decision:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001:SENIOR_CITIZEN");
    expect(onStateChange).toHaveBeenLastCalledWith(expect.objectContaining({ status: "awaiting_review" }));
  });

  it("submits a separate idempotent application intent only after approval", async () => {
    const submit = decisionSubmitMock(async () => ({
      ok: true,
      response: decisionResponse({
        payableBasisReady: true,
        payableBasisReadinessStatus: "APPLIED",
        payableBasisReadinessAction: null,
        statutoryDiscountPayableBasisApplicationCommandId: "88888888-8888-4888-8888-888888880001",
        applicationRequested: true,
        applicationCommandStatus: "APPLIED",
        applicationResultClassification: "APPLIED",
        appliedTariffSnapshotId: "99999999-9999-4999-8999-999999990001",
        statutoryDiscountAmountMinorUnits: 12500,
        netPayableAmountMinorUnits: 0,
        currency: "PHP",
        vatExclusiveBasisAmountMinorUnits: 11161,
        vatAmountMinorUnits: 1339,
        originalVatAmountMinorUnits: 1339,
        finalVatableSalesMinorUnits: 0,
        finalVatAmountMinorUnits: 0,
        discountPercentageBasisPoints: null,
        benefitType: "FULL_FEE_EXEMPTION",
        ordinanceReference: "PARANAQUE-ORDINANCE",
        vatTreatment: "VAT_EXEMPT_FULL_FEE_WAIVER",
      }),
    }));
    const onAppliedBasisReady = vi.fn(async () => undefined);
    render(
      <StatutoryDiscountPanel
        basis={basis}
        client={clientWithSubmit(submit)}
        context={buildTerminalContext(mode1Config())}
        state={{
          status: "approved_application_not_requested",
          entitlementType: "PWD",
          maskedIdReference: "PW******4321",
          idDocumentType: "PWD_ID",
          issuingAuthority: "NCDA",
          requesterAttested: true,
          beneficiaryResidencySatisfied: true,
          requestReference: "99999999-9999-4999-8999-999999990001",
          statutoryDiscountDecisionCommandId: "77777777-7777-4777-8777-777777770777",
          decisionResultStatus: "APPROVED",
        }}
        availableEntitlements={["PWD"]}
        onStateChange={vi.fn(async () => undefined)}
        onAppliedBasisReady={onAppliedBasisReady}
      />,
    );

    await userEvent.click(screen.getByRole("button", { name: "Apply approved benefit" }));

    await waitFor(() => expect(submit).toHaveBeenCalledOnce());
    const [request, , idempotencyKey] = submit.mock.calls[0];
    expect(request).toEqual(expect.objectContaining({
      entitlementType: "PWD",
      beneficiaryResidencySatisfied: true,
      applyPayableBasis: true,
    }));
    expect(idempotencyKey).toBe("apt-statutory-application:77777777-7777-4777-8777-777777770777");
    expect(onAppliedBasisReady).toHaveBeenCalledWith(
      "77777777-7777-4777-8777-777777770777",
      expect.objectContaining({ payableBasisReadinessStatus: "APPLIED" }),
      expect.objectContaining({ status: "applied", finalPayableAmountMinorUnits: 0 }),
    );
  });

  it("does not expose benefit application for a rejected decision", () => {
    renderPanel(
      { status: "rejected", decisionResultStatus: "REJECTED" },
      decisionSubmitMock(async () => ({ ok: true, response: decisionResponse() })),
      vi.fn(),
    );

    expect(screen.getAllByText("Rejected")).not.toHaveLength(0);
    expect(screen.queryByRole("button", { name: "Apply approved benefit" })).not.toBeInTheDocument();
  });
});

function renderPanel(
  state: StatutoryDiscountWorkflowState,
  submit: DecisionSubmitMock,
  onStateChange: ReturnType<typeof vi.fn>,
) {
  render(
    <StatutoryDiscountPanel
      basis={basis}
      client={clientWithSubmit(submit)}
      context={buildTerminalContext(mode1Config())}
      state={state}
      availableEntitlements={["SENIOR_CITIZEN", "PWD"]}
      onStateChange={onStateChange}
      onAppliedBasisReady={vi.fn(async () => undefined)}
    />,
  );
}

function clientWithSubmit(submit: DecisionSubmitMock): CentralPmsClient {
  return {
    resolvePayableBasis: vi.fn(),
    revalidatePayableBasis: vi.fn(),
    submitStatutoryDiscountDecision: submit,
    getStatutoryDiscountDecision: vi.fn(),
  } as unknown as CentralPmsClient;
}

type DecisionSubmit = (
  request: StatutoryDiscountDecisionSubmitRequest,
  correlationId: string,
  idempotencyKey: string,
) => Promise<StatutoryDiscountDecisionResult>;
type DecisionSubmitMock = Mock<DecisionSubmit>;

function decisionSubmitMock(implementation: DecisionSubmit): DecisionSubmitMock {
  return vi.fn<DecisionSubmit>(implementation);
}

const basis: PayableBasisResponse = {
  parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
  siteGroupId: "22222222-2222-2222-8222-222222222222",
  siteId: "11111111-1111-1111-8111-111111111111",
  terminalId: "APT-DEV-001",
  ticketReference: "APT-ACTIVE-1001",
  plateNumber: "NCR-4421",
  parkingStatus: "Active",
  paymentStatus: "Unpaid",
  authoritativeAmountMinorUnits: 12500,
  currency: "PHP",
  tariffValidUntil: "2026-11-09T10:00:00Z",
  readyForCashAcceptance: true,
  blockingReasonCodes: [],
  retryable: false,
  safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
  correlationId: "basis-correlation",
};

function decisionResponse(overrides: Partial<StatutoryDiscountDecisionResponse> = {}): StatutoryDiscountDecisionResponse {
  return {
    statutoryDiscountDecisionCommandId: "77777777-7777-4777-8777-777777770777",
    requestReference: "99999999-9999-4999-8999-999999990001",
    parkingSessionId: basis.parkingSessionId,
    sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
    entitlementType: "SENIOR_CITIZEN",
    decisionStatus: "AWAITING_REVIEW",
    localOrdinanceApplied: false,
    evidenceRequired: true,
    evidenceRecorded: false,
    correlationId: "decision-correlation",
    createdAt: "2026-11-09T00:00:00Z",
    originalTariffSnapshotId: basis.tariffSnapshotId,
    commandStatus: "AWAITING_REVIEW",
    clientResultStatus: "ACCEPTED",
    resultClassification: "AWAITING_REVIEW",
    semanticHashSourceVersion: "statutory-discount-decision-v1",
    retryable: false,
    recoveryClassification: "NONE",
    recoveryAction: "POLL_READBACK",
    decisionCommandStatus: "AWAITING_REVIEW",
    decisionResultStatus: "NOT_DECIDED",
    decisionRetryable: false,
    decisionRecoveryClassification: "NONE",
    applicationRequested: false,
    applicationCommandStatus: "NOT_REQUESTED",
    applicationResultClassification: "NOT_REQUESTED",
    applicationRetryable: false,
    applicationRecoveryClassification: "NONE",
    overallResultClassification: "NOT_READY",
    oneShotComplete: false,
    siteId: basis.siteId,
    siteGroupId: basis.siteGroupId,
    payableBasisReady: false,
    payableBasisReadinessStatus: "AWAITING_REVIEW",
    payableBasisReadinessAction: "POLL_READBACK",
    ...overrides,
  };
}
