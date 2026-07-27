import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App, TerminalShell } from "./App";
import {
  createPayableBasisVisualSmokeBridge,
  PayableBasisVisualSmokeShell,
  payableBasisVisualSmokeScenarios,
  shouldUsePayableBasisVisualSmoke,
} from "./PayableBasisVisualSmoke";
import type { AptConfig } from "./config";
import { mode1Config, rawMode1Config } from "./test/testConfig";

function enabledConfig(): AptConfig {
  return {
    ...mode1Config(),
    nonLiveCashCaptureEnabled: true,
    centralPmsConnectionMode: "mock",
  };
}

function renderSmoke() {
  const bridge = createPayableBasisVisualSmokeBridge(`payable-basis-test:${crypto.randomUUID()}`);
  render(
    <PayableBasisVisualSmokeShell
      config={enabledConfig()}
      bridge={bridge}
      renderTerminalShell={({ scenario, bridge: scenarioBridge, restorePayableBasisOnMount, renderKey }) => (
        <TerminalShell
          key={renderKey}
          config={scenario.config}
          client={scenario.client}
          localJournalBridge={scenarioBridge}
          initialReferenceType={scenario.referenceType}
          initialReferenceValue={scenario.referenceValue}
          restorePayableBasisOnMount={restorePayableBasisOnMount}
        />
      )}
    />,
  );
  return bridge;
}

async function resolveSelectedReference() {
  await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
  await screen.findByText("Authoritative payable basis");
}

describe("PayableBasisVisualSmokeShell", () => {
  it("is development-only and mounted by the payable-basis query flag", async () => {
    expect(shouldUsePayableBasisVisualSmoke("?payableBasisVisualSmoke=1", true)).toBe(true);
    expect(shouldUsePayableBasisVisualSmoke("?payableBasisVisualSmoke=1", false)).toBe(false);
    expect(shouldUsePayableBasisVisualSmoke("?receiptVisualSmoke=1", true)).toBe(false);

    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", "/?payableBasisVisualSmoke=1");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Payable Basis Visual Smoke" })).toBeInTheDocument();
    expect(screen.getByText("Development-only")).toBeInTheDocument();
    expect(screen.getByLabelText("Payable basis visual smoke scenarios")).toBeInTheDocument();
  });

  it("exposes every required pre-cash scenario as an interactive button", () => {
    renderSmoke();

    for (const scenario of payableBasisVisualSmokeScenarios) {
      expect(screen.getByRole("button", { name: scenario.label })).toBeInTheDocument();
    }

    expect(screen.getByRole("heading", { name: "Payable Basis Visual Smoke" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Receipt Visual Smoke" })).not.toBeInTheDocument();
  });

  it("starts payable-basis scenarios before CASH_RECEIVED and before local custody", () => {
    renderSmoke();

    expect(screen.getByText(/Pre-cash fixture state: no local cash-custody record/)).toBeInTheDocument();
    expect(screen.getByLabelText("Ticket reference")).toHaveValue("APT-ACTIVE-1001");
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("Cash received locally");
  });

  it("uses the real terminal lookup and readiness components for ticket and plate ready states", async () => {
    renderSmoke();

    await resolveSelectedReference();
    expect(screen.getByText("Ready for cash acceptance")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeEnabled();

    await userEvent.click(screen.getByRole("button", { name: "Plate ready for cash" }));
    expect(screen.getByLabelText("Plate number")).toHaveValue("PLATE-READY-1002");
    await resolveSelectedReference();
    expect(screen.getByText("PLATE-READY-1002")).toBeInTheDocument();
    expect(screen.getByText("Ready for cash acceptance")).toBeInTheDocument();
  });

  it("keeps blocked readiness pre-cash and disables Continue to Cash", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Fiscal readiness blocked" }));
    await resolveSelectedReference();

    expect(screen.getByText("Cash acceptance blocked")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("runs PASSED_UNCHANGED revalidation before exposing the local cash workflow", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Revalidation passed unchanged" }));
    await resolveSelectedReference();
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect(await screen.findByLabelText("Non-live cash custody capture")).toBeInTheDocument();
    expect(screen.getAllByText("Revalidation passed unchanged").length).toBeGreaterThan(1);
    expect(screen.getByText("Cash has not yet been recorded at this terminal.")).toBeInTheDocument();
    expect(screen.getByText(/Complete denomination entry and attest physical receipt before recording CASH_RECEIVED/)).toBeInTheDocument();
    expect(screen.queryByText(/State at local cash capture:/)).not.toBeInTheDocument();
    expect(screen.getByLabelText(/I attest/)).not.toBeChecked();
    expect(document.body).not.toHaveTextContent("Local state: CashReceived");
  });

  it("keeps AMOUNT_CHANGED pre-cash and requires acknowledgement", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Revalidation amount changed" }));
    await resolveSelectedReference();
    await userEvent.click(screen.getByRole("button", { name: "Continue to Cash" }));

    expect(await screen.findByText("Parking fee changed before cash acceptance")).toBeInTheDocument();
    expect(screen.getByText("Previous amount")).toBeInTheDocument();
    expect(screen.getByText("New amount")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge new amount" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("restores the persisted payable basis before cash acceptance after simulated restart", async () => {
    renderSmoke();

    await userEvent.click(screen.getByRole("button", { name: "Restart recovery before cash acceptance" }));
    await resolveSelectedReference();
    await userEvent.click(screen.getByRole("button", { name: "Simulate restart" }));

    await waitFor(() => expect(screen.getByText("Previously resolved")).toBeInTheDocument());
    expect(screen.getByText(/revalidation before local cash custody/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });
});