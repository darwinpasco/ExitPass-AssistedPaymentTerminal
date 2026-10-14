import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { StatutoryDiscountPanel } from "./StatutoryDiscountPanel";
import type { CentralPmsClient, PayableBasisResponse } from "./api/centralPmsTypes";
import { containsManualStatutoryIdMask, maskStatutoryId } from "./statutoryIdMasking";
import { buildTerminalContext } from "./terminalContext";
import { mode1Config } from "./test/testConfig";
describe("statutory ID presentation masking", () => {
  it.each([
    ["AB1234567890", "AB******7890"],
    ["123456789012", "12******9012"],
    ["aB12Cd345678", "aB******5678"],
    ["ABC1234", "AB*1234"],
    ["LONG-STATUTORY-ID-1234567890", "LO**********************7890"],
    ["  AB1234567890  ", "AB******7890"],
    ["ABCDEF", "******"],
    ["ABC", "***"],
    ["", ""],
  ])("masks %j as %j", (input, expected) => {
    expect(maskStatutoryId(input)).toBe(expected);
  });

  it("handles absent values without exposing content", () => {
    expect(maskStatutoryId(null)).toBe("");
    expect(maskStatutoryId(undefined)).toBe("");
  });

  it("does not consider user-entered asterisks proof of masking", () => {
    expect(containsManualStatutoryIdMask("AB1234567890")).toBe(false);
    expect(containsManualStatutoryIdMask("AB******7890")).toBe(true);
  });

  it("masks a restored draft ID but does not expose APT statutory submission", async () => {
    const user = userEvent.setup();
    const rawId = "AB1234567890";
    const maskedId = "AB******7890";
    const submitDecision = vi.fn();
    const onStateChange = vi.fn();

    renderPanel({ submitStatutoryDiscountDecision: submitDecision } as unknown as CentralPmsClient, onStateChange);

    const input = screen.getByRole("textbox", { name: "Statutory ID" });
    await user.type(input, rawId);
    expect(input).toHaveValue(rawId);
    await user.tab();

    expect(input).toHaveValue(maskedId);
    expect(input).not.toHaveValue(rawId);
    expect(document.body).not.toHaveTextContent(rawId);
    expect(cashierAccessibilityText()).not.toContain(rawId);

    expect(screen.queryByRole("button", { name: "Submit for Operator Review" })).not.toBeInTheDocument();
    expect(submitDecision).not.toHaveBeenCalled();
    expect(JSON.stringify(onStateChange.mock.calls)).not.toContain(rawId);
    expect(window.localStorage.getItem("statutoryId")).toBeNull();
    expect(window.sessionStorage.getItem("statutoryId")).toBeNull();
  });

  it("rejects manually entered asterisks without exposing a submission action", async () => {
    const user = userEvent.setup();
    renderPanel({ submitStatutoryDiscountDecision: vi.fn() } as unknown as CentralPmsClient, vi.fn());

    const input = screen.getByRole("textbox", { name: "Statutory ID" });
    await user.type(input, "AB******7890");
    await user.tab();
    await user.click(screen.getByRole("checkbox"));

    expect(screen.getByText("Enter the statutory ID normally without asterisks. Masking is automatic.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Submit for Operator Review" })).not.toBeInTheDocument();
  });

  it("restores only the masked recovery value and never reconstructs raw ID input", () => {
    const rawId = "AB1234567890";
    renderPanel({} as CentralPmsClient, vi.fn(), {
      status: "draft",
      entitlementType: "SENIOR_CITIZEN",
      maskedIdReference: maskStatutoryId(rawId),
      restoredAfterRestart: true,
    });

    expect(screen.getByRole("textbox", { name: "Statutory ID" })).toHaveValue("AB******7890");
    expect(document.body).not.toHaveTextContent(rawId);
    expect(cashierAccessibilityText()).not.toContain(rawId);
  });
});

function renderPanel(
  client: CentralPmsClient,
  onStateChange: ReturnType<typeof vi.fn>,
  state: Parameters<typeof StatutoryDiscountPanel>[0]["state"] = { status: "draft" },
) {
  render(
    <StatutoryDiscountPanel
      basis={basis}
      client={client}
      context={buildTerminalContext(mode1Config())}
      state={state}
      availableEntitlements={["SENIOR_CITIZEN", "PWD"]}
      onStateChange={onStateChange}
      onAppliedBasisReady={vi.fn(async () => undefined)}
    />,
  );
}

function cashierAccessibilityText(): string {
  return Array.from(document.querySelectorAll<HTMLElement>("*"))
    .flatMap((element) => [element.getAttribute("aria-label"), element.getAttribute("aria-description"), element.getAttribute("title")])
    .filter((value): value is string => Boolean(value))
    .join("\n");
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
  tariffValidUntil: "2026-08-07T01:00:00Z",
  readyForCashAcceptance: true,
  blockingReasonCodes: [],
  retryable: false,
  safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
  correlationId: "safe-basis-correlation",
};
