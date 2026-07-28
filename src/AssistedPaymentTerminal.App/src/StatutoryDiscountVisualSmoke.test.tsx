import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App, TerminalShell } from "./App";
import type { CentralPmsClient, PayableBasisResponse } from "./api/centralPmsTypes";
import {
  shouldUseStatutoryDiscountVisualSmoke,
  StatutoryDiscountVisualSmokeShell,
  statutoryDiscountVisualSmokeScenarios,
} from "./StatutoryDiscountVisualSmoke";
import { mode1Config, rawMode1Config } from "./test/testConfig";

function renderSmoke(onRevalidate?: (basis: PayableBasisResponse) => void) {
  render(
    <StatutoryDiscountVisualSmokeShell
      config={{ ...mode1Config(), nonLiveCashCaptureEnabled: true }}
      renderTerminalShell={({ config, client, initialResolvedBasis, initialStatutoryState, renderKey }) => {
        const wrappedClient: CentralPmsClient = onRevalidate
          ? {
            resolvePayableBasis: (...args) => client.resolvePayableBasis(...args),
            revalidatePayableBasis: (basis, correlationId) => {
              onRevalidate(basis);
              return client.revalidatePayableBasis(basis, correlationId);
            },
            submitStatutoryDiscountDecision: (...args) => client.submitStatutoryDiscountDecision(...args),
            getStatutoryDiscountDecision: (...args) => client.getStatutoryDiscountDecision(...args),
          }
          : client;
        return (
          <TerminalShell
            key={renderKey}
            config={config}
            client={wrappedClient}
            initialReferenceType="ticket"
            initialReferenceValue="APT-ACTIVE-1001"
            initialResolvedBasis={initialResolvedBasis}
            initialStatutoryState={initialStatutoryState}
            restorePayableBasisOnMount={false}
          />
        );
      }}
    />,
  );
}

describe("StatutoryDiscountVisualSmokeShell", () => {
  it("is development-only and mounted by the statutory discount query flag", async () => {
    expect(shouldUseStatutoryDiscountVisualSmoke("?statutoryDiscountVisualSmoke=1", true)).toBe(true);
    expect(shouldUseStatutoryDiscountVisualSmoke("?statutoryDiscountVisualSmoke=1", false)).toBe(false);
    expect(shouldUseStatutoryDiscountVisualSmoke("?payableBasisVisualSmoke=1", true)).toBe(false);

    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", "/?statutoryDiscountVisualSmoke=1");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Statutory Discount Visual Smoke" })).toBeInTheDocument();
    expect(screen.getByText("Development-only")).toBeInTheDocument();
    expect(screen.getByLabelText("Statutory discount visual smoke scenarios")).toBeInTheDocument();
  });

  it("exposes every required statutory orchestration scenario as an interactive button", () => {
    renderSmoke();

    for (const scenario of statutoryDiscountVisualSmokeScenarios) {
      expect(screen.getByRole("button", { name: scenario.label })).toBeInTheDocument();
    }

    expect(screen.getByRole("heading", { name: "Statutory Discount Visual Smoke" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Receipt Visual Smoke" })).not.toBeInTheDocument();
  });

  it("renders applied statutory facts and keeps Continue to Cash disabled", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Applied complete" }));

    expect(screen.getByText("Statutory Payable Basis Applied")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Central PMS payable basis ready" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Ready for cash acceptance" })).not.toBeInTheDocument();
    expect(screen.getByText("Central PMS confirms the statutory payable basis is ready. Statutory cash acceptance is not enabled in this slice.")).toBeInTheDocument();
    const appliedFacts = screen.getByTestId("statutory-applied-facts");
    expect(within(appliedFacts).getByText("Applied statutory payable basis")).toBeInTheDocument();
    expect(within(appliedFacts).getByText("VAT-exclusive amount")).toBeInTheDocument();
    expect(within(appliedFacts).getByText("Statutory discount")).toBeInTheDocument();
    expect(within(appliedFacts).getByText("Final payable amount")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("routes applied amount or snapshot changes through acknowledgement and statutory-aware revalidation", async () => {
    const revalidated: PayableBasisResponse[] = [];
    renderSmoke((basis) => revalidated.push(basis));

    await userEvent.click(screen.getByRole("button", { name: "Applied amount changed" }));

    expect(screen.getByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getAllByText("₱125.00").length).toBeGreaterThan(0);
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(screen.getAllByText("₱100.00").length).toBeGreaterThan(0);
    expect(screen.getByText("Original tariff snapshot")).toBeInTheDocument();
    expect(screen.getByText("Applied tariff snapshot")).toBeInTheDocument();
    expect(screen.getByText("dddddddd-dddd-4ddd-8ddd-dddddddd1001")).toBeInTheDocument();
    expect(screen.getByText("99999999-9999-4999-8999-999999990001")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Ready for cash acceptance" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge new amount" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Acknowledge new amount" }));

    await screen.findByRole("heading", { name: "Central PMS payable basis ready" });
    expect(revalidated).toHaveLength(1);
    expect(revalidated[0].statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId).toBe("77777777-7777-4777-8777-777777770777");
    expect(revalidated[0].tariffSnapshotId).toBe("99999999-9999-4999-8999-999999990001");
    expect(revalidated[0].authoritativeAmountMinorUnits).toBe(10000);

    expect(await screen.findByRole("heading", { name: "Central PMS payable basis ready" })).toBeInTheDocument();
    expect(screen.getByText("Statutory Payable Basis Applied")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("shows restart scenarios as restored and still pre-CASH_RECEIVED", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart during application processing" }));

    expect(screen.getByText("Statutory payable-basis application remains in progress after restart. Use canonical readback before taking another action.")).toBeInTheDocument();
    expect(screen.getByText("Statutory Payable Basis Processing")).toBeInTheDocument();
    expect(screen.getByText("Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(document.body).not.toHaveTextContent("Cash received locally");
  });

  it("restores applied amount changes with acknowledgement still pending", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after applied amount change" }));

    expect(screen.getByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge new amount" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS payable basis ready" })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("Cash received locally");
  });

  it("uses state-specific statutory blocker wording", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Awaiting review" }));
    expect(screen.getByText("Statutory request is awaiting Operator Console review.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Approved, application not requested" }));
    expect(screen.getAllByText("Statutory request was approved. Statutory payable-basis application has not been requested. Action: Submit Application Intent.").length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole("button", { name: "Application processing" }));
    expect(screen.getAllByText("Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.").length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole("button", { name: "Rejected" }));
    expect(screen.getByText("Operator Console rejected the statutory request. Application intent is unavailable.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Terminal failure" }));
    expect(screen.getByText("A terminal statutory failure requires support. Blind retry is disabled.")).toBeInTheDocument();
  });
});
