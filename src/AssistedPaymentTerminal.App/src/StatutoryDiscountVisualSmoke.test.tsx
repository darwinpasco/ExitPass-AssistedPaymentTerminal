import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App, TerminalShell } from "./App";
import type { CentralPmsClient, PayableBasisResponse, StatutoryOrdinanceAvailabilityResult } from "./api/centralPmsTypes";
import {
  shouldUseStatutoryDiscountVisualSmoke,
  StatutoryDiscountVisualSmokeShell,
  statutoryDiscountVisualSmokeScenarios,
} from "./StatutoryDiscountVisualSmoke";
import type { StatutoryEvidenceBridge, StatutoryEvidenceBridgeResult, StatutoryEvidenceChannelResponse } from "./statutoryEvidenceBridge";
import { mode1Config, rawMode1Config } from "./test/testConfig";

function renderSmoke(
  onRevalidate?: (basis: PayableBasisResponse) => void,
  ordinanceRevalidate?: NonNullable<CentralPmsClient["revalidateStatutoryOrdinanceAvailability"]>,
  evidenceRevalidate?: (
    original: StatutoryEvidenceBridge["revalidate"],
    correlationId: string,
    decisionCommandId: string,
  ) => Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>,
) {
  render(
    <StatutoryDiscountVisualSmokeShell
      config={{ ...mode1Config(), nonLiveCashCaptureEnabled: true }}
      renderTerminalShell={({ config, client, initialResolvedBasis, initialStatutoryState, bridge, evidenceBridge, initialCashEntryRequested, renderKey }) => {
        const wrappedClient: CentralPmsClient = onRevalidate || ordinanceRevalidate
          ? {
            resolvePayableBasis: (...args) => client.resolvePayableBasis(...args),
            revalidatePayableBasis: (basis, correlationId) => {
              onRevalidate?.(basis);
              return client.revalidatePayableBasis(basis, correlationId);
            },
            ...(client.submitStatutoryDiscountDecision
              ? { submitStatutoryDiscountDecision: (...args) => client.submitStatutoryDiscountDecision!(...args) }
              : {}),
            ...(client.getStatutoryDiscountDecision
              ? { getStatutoryDiscountDecision: (...args) => client.getStatutoryDiscountDecision!(...args) }
              : {}),
            ...(client.resolveStatutoryOrdinanceAvailability
              ? { resolveStatutoryOrdinanceAvailability: (...args) => client.resolveStatutoryOrdinanceAvailability!(...args) }
              : {}),
            ...(ordinanceRevalidate
              ? { revalidateStatutoryOrdinanceAvailability: ordinanceRevalidate }
              : client.revalidateStatutoryOrdinanceAvailability
                ? { revalidateStatutoryOrdinanceAvailability: (...args) => client.revalidateStatutoryOrdinanceAvailability!(...args) }
              : {}),
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
            localJournalBridge={bridge}
            statutoryEvidenceBridge={evidenceRevalidate
              ? { ...evidenceBridge, revalidate: (correlationId, decisionCommandId) => evidenceRevalidate(evidenceBridge.revalidate, correlationId, decisionCommandId) }
              : evidenceBridge}
            restorePayableBasisOnMount={false}
            initialCashEntryRequested={initialCashEntryRequested}
          />
        );
      }}
    />,
  );
}

describe("StatutoryDiscountVisualSmokeShell", () => {
  it("derives the cashier mask from a raw synthetic fixture instead of pre-masking fixture input", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Draft statutory request" }));

    expect(screen.getByRole("textbox", { name: "Statutory ID" })).toHaveValue("AB******7890");
    expect(document.body).not.toHaveTextContent("AB1234567890");
  });

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

  it("exposes every required statutory orchestration scenario as an interactive button", async () => {
    renderSmoke();

    for (const scenario of statutoryDiscountVisualSmokeScenarios) {
      expect(screen.getByRole("button", { name: scenario.label })).toBeInTheDocument();
    }

    expect(screen.getByRole("heading", { name: "Statutory Discount Visual Smoke" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Receipt Visual Smoke" })).not.toBeInTheDocument();
    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toBeInTheDocument();
    expect(await screen.findByTestId("pwd-ordinance-availability")).toBeInTheDocument();
  });

  it("enables Continue to Cash only for applied acknowledged statutory basis with local prerequisites", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "APPLIED complete but local prerequisites blocked" }));
    expect(screen.getByRole("heading", { name: "Statutory payable basis ready for cash acceptance" })).toBeInTheDocument();
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Blocked");
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "APPLIED complete and Continue to Cash enabled" }));
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Satisfied");
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeEnabled();
  });

  it("opens cash entry only after statutory-aware Continue to Cash revalidation passes unchanged", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash revalidation PASSED_UNCHANGED" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect(await screen.findByText("Revalidation passed unchanged")).toBeInTheDocument();
    expect(await screen.findByLabelText("Non-live cash custody capture")).toBeInTheDocument();
    expect(screen.getByLabelText("Amount due")).toHaveValue("100.00");
    expect(screen.getByText(/Cash has not yet been recorded at this terminal/)).toBeInTheDocument();
    expect(screen.queryByText("No statutory request is active for this payable basis.")).not.toBeInTheDocument();
    expect(screen.getByTestId("statutory-status-card")).toHaveTextContent("DecisionRecorded");
    expect(screen.getByTestId("statutory-status-card")).toHaveTextContent("ApplicationRecorded");
    expect(screen.getByTestId("statutory-applied-facts")).toHaveTextContent("Authoritative version applied");
    expect(screen.getByTestId("statutory-applied-facts")).not.toHaveTextContent("99999999-9999-4999-8999-999999990001");
    expect(screen.getByTestId("statutory-applied-facts")).toHaveTextContent("100.00");
  });

  it("keeps Continue to Cash AMOUNT_CHANGED statutory and pre-custody", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash revalidation AMOUNT_CHANGED" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect(await screen.findByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(screen.getByText("Tariff update")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("99999999-9999-4999-8999-999999990001");
    expect(document.body).not.toHaveTextContent("99999999-9999-4999-8999-999999990002");
    expect(screen.getByText("Statutory decision")).toBeInTheDocument();
    expect(screen.getByText("Statutory application")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("77777777-7777-4777-8777-777777770777");
    expect(document.body).not.toHaveTextContent("88888888-8888-4888-8888-888888880001");
    expect(screen.queryByText("No statutory request is active for this payable basis.")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();
  });

  it("keeps cash entry closed when statutory-aware Continue to Cash revalidation returns a statutory blocker", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash statutory blocked" }));
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeEnabled();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect((await screen.findAllByText("Statutory payable basis is being applied. Action: Poll Readback or Check Application Status.")).length).toBeGreaterThan(0);
    expect(screen.getByTestId("statutory-status-card")).toHaveTextContent("Statutory Payable Basis Processing");
    expect(screen.getAllByText("Decision").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Application").length).toBeGreaterThan(0);
    expect(document.body).not.toHaveTextContent("77777777-7777-4777-8777-777777770777");
    expect(document.body).not.toHaveTextContent("88888888-8888-4888-8888-888888880001");
    expect(screen.queryByText("No statutory request is active for this payable basis.")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("blocks before local cash custody when J-005 revokes ordinance coverage", async () => {
    let calls = 0;
    renderSmoke(undefined, async (basis, entitlementType, correlationId): Promise<StatutoryOrdinanceAvailabilityResult> => {
      calls += 1;
      return {
        ok: true,
        response: {
          operation: "REVALIDATE",
          revalidationOutcome: "FAILED",
          classification: "EXPIRED",
          entitlementType,
          ordinanceCoverageAvailable: false,
          statutoryRequestAllowed: false,
          preCashRevalidationPassed: false,
          readyForStatutoryCashFlow: false,
          ordinaryPaymentPreserved: true,
          parkingSessionId: basis.parkingSessionId,
          siteId: basis.siteId,
          siteGroupId: basis.siteGroupId,
          resolvedScopeType: "SITE",
          coverageClassification: "EXPIRED",
          policyStatusClassification: "EXPIRED",
          supportReference: correlationId,
          correlationId,
          evaluatedAt: "2026-08-03T00:00:00Z",
          retryable: false,
          safeMessage: "Statutory parking coverage is no longer effective. Cash acceptance remains blocked for the statutory path.",
        },
      };
    });

    await userEvent.click(screen.getByRole("button", { name: "APPLIED complete and Continue to Cash enabled" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect((await screen.findAllByText("Statutory parking coverage is no longer effective. Cash acceptance remains blocked for the statutory path.")).length).toBeGreaterThan(0);
    expect(calls).toBe(1);
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.getByTestId("ordinary-payment-preserved")).toHaveTextContent("Ordinary payment remains available");
  });

  it("blocks the second J-005 check immediately before CASH_RECEIVED", async () => {
    let calls = 0;
    renderSmoke(undefined, async (basis, entitlementType, correlationId): Promise<StatutoryOrdinanceAvailabilityResult> => {
      calls += 1;
      const passed = calls === 1;
      return {
        ok: true,
        response: {
          operation: "REVALIDATE",
          revalidationOutcome: passed ? "PASSED_UNCHANGED" : "FAILED",
          classification: passed ? "AVAILABLE" : "SOURCE_UNAVAILABLE",
          entitlementType,
          ordinanceCoverageAvailable: passed,
          statutoryRequestAllowed: passed,
          preCashRevalidationPassed: passed,
          readyForStatutoryCashFlow: passed,
          ordinaryPaymentPreserved: true,
          parkingSessionId: basis.parkingSessionId,
          siteId: basis.siteId,
          siteGroupId: basis.siteGroupId,
          resolvedScopeType: "SITE",
          coverageClassification: passed ? "AVAILABLE" : "SOURCE_UNAVAILABLE",
          policyStatusClassification: passed ? "ACTIVE" : "SOURCE_UNAVAILABLE",
          supportReference: correlationId,
          correlationId,
          evaluatedAt: "2026-08-03T00:00:00Z",
          retryable: !passed,
          safeMessage: passed
            ? "Statutory parking coverage remains available."
            : "Statutory coverage could not be revalidated. Do not accept cash for the statutory path.",
        },
      };
    });

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Statutory coverage could not be revalidated. Do not accept cash for the statutory path.")).length).toBeGreaterThan(0);
    expect(calls).toBe(2);
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
  });

  it("exposes deterministic interactive J-005 failure scenarios", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash ordinance coverage revoked" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    expect((await screen.findAllByText("Statutory parking coverage expired before cash acceptance.")).length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received ordinance unavailable" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));
    expect((await screen.findAllByText("Statutory coverage could not be revalidated. Do not accept cash for the statutory path.")).length).toBeGreaterThan(0);
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
  });

  it("records statutory CASH_RECEIVED once after the second immediate revalidation", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByText("Cash received locally")).toBeInTheDocument();
    const evidence = await screen.findByTestId("statutory-tender-evidence");
    expect(screen.getAllByText("Cash received locally")).toHaveLength(1);
    expect(within(evidence).getAllByText("Recorded")).toHaveLength(2);
    expect(within(evidence).getByText("Authoritative version retained")).toBeInTheDocument();
    expect(within(evidence).getByText(/100\.00/)).toBeInTheDocument();
    expect(within(evidence).getByText(/Revalidated at/)).toBeInTheDocument();
  });

  it("blocks statutory cash when evidence readiness fails before entry or immediate custody", async () => {
    const scanPending = evidenceBridgeSuccess("SCAN_PENDING", false);
    renderSmoke(undefined, undefined, async () => scanPending);

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect((await screen.findAllByText("Evidence security scanning is pending.")).length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
  });

  it("does not write CASH_RECEIVED when evidence changes after cash entry opens", async () => {
    let calls = 0;
    renderSmoke(undefined, undefined, async (original, correlationId, decisionCommandId) => {
      calls++;
      return calls === 1
        ? original(correlationId, decisionCommandId)
        : evidenceBridgeSuccess("SCAN_PENDING", false);
    });

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Evidence security scanning is pending.")).length).toBeGreaterThan(0);
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
  });

  it("keeps second-stage revalidation failures before statutory CASH_RECEIVED", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received revalidation AMOUNT_CHANGED" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(screen.getByText("Tariff update")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("77777777-7777-4777-8777-777777770777");
    expect(document.body.textContent).not.toContain("88888888-8888-4888-8777-777777770777");
    expect(document.body.textContent).not.toContain("88888888-8888-4888-8888-888888880001");
    expect(document.body.textContent).toContain("100.00");
    expect(document.body.textContent).toContain("125.00");
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received retryable failure" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByText("Central PMS could not revalidate the statutory payable basis. Try again before accepting cash.")).toBeInTheDocument();
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received terminal failure" }));
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));
    await screen.findByLabelText("Non-live cash custody capture");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Parking session is already paid.")).length).toBeGreaterThan(0);
    expect(screen.queryByText("Cash received locally")).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();
  });

  it("restores statutory CASH_RECEIVED custody evidence after restart", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after statutory CASH_RECEIVED preserves custody evidence" }));

    expect(await screen.findByText(/State at local cash capture:/)).toBeInTheDocument();
    const evidence = await screen.findByTestId("statutory-tender-evidence");
    expect(within(evidence).getAllByText("Recorded")).toHaveLength(2);
    expect(within(evidence).getByText("Authoritative version retained")).toBeInTheDocument();
    expect(within(evidence).getByText(/100\.00/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("restores statutory CASH_RECEIVED and shows terminal-cash submission recovery", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after statutory CASH_RECEIVED resumes terminal-cash submission" }));

    expect(await screen.findByText(/State at local cash capture:/, {}, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Central PMS canonical payment" }, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByText(/Submitting cash payment/, {}, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Submit / Check Central PMS" }, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("renders applied statutory facts and keeps Continue to Cash disabled", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Applied complete" }));

    expect(screen.getByText("Statutory Payable Basis Applied")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Statutory payable basis ready for cash acceptance" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Ready for cash acceptance" })).not.toBeInTheDocument();
    expect(screen.getByText(/Continue to Cash runs statutory-aware revalidation before cash entry/)).toBeInTheDocument();
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
    expect(document.body).not.toHaveTextContent("dddddddd-dddd-4ddd-8ddd-dddddddd1001");
    expect(document.body).not.toHaveTextContent("99999999-9999-4999-8999-999999990001");
    expect(screen.queryByRole("heading", { name: "Ready for cash acceptance" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge new amount" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Acknowledge new amount" }));

    await screen.findByRole("heading", { name: "Statutory payable basis ready for cash acceptance" });
    expect(revalidated).toHaveLength(1);
    expect(revalidated[0].statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId).toBe("77777777-7777-4777-8777-777777770777");
    expect(revalidated[0].tariffSnapshotId).toBe("99999999-9999-4999-8999-999999990001");
    expect(revalidated[0].authoritativeAmountMinorUnits).toBe(10000);

    expect(await screen.findByRole("heading", { name: "Statutory payable basis ready for cash acceptance" })).toBeInTheDocument();
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

function evidenceBridgeSuccess(
  lifecycleClassification: string,
  readyForAptPreCash: boolean,
): StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse> {
  return {
    ok: true,
    command: "statutoryEvidence.revalidate",
    correlationId: "statutory-evidence-failure-correlation",
    payload: {
      classification: "RESOLVED",
      retryable: lifecycleClassification === "SCAN_RETRYABLE",
      errorCode: null,
      correlationId: "statutory-evidence-failure-correlation",
      sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
      evidenceRequired: true,
      evidenceSetReference: "11111111-1111-4111-8111-111111110001",
      evidenceItemReference: "22222222-2222-4222-8222-222222220001",
      allowedContentTypes: ["image/jpeg", "image/png"],
      maximumContentLengthBytes: 5 * 1024 * 1024,
      maximumImageWidth: 4096,
      maximumImageHeight: 4096,
      maximumImagePixelCount: 16_000_000,
      requiredDocumentType: "STATUTORY_ID_IMAGE",
      requiredItemRole: "PRIMARY_IDENTITY_EVIDENCE",
      lifecycleClassification,
      replacementPosture: "REPLACEMENT_NOT_ALLOWED",
      readyForReview: false,
      readyForAptPreCash,
      blockingReasonCode: readyForAptPreCash ? null : `STATUTORY_EVIDENCE_${lifecycleClassification}`,
      evaluatedAt: "2026-08-05T10:00:00Z",
      safeMessage: lifecycleClassification === "SCAN_PENDING"
        ? "Evidence security scanning is pending."
        : lifecycleClassification,
    },
  };
}
