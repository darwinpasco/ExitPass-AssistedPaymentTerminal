import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App } from "./App";
import {
  TransactionCompletionVisualSmokeShell,
  transactionCompletionVisualSmokeScenarios,
  shouldUseTransactionCompletionVisualSmoke,
} from "./TransactionCompletionVisualSmoke";
import { rawMode1Config, mode1Config } from "./test/testConfig";

describe("TransactionCompletionVisualSmokeShell", () => {
  it("is gated to development mode and an explicit query flag", () => {
    expect(shouldUseTransactionCompletionVisualSmoke("?transactionCompletionVisualSmoke=1", true)).toBe(true);
    expect(shouldUseTransactionCompletionVisualSmoke("?transactionCompletionVisualSmoke=1", false)).toBe(false);
    expect(shouldUseTransactionCompletionVisualSmoke("", true)).toBe(false);
  });

  it("mounts from the dedicated app query flag", async () => {
    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", "/?transactionCompletionVisualSmoke=1");

    render(<App />);

    const shell = await screen.findByTestId("apt-terminal-shell");
    expect(shell).toHaveAttribute("data-app-ready", "true");
    expect(shell).toHaveAttribute("data-surface", "transaction-completion-visual-smoke");
    expect(screen.getByRole("heading", { name: "Transaction Completion Visual Smoke" })).toBeInTheDocument();
    expect(screen.getByText("Development-only")).toBeInTheDocument();
  });

  it("does not mount the transaction-completion scenarios when the query flag is not accepted", async () => {
    expect(shouldUseTransactionCompletionVisualSmoke("?transactionCompletionVisualSmoke=1", false)).toBe(false);

    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", "/");

    render(<App />);

    const shell = await screen.findByTestId("apt-terminal-shell");
    expect(shell).toHaveAttribute("data-app-ready", "true");
    expect(shell).not.toHaveAttribute("data-surface", "transaction-completion-visual-smoke");
    expect(screen.getByRole("heading", { name: "Cashier-Assisted Terminal" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Transaction Completion Visual Smoke" })).not.toBeInTheDocument();
  });

  it("offers the required transaction-completion scenarios separately from receipt visual smoke", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    const shell = await screen.findByTestId("apt-terminal-shell");
    expect(shell).toHaveAttribute("data-app-ready", "true");
    expect(shell).toHaveAttribute("data-surface", "transaction-completion-visual-smoke");
    expect(await screen.findByLabelText("Transaction completion visual smoke scenarios")).toBeInTheDocument();
    for (const scenario of transactionCompletionVisualSmokeScenarios) {
      expect(screen.getByRole("button", { name: scenario.label })).toBeInTheDocument();
    }

    expect(screen.queryByRole("button", { name: "Temporarily unavailable" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Available" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "ExitAuthorization pending" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "ExitAuthorization available" })).not.toBeInTheDocument();
    expect(screen.getByText(/no APT-usable Central PMS ExitAuthorization readback contract is present/i)).toBeInTheDocument();
  });

  it("uses the production cash and state-machine panels from a CASH_RECEIVED boundary", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    expect(await screen.findByLabelText("Non-live cash custody capture")).toBeInTheDocument();
    expect(screen.getByText("Local cash custody: cash received locally.")).toBeInTheDocument();
    expect(screen.getByLabelText("Cashier transaction state")).toBeInTheDocument();
    expect(screen.getByTestId("terminal-cash-submission-state")).toHaveTextContent("Terminal Cash Not Submitted");
    expect(screen.getByTestId("payment-finality-state")).toHaveTextContent("Payment Finality Pending");
    expect(screen.queryByText("CASH_RECEIVED has not occurred.")).not.toBeInTheDocument();
  });

  it("keeps receipt-available completion blocked by the missing ExitAuthorization readback contract", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    await userEvent.click(screen.getByRole("button", { name: "Receipt available" }));

    const statePanel = within(await screen.findByLabelText("Cashier transaction state"));
    await waitFor(() => expect(statePanel.getByTestId("receipt-presentation-state")).toHaveTextContent("Receipt Available"));
    expect(statePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction Requires Support");
    expect(statePanel.getByText("Exit Authorization Readback Contract Missing")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("Transaction complete");
  });

  it("maps payment-finality pending as accepted submission and transaction in progress", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    await userEvent.click(screen.getByRole("button", { name: "Payment finality pending" }));

    const statePanel = within(await screen.findByLabelText("Cashier transaction state"));
    await waitFor(() => expect(statePanel.getByTestId("terminal-cash-submission-state")).toHaveTextContent("Terminal Cash Submission Accepted"));
    expect(statePanel.getByTestId("payment-finality-state")).toHaveTextContent("Payment Finality Pending");
    expect(statePanel.getByTestId("fiscal-issuance-state")).toHaveTextContent("Fiscal Not Started");
    expect(statePanel.getByTestId("receipt-presentation-state")).toHaveTextContent("Receipt Not Requested");
    expect(statePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction In Progress");
    expect(screen.getByRole("button", { name: "Check Payment Status" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Submit / Check Central PMS" })).not.toBeInTheDocument();
  });

  it("restores restart-during-payment-pending as accepted pending readback without retry classification", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    await userEvent.click(screen.getByRole("button", { name: "Restart during payment pending" }));

    const statePanel = within(await screen.findByLabelText("Cashier transaction state"));
    await waitFor(() => expect(statePanel.getByTestId("terminal-cash-submission-state")).toHaveTextContent("Terminal Cash Submission Accepted"));
    expect(statePanel.getByTestId("payment-finality-state")).toHaveTextContent("Payment Finality Pending");
    expect(statePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction In Progress");
    expect(screen.getByText(/Restart preserves pending payment readback and the same tender identity/i)).toBeInTheDocument();
    expect(screen.getAllByText("eeeeeeee-eeee-4eee-8eee-eeeeeeee9001").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Check Payment Status" })).toBeInTheDocument();
  });

  it("distinguishes terminal support failures from retryable and pending stages", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    await userEvent.click(screen.getByRole("button", { name: "Terminal payment failure" }));

    const statePanel = within(await screen.findByLabelText("Cashier transaction state"));
    await waitFor(() => expect(statePanel.getByTestId("terminal-cash-submission-state")).toHaveTextContent("Terminal Cash Submission Failed"));
    expect(statePanel.getByTestId("payment-finality-state")).toHaveTextContent("Payment Finality Failed");
    expect(statePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction Requires Support");

    await userEvent.click(screen.getByRole("button", { name: "Terminal-cash submission retryable" }));

    const retryStatePanel = within(await screen.findByLabelText("Cashier transaction state"));
    await waitFor(() => expect(retryStatePanel.getByTestId("terminal-cash-submission-state")).toHaveTextContent("Terminal Cash Submission Retryable"));
    expect(retryStatePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction Requires Retry");
  });

  it("renders restart-recovery scenarios with the same CASH_RECEIVED tender identity", async () => {
    render(<TransactionCompletionVisualSmokeShell config={mode1Config()} />);

    await userEvent.click(screen.getByRole("button", { name: "Restart during fiscal pending" }));

    const statePanel = within(await screen.findByLabelText("Cashier transaction state"));
    expect(screen.getAllByText("eeeeeeee-eeee-4eee-8eee-eeeeeeee9001").length).toBeGreaterThan(0);
    await waitFor(() => expect(statePanel.getByTestId("fiscal-issuance-state")).toHaveTextContent("Fiscal Pending"));
    expect(statePanel.getByTestId("cashier-completion-state")).toHaveTextContent("Transaction In Progress");
    expect(screen.getByText(/Restart preserves payment finality and pending fiscal state/i)).toBeInTheDocument();
  });
});
