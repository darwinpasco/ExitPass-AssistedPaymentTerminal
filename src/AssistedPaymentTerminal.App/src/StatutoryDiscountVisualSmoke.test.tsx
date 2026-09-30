import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App, TerminalShell } from "./App";
import type { CentralPmsClient, PayableBasisResponse } from "./api/centralPmsTypes";
import {
  shouldUseStatutoryDiscountVisualSmoke,
  StatutoryDiscountVisualSmokeShell,
  statutoryDiscountVisualSmokeScenarios,
} from "./StatutoryDiscountVisualSmoke";
import type { PayableBasisVisualSmokeBridge } from "./PayableBasisVisualSmoke";
import type { StatutoryEvidenceBridge, StatutoryEvidenceBridgeResult, StatutoryEvidenceChannelResponse } from "./statutoryEvidenceBridge";
import { mode1Config, rawMode1Config } from "./test/testConfig";

function renderSmoke(
  onRevalidate?: (basis: PayableBasisResponse) => void,
  evidenceRevalidate?: (
    original: StatutoryEvidenceBridge["revalidate"],
    correlationId: string,
    decisionCommandId: string,
  ) => Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>,
): () => PayableBasisVisualSmokeBridge {
  let smokeBridge: PayableBasisVisualSmokeBridge | null = null;
  render(
    <StatutoryDiscountVisualSmokeShell
      config={mode1Config()}
      renderTerminalShell={({ config, client, initialResolvedBasis, initialStatutoryState, bridge, evidenceBridge, initialCashEntryRequested, renderKey }) => {
        smokeBridge = bridge;
        const wrappedClient: CentralPmsClient = onRevalidate
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

  if (!smokeBridge) {
    throw new Error("Statutory visual-smoke bridge was not initialized.");
  }

  return () => {
    if (!smokeBridge) {
      throw new Error("Statutory visual-smoke bridge is unavailable.");
    }
    return smokeBridge;
  };
}

describe("StatutoryDiscountVisualSmokeShell", () => {
  it("does not expose statutory identity evidence in the cashier workflow", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Draft statutory request" }));

    expect(screen.queryByRole("textbox", { name: "Statutory ID" })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("AB1234567890");
    expect(document.body).not.toHaveTextContent("AB******7890");
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
    expect(document.body).not.toHaveTextContent("Site ordinance availability");
    expect(document.body).not.toHaveTextContent("Source Unavailable");
    expect(screen.queryByRole("button", { name: "Retry ordinance availability" })).not.toBeInTheDocument();
  });

  it("enables direct cash recording for an applied acknowledged statutory basis when prerequisites pass", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "APPLIED complete and Continue to Cash enabled" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Approved");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Continue to Cash" })).not.toBeInTheDocument();
  });

  it("runs statutory-aware revalidation at Record Cash Received", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash revalidation PASSED_UNCHANGED" }));
    expect(await screen.findByLabelText("Cash custody capture")).toBeInTheDocument();
    expect(screen.getByLabelText("Amount due")).toHaveValue("100.00");
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Approved");
    expect(screen.queryByRole("button", { name: "Continue to Cash" })).not.toBeInTheDocument();
  });

  it("keeps AMOUNT_CHANGED statutory state before irreversible cash", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash revalidation AMOUNT_CHANGED" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("99999999-9999-4999-8999-999999990001");
    expect(document.body).not.toHaveTextContent("99999999-9999-4999-8999-999999990002");
    expect(document.body).not.toHaveTextContent("77777777-7777-4777-8777-777777770777");
    expect(document.body).not.toHaveTextContent("88888888-8888-4888-8888-888888880001");
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();
  });

  it("keeps cash recording blocked while statutory application is processing", async () => {
    const getBridge = renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash statutory blocked" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Approved");
    expect(document.body).not.toHaveTextContent("77777777-7777-4777-8777-777777770777");
    expect(document.body).not.toHaveTextContent("88888888-8888-4888-8888-888888880001");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));
    expect(screen.queryByRole("heading", { name: "Complete transaction" })).not.toBeInTheDocument();
    const readback = await getBridge().readTenderByParkingSession("blocked-readback", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001");
    expect(readback.ok).toBe(true);
    if (!readback.ok) throw new Error(readback.error.message);
    expect(readback.payload.tender?.currentLocalState).not.toBe("CashReceived");
  });

  it("records statutory CASH_RECEIVED once after the second immediate revalidation", async () => {
    const getBridge = renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByRole("heading", { name: "Complete transaction" })).toBeInTheDocument();
    const readback = await getBridge().readTenderByParkingSession("statutory-readback", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001");
    expect(readback.ok).toBe(true);
    if (!readback.ok) throw new Error(readback.error.message);
    expect(readback.payload.tender?.statutoryDiscountDecisionCommandId).toBe("77777777-7777-4777-8777-777777770777");
    expect(readback.payload.tender?.statutoryDiscountPayableBasisApplicationCommandId).toBe("88888888-8888-4888-8888-888888880001");
    expect(readback.payload.tender?.statutoryFinalAmountMinorUnits).toBe(10000);
    expect(readback.payload.tender?.statutoryImmediateRevalidationOutcome).toBe("PASSED_UNCHANGED");
  });

  it("blocks statutory cash when evidence readiness fails before entry or immediate custody", async () => {
    const scanPending = evidenceBridgeSuccess("SCAN_PENDING", false);
    renderSmoke(undefined, async () => scanPending);

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Evidence security scanning is pending.")).length).toBeGreaterThan(0);
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
  });

  it("does not write CASH_RECEIVED when evidence changes after cash entry opens", async () => {
    renderSmoke(undefined, async () => evidenceBridgeSuccess("SCAN_PENDING", false));

    await userEvent.click(screen.getByRole("button", { name: "Statutory CASH_RECEIVED recorded once" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Evidence security scanning is pending.")).length).toBeGreaterThan(0);
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
  });

  it("keeps second-stage revalidation failures before statutory CASH_RECEIVED", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received revalidation AMOUNT_CHANGED" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("77777777-7777-4777-8777-777777770777");
    expect(document.body.textContent).not.toContain("88888888-8888-4888-8777-777777770777");
    expect(document.body.textContent).not.toContain("88888888-8888-4888-8888-888888880001");
    expect(document.body.textContent).toContain("100.00");
    expect(document.body.textContent).toContain("125.00");
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received retryable failure" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByText("Central PMS could not revalidate the statutory payable basis. Try again before accepting cash.")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Immediate Record Cash Received terminal failure" }));
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect((await screen.findAllByText("Parking session is already paid.")).length).toBeGreaterThan(0);
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("statutory-tender-evidence")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Central PMS canonical payment" })).not.toBeInTheDocument();
  });

  it("restores statutory CASH_RECEIVED custody evidence after restart", async () => {
    const getBridge = renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after statutory CASH_RECEIVED preserves custody evidence" }));

    expect(await screen.findByRole("heading", { name: "Complete transaction" })).toBeInTheDocument();
    const readback = await getBridge().readTenderByParkingSession("restart-readback", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001");
    expect(readback.ok).toBe(true);
    if (!readback.ok) throw new Error(readback.error.message);
    expect(readback.payload.tender?.statutoryDiscountDecisionCommandId).toBe("77777777-7777-4777-8777-777777770777");
    expect(readback.payload.tender?.statutoryDiscountPayableBasisApplicationCommandId).toBe("88888888-8888-4888-8888-888888880001");
    expect(readback.payload.tender?.statutoryFinalAmountMinorUnits).toBe(10000);
    expect(readback.payload.tender?.statutoryImmediateRevalidationOutcome).toBe("PASSED_UNCHANGED");
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("restores statutory CASH_RECEIVED and shows terminal-cash submission recovery", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after statutory CASH_RECEIVED resumes terminal-cash submission" }));

    expect(await screen.findByRole("heading", { name: "Complete transaction" }, { timeout: 5000 })).toBeInTheDocument();
    await userEvent.click(screen.getByText("Transaction support details"));
    expect(await screen.findByRole("heading", { name: "Central PMS canonical payment" }, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByText(/Submitting cash payment/, {}, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Submit / Check Central PMS" }, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("renders applied statutory summary and enables direct cash recording when prerequisites pass", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Applied complete" }));

    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Approved");
    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("100.00");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Continue to Cash" })).not.toBeInTheDocument();
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

    await screen.findByRole("heading", { name: "Parking session details" });
    expect(revalidated).toHaveLength(1);
    expect(revalidated[0].statutoryDiscountReadiness?.statutoryDiscountDecisionCommandId).toBe("77777777-7777-4777-8777-777777770777");
    expect(revalidated[0].tariffSnapshotId).toBe("99999999-9999-4999-8999-999999990001");
    expect(revalidated[0].authoritativeAmountMinorUnits).toBe(10000);

    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Approved");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
  });

  it("shows restart scenarios as restored and still pre-CASH_RECEIVED", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart during application processing" }));

    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
  });

  it("restores applied amount changes with acknowledgement still pending", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart after applied amount change" }));

    expect(screen.getByRole("heading", { name: "Parking fee changed before cash acceptance" })).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getByText("Authoritative applied amount")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge new amount" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Payment recorded" })).not.toBeInTheDocument();
  });

  it("uses concise state-specific statutory summaries", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Awaiting review" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");

    await userEvent.click(screen.getByRole("button", { name: "Approved, application not requested" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");

    await userEvent.click(screen.getByRole("button", { name: "Application processing" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");

    await userEvent.click(screen.getByRole("button", { name: "Rejected" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Rejected");
    expect(screen.getByText("Reason").parentElement).toHaveTextContent("not approved");

    await userEvent.click(screen.getByRole("button", { name: "Terminal failure" }));
    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");
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
