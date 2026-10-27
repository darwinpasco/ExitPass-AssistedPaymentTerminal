import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import type { CentralPmsClient, PayableBasisResponse, StatutoryDiscountWorkflowState } from "./api/centralPmsTypes";
import { buildTerminalContext } from "./terminalContext";
import { mode1Config } from "./test/testConfig";

describe("statutory parking fee calculation presentation", () => {
  it("renders Central PMS original and final VAT facts without recomputing them", () => {
    renderPanel({
      status: "applied",
      appliedTariffSnapshotId: "99999999-9999-4999-8999-999999999999",
      originalAmountMinorUnits: 11200,
      originalVatAmountMinorUnits: 1200,
      vatExclusiveBasisAmountMinorUnits: 10000,
      statutoryDiscountAmountMinorUnits: 2000,
      finalVatableSalesMinorUnits: 8000,
      finalVatAmountMinorUnits: 960,
      finalPayableAmountMinorUnits: 8960,
      discountPercentageBasisPoints: 2000,
      ordinanceReference: "Approved LGU Ordinance",
      vatTreatment: "VAT_INCLUSIVE_NO_EXEMPTION",
      currency: "PHP",
      payableBasisReady: true,
      payableBasisReadinessStatus: "READY",
    });

    const calculation = screen.getByTestId("statutory-applied-facts");
    expect(calculation).toHaveTextContent("Original parking fee (VAT-inclusive)₱112.00");
    expect(calculation).toHaveTextContent("Original VAT component (12%)₱12.00");
    expect(calculation).toHaveTextContent("Original VAT-exclusive parking fee₱100.00");
    expect(calculation).toHaveTextContent("LGU ordinance discount (20%)-₱20.00");
    expect(calculation).toHaveTextContent("Final VATable sales₱80.00");
    expect(calculation).toHaveTextContent("Final applicable VAT (12%)₱9.60");
    expect(calculation).toHaveTextContent("Total amount payable₱89.60");
    expect(calculation).toHaveTextContent("Vat Inclusive No Exemption");
  });

  it("presents a full waiver without fabricating a percentage", () => {
    renderPanel({
      status: "applied",
      appliedTariffSnapshotId: "99999999-9999-4999-8999-999999999999",
      originalAmountMinorUnits: 10000,
      originalVatAmountMinorUnits: 1071,
      vatExclusiveBasisAmountMinorUnits: 8929,
      statutoryDiscountAmountMinorUnits: 8929,
      finalVatableSalesMinorUnits: 0,
      finalVatAmountMinorUnits: 0,
      finalPayableAmountMinorUnits: 0,
      benefitType: "FULL_FEE_EXEMPTION",
      ordinanceReference: "City Ordinance No. 48",
      vatTreatment: "VAT_INCLUSIVE_NO_EXEMPTION",
      currency: "PHP",
      payableBasisReady: true,
      payableBasisReadinessStatus: "READY",
    });

    const calculation = screen.getByTestId("statutory-applied-facts");
    expect(calculation).toHaveTextContent("LGU ordinance benefit-₱89.29");
    expect(calculation).not.toHaveTextContent("LGU ordinance discount (");
    expect(calculation).toHaveTextContent("Final applicable VAT (12%)₱0.00");
    expect(calculation).toHaveTextContent("Total amount payable₱0.00");
  });
});

function renderPanel(state: StatutoryDiscountWorkflowState) {
  render(
    <StatutoryDiscountPanel
      basis={basis}
      client={{} as CentralPmsClient}
      context={buildTerminalContext(mode1Config())}
      state={state}
      availableEntitlements={["SENIOR_CITIZEN", "PWD"]}
      onStateChange={vi.fn()}
      onAppliedBasisReady={vi.fn(async () => undefined)}
    />,
  );
}

const basis: PayableBasisResponse = {
  parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
  siteGroupId: "22222222-2222-2222-2222-222222222222",
  siteId: "11111111-1111-1111-1111-111111111111",
  terminalId: "APT-DEV-001",
  ticketReference: "APT-ACTIVE-1001",
  parkingStatus: "Active",
  paymentStatus: "Unpaid",
  authoritativeAmountMinorUnits: 11200,
  currency: "PHP",
  tariffValidUntil: "2026-10-27T10:00:00Z",
  readyForCashAcceptance: true,
  blockingReasonCodes: [],
  retryable: false,
  safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
  correlationId: "safe-basis-correlation",
};
