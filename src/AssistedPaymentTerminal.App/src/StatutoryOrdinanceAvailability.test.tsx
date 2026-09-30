import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import type { CentralPmsClient, PayableBasisResponse, StatutoryEntitlementType } from "./api/centralPmsTypes";
import { buildTerminalContext } from "./terminalContext";
import { mode1Config } from "./test/testConfig";

describe("configured statutory entitlements", () => {
  it("offers only entitlements configured for the Site", () => {
    renderPanel(["SENIOR_CITIZEN"]);

    expect(screen.getByTestId("covered-entitlement-selector")).toHaveDisplayValue("Senior citizen");
    expect(screen.getByRole("button", { name: "Start statutory request" })).toBeEnabled();
    expect(screen.queryByRole("option", { name: "Person with disability" })).not.toBeInTheDocument();
  });

  it("offers no statutory action when the Site has no configured entitlement", () => {
    renderPanel([]);

    expect(screen.queryByTestId("covered-entitlement-selector")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
  });

  it("does not expose ordinance-source or policy diagnostics", () => {
    renderPanel(["SENIOR_CITIZEN", "PWD"]);

    expect(document.body).not.toHaveTextContent("Site ordinance availability");
    expect(document.body).not.toHaveTextContent("Source Unavailable");
    expect(screen.queryByRole("button", { name: "Retry ordinance availability" })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("compatibility policy");
    expect(document.body).not.toHaveTextContent("verification unknown");
  });
});

function renderPanel(availableEntitlements: readonly StatutoryEntitlementType[]) {
  render(
    <StatutoryDiscountPanel
      basis={basis}
      client={client}
      context={buildTerminalContext(mode1Config())}
      state={{ status: "none" }}
      availableEntitlements={availableEntitlements}
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
  authoritativeAmountMinorUnits: 12500,
  currency: "PHP",
  tariffValidUntil: "2026-08-03T01:00:00Z",
  readyForCashAcceptance: true,
  blockingReasonCodes: [],
  retryable: false,
  safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
  correlationId: "basis-correlation",
};

const client = {
  resolvePayableBasis: vi.fn(),
  revalidatePayableBasis: vi.fn(),
} as unknown as CentralPmsClient;
