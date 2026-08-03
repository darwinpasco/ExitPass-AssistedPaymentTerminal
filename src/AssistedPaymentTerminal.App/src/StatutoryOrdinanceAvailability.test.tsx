import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import type {
  CentralPmsClient,
  PayableBasisResponse,
  StatutoryOrdinanceAvailabilityClassification,
  StatutoryOrdinanceAvailabilityResponse,
  StatutoryOrdinanceAvailabilityViewState,
} from "./api/centralPmsTypes";
import { buildTerminalContext } from "./terminalContext";
import { mode1Config } from "./test/testConfig";

const blockedClassifications: StatutoryOrdinanceAvailabilityClassification[] = [
  "NOT_AVAILABLE",
  "NO_CONFIGURED_POLICY",
  "NOT_YET_EFFECTIVE",
  "EXPIRED",
  "INACTIVE",
  "AMBIGUOUS_SCOPE",
  "SESSION_NOT_FOUND",
  "AMBIGUOUS_SESSION",
  "SOURCE_UNAVAILABLE",
  "MALFORMED_AUTHORITATIVE_STATE",
  "ACCESS_DENIED",
  "UNEXPECTED_FAILURE",
];

describe("Statutory ordinance availability gate", () => {
  it.each(blockedClassifications)("keeps %s fail closed without converting it to coverage", (classification) => {
    renderPanel(readyState(classification));

    expect(screen.getByTestId("senior-citizen-ordinance-availability")).toHaveTextContent(friendly(classification));
    expect(screen.getByTestId("pwd-ordinance-availability")).toHaveTextContent(friendly(classification));
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
    expect(screen.getByTestId("ordinary-payment-preserved")).toHaveTextContent("Ordinary payment remains available");
  });

  it("does not render evidence or entitlement controls before authoritative availability is known", () => {
    renderPanel({ status: "loading", parkingSessionId: basis.parkingSessionId, siteId: basis.siteId, restoredRefresh: false });

    expect(screen.getByRole("status")).toHaveTextContent("Checking authoritative");
    expect(screen.queryByTestId("covered-entitlement-selector")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Masked statutory ID reference")).not.toBeInTheDocument();
  });

  it("offers keyboard-focusable retry only for retryable authoritative failures", () => {
    renderPanel(readyState("SOURCE_UNAVAILABLE", true));

    expect(screen.getByRole("button", { name: "Retry ordinance availability" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
  });

  it("renders only safe fields from the browser contract", () => {
    renderPanel(readyState("MALFORMED_AUTHORITATIVE_STATE"));

    expect(document.body).not.toHaveTextContent("stack trace");
    expect(document.body).not.toHaveTextContent("SELECT ");
    expect(document.body).not.toHaveTextContent("Authorization");
    expect(document.body).not.toHaveTextContent("X-ExitPass-Permissions");
    expect(document.body).not.toHaveTextContent("internalPolicyId");
  });
});

function renderPanel(ordinanceAvailability: StatutoryOrdinanceAvailabilityViewState) {
  render(
    <StatutoryDiscountPanel
      basis={basis}
      client={client}
      context={buildTerminalContext(mode1Config())}
      state={{ status: "none" }}
      ordinanceAvailability={ordinanceAvailability}
      onRetryAvailability={vi.fn()}
      onStateChange={vi.fn()}
      onAppliedBasisReady={vi.fn(async () => undefined)}
    />,
  );
}

function readyState(classification: StatutoryOrdinanceAvailabilityClassification, retryable = false): StatutoryOrdinanceAvailabilityViewState {
  return {
    status: "ready",
    parkingSessionId: basis.parkingSessionId,
    siteId: basis.siteId,
    restoredRefresh: false,
    seniorCitizen: response("SENIOR_CITIZEN", classification, retryable),
    pwd: response("PWD", classification, retryable),
  };
}

function response(
  entitlementType: "SENIOR_CITIZEN" | "PWD",
  classification: StatutoryOrdinanceAvailabilityClassification,
  retryable: boolean,
): StatutoryOrdinanceAvailabilityResponse {
  const available = classification === "AVAILABLE";
  return {
    operation: "RESOLVE",
    revalidationOutcome: null,
    classification,
    entitlementType,
    ordinanceCoverageAvailable: available,
    statutoryRequestAllowed: available,
    preCashRevalidationPassed: false,
    readyForStatutoryCashFlow: available,
    ordinaryPaymentPreserved: true,
    parkingSessionId: basis.parkingSessionId,
    siteId: basis.siteId,
    siteGroupId: basis.siteGroupId,
    resolvedScopeType: "SITE",
    coverageClassification: classification,
    policyStatusClassification: classification,
    supportReference: "safe-support-reference",
    correlationId: "safe-correlation-reference",
    evaluatedAt: "2026-08-03T00:00:00Z",
    retryable,
    safeMessage: retryable ? "Coverage could not be confirmed. Retry is available." : "Coverage is not available for this entitlement.",
  };
}

function friendly(value: string): string {
  return value.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
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
