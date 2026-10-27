import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import { CashierAccountabilityReport } from "./CashierAccountabilityReport";
import type { CashCustodySummary } from "./humanSessionBridge";

describe("CashierAccountabilityReport", () => {
  it("renders committed closed-custody evidence without treating confirmations as physical cash", async () => {
    const onPrint = vi.fn();
    renderReport(closedSummary(), { onPrint });

    expect(screen.getByText("CASHIER ACCOUNTABILITY REPORT")).toBeVisible();
    expect(screen.getByText("FINAL")).toBeVisible();
    expect(screen.getByText("cashier-stable-id")).toBeVisible();
    expect(screen.getByText("Current Cashier Name")).toBeVisible();
    expect(screen.getByText("Physical cash received (3)")).toBeVisible();
    expect(screen.getByText("Central-Confirmed Payments (Local Record) (2)")).toBeVisible();
    expect(screen.getByText("Completed safe drops")).toBeVisible();
    expect(screen.getByText("Completed direct remittances")).toBeVisible();
    expect(screen.getByText("Physical cash received is custody evidence, not fiscal sales.")).toBeVisible();
    expect(screen.getByText("BALANCED")).toBeVisible();
    const report = screen.getByLabelText("Printable Cashier Accountability Report");
    expect(report).toHaveTextContent("Completed cash returns₱200.00");
    expect(report).toHaveTextContent("Completed safe drops₱250.00");
    expect(report).toHaveTextContent("Completed direct remittances₱50.00");
    expect(report).toHaveTextContent("Expected closing cash₱1,700.00");
    expect(report).toHaveTextContent("Actual physical closing cash₱1,700.00");
    expect(report).toHaveTextContent("Signed cash variance₱0.00");

    await userEvent.click(screen.getByRole("button", { name: "Print report" }));
    expect(onPrint).toHaveBeenCalledOnce();
  });

  it.each([
    [10, "OVER"],
    [-10, "SHORT"],
    [0, "BALANCED"],
  ])("classifies a committed variance of %s as %s", (varianceAmount, expected) => {
    renderReport(closedSummary(varianceAmount));
    expect(screen.getByText(expected)).toBeVisible();
  });

  it("labels open custody as interim and withholds actual cash and final variance", () => {
    const summary = closedSummary();
    summary.session = {
      ...summary.session,
      status: "Open",
      closedAt: null,
      expectedClosingCashAmount: null,
      closingCashAmount: null,
      varianceAmount: null,
    };

    renderReport(summary);

    expect(screen.getByText("INTERIM - CUSTODY OPEN")).toBeVisible();
    expect(screen.getAllByText("Unavailable - custody open")).toHaveLength(3);
    expect(screen.getByText("INTERIM")).toBeVisible();
    expect(screen.queryByText("OVER")).not.toBeInTheDocument();
    expect(screen.queryByText("SHORT")).not.toBeInTheDocument();
  });

  it("does not present another or currently resolved identity as a historical cashier name", () => {
    renderReport(closedSummary(), {
      currentCashierId: "different-cashier-id",
      currentCashierDisplayName: "Different Cashier",
    });

    expect(screen.getByText("Unavailable - no historical name snapshot")).toBeVisible();
    expect(screen.queryByText("Different Cashier")).not.toBeInTheDocument();
  });
});

function renderReport(
  summary: CashCustodySummary,
  overrides: Partial<ComponentProps<typeof CashierAccountabilityReport>> = {},
) {
  return render(
    <CashierAccountabilityReport
      summary={summary}
      currentCashierId="cashier-stable-id"
      currentCashierDisplayName="Current Cashier Name"
      siteDisplayName="PITX Level 3"
      terminalDisplayName="APT PITX Level 3"
      terminalSerialNumber="PF6BJ1WA"
      generatedAt="2026-10-27T06:30:00Z"
      onClose={() => undefined}
      {...overrides}
    />,
  );
}

function closedSummary(varianceAmount = 0): CashCustodySummary {
  return {
    session: {
      id: "11111111-1111-4111-8111-111111111111",
      cashierId: "cashier-stable-id",
      authenticatedCashierSessionReference: "session-reference",
      cashierShiftId: "SHIFT-ACCOUNTABILITY-001",
      terminalId: "apt-pitx-level-3-01",
      siteId: "PITX-LEVEL-3",
      siteGroupId: "PITX",
      posServerId: "pos-pitx-level-3",
      openingCashAmount: 1_000,
      openedAt: "2026-10-27T00:00:00Z",
      status: "Closed",
      expectedClosingCashAmount: 1_700,
      closingCashAmount: 1_700 + varianceAmount,
      varianceAmount,
      closedAt: "2026-10-27T06:00:00Z",
      unresolvedSubmissionCountAtClose: 1,
    },
    submissions: {
      physicalCashReceiptCount: 3,
      confirmedSubmissionCount: 2,
      pendingSubmissionCount: 1,
      conflictingSubmissionCount: 0,
      rejectedSubmissionCount: 0,
      unresolvedSubmissionCount: 1,
      pendingFiscalIssuanceCount: 0,
    },
    movements: [],
    accountability: {
      physicalCashReceivedMinorUnits: 100_000,
      canonicallyConfirmedCashMinorUnits: 75_000,
      completedCashReturnsMinorUnits: 20_000,
      completedSafeDropsMinorUnits: 25_000,
      completedDirectRemittancesMinorUnits: 5_000,
    },
  };
}
