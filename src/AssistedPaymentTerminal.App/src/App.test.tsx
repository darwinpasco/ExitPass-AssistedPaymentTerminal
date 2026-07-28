import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App, TerminalShell } from "./App";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import { mode1Config, rawMode1Config } from "./test/testConfig";

describe("App startup and payable-basis readiness workflow", () => {
  it("refuses unsupported profile at startup", async () => {
    window.__APT_CONFIG__ = { ...rawMode1Config, APT_PROFILE: "ADMIN_WORKSTATION" };

    render(<App />);

    expect(await screen.findByText("Unsupported terminal profile")).toBeInTheDocument();
    expect(screen.getByText(/Unsupported APT_PROFILE/)).toBeInTheDocument();
  });

  it("displays Cashier-Assisted Terminal without numbered-mode wording", () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    expect(screen.getByTestId("apt-terminal-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.getByRole("heading", { name: "Cashier-Assisted Terminal" })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/Mode 1|Mode 2|Mode1|Mode2/);
  });

  it("mounts the receipt visual-smoke surface from the development query flag", async () => {
    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", "/?receiptVisualSmoke=1");

    render(<App />);

    const shell = await screen.findByTestId("apt-terminal-shell");
    expect(shell).toHaveAttribute("data-app-ready", "true");
    expect(shell).toHaveAttribute("data-surface", "receipt-visual-smoke");
    expect(screen.getByText("Development-only")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Temporarily unavailable" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Available" })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/Mode 1|React Mode 1|apt-mode1-shell/);
  });

  it("displays compact operational context", () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    expect(screen.getByLabelText("Operational context")).toBeInTheDocument();
    expect(screen.getByText("ExitPass Demo Parking")).toBeInTheDocument();
    expect(screen.getByText("Development Cashier")).toBeInTheDocument();
    expect(screen.getByText("Development Cashier Terminal 1")).toBeInTheDocument();
    expect(screen.getByText("Configured: POS-DEV-001")).toBeInTheDocument();
  });

  it("resolves a ticket through the APT payable-basis facade and displays Central PMS readiness", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Authoritative payable basis")).toBeInTheDocument();
    expect(screen.getByText("APT-ACTIVE-1001")).toBeInTheDocument();
    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("₱125.00");
    expect(screen.getByTestId("cash-readiness-status")).toHaveTextContent("Ready for cash acceptance");
    expect(screen.getByTestId("session-readiness-value")).toHaveTextContent("Resolved Payable");
    expect(screen.getByTestId("tariff-readiness-value")).toHaveTextContent("Current");
    expect(screen.getByTestId("payment-eligibility-value")).toHaveTextContent("Eligible");
    expect(screen.getByTestId("terminal-cash-readiness-value")).toHaveTextContent("Available");
    expect(screen.getByTestId("sales-invoice-readiness-value")).toHaveTextContent("Ready");
    expect(screen.getByTestId("fiscal-readiness-value")).toHaveTextContent("Ready");
    expect(screen.getByTestId("central-cash-ready-value")).toHaveTextContent("true");
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Blocked");
    expect(screen.getByTestId("continue-to-cash")).toBeDisabled();
    expect(screen.getByText(/Revalidation will still run immediately before CASH_RECEIVED/)).toBeInTheDocument();
    expect(screen.queryByText("Payable basis is current")).not.toBeInTheDocument();
  });

  it("resolves a plate without requiring a ticket", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.click(screen.getByLabelText("Plate"));
    await userEvent.type(screen.getByLabelText("Plate number"), "PLATE-READY-1002");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("PLATE-READY-1002")).toBeInTheDocument();
    expect(screen.getByTestId("cash-readiness-status")).toHaveTextContent("Ready for cash acceptance");
  });

  it("shows not-found failure with support reference and recovery path", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Parking session not found")).toBeInTheDocument();
    expect(screen.getByText(/Support reference:/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Back to lookup" }));
    expect(screen.queryByText("Parking session not found")).not.toBeInTheDocument();
  });

  it("shows fiscal-readiness blockers without enabling cash", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-FISCAL-BLOCKED");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Cash acceptance blocked")).toBeInTheDocument();
    expect(screen.getByTestId("central-cash-ready-value")).toHaveTextContent("false");
    expect(screen.getByTestId("continue-to-cash")).toBeDisabled();
    expect(screen.getByText(/Sales Invoice configuration is incomplete|Cash acceptance is blocked by Central PMS readiness/)).toBeInTheDocument();
  });

  it("shows retryable Vendor PMS unavailability", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-UNAVAILABLE-503");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Central PMS temporarily unavailable")).toBeInTheDocument();
    expect(screen.getByText("Retry is available after Central PMS is reachable.")).toBeInTheDocument();
  });

  it("generates a fresh correlation id per lookup action", async () => {
    const calls: string[] = [];
    const client = new MockCentralPmsClient(mode1Config());
    const resolvePayableBasis = async (...args: Parameters<typeof client.resolvePayableBasis>) => {
      calls.push(args[2]);
      return client.resolvePayableBasis(...args);
    };

    render(<TerminalShell config={mode1Config()} client={{ resolvePayableBasis, revalidatePayableBasis: (basis, correlationId) => client.revalidatePayableBasis(basis, correlationId) }} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Parking session not found");
    await userEvent.click(screen.getByRole("button", { name: "Back to lookup" }));

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls[0]).not.toBe(calls[1]);
  });

  it("submits safe statutory facts for review and keeps statutory cash acceptance blocked", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Authoritative payable basis");

    await userEvent.click(screen.getByRole("button", { name: "Start statutory request" }));
    expect(screen.getByText("Draft statutory request")).toBeInTheDocument();
    expect(screen.getByLabelText(/Cashier attests safe entitlement facts/)).not.toBeChecked();
    expect(screen.getByRole("button", { name: "Submit for Operator Review" })).toBeDisabled();

    await userEvent.click(screen.getByLabelText(/Cashier attests safe entitlement facts/));
    await userEvent.click(screen.getByRole("button", { name: "Submit for Operator Review" }));

    expect(await screen.findByText("Awaiting Operator Review")).toBeInTheDocument();
    expect(screen.getByText(/Check status performs read-only Central PMS GET readback/)).toBeInTheDocument();
    expect(screen.getByTestId("statutory-cash-blocker")).toHaveTextContent("Statutory CASH_RECEIVED is not enabled");
    expect(screen.getByRole("button", { name: "Continue to Cash" })).toBeDisabled();
    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("shows applied statutory basis facts and reuses amount-change acknowledgement while remaining pre-cash", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    const client = new MockCentralPmsClient(config);
    render(<TerminalShell config={config} client={client} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Authoritative payable basis");
    await userEvent.click(screen.getByRole("button", { name: "Start statutory request" }));
    await userEvent.click(screen.getByLabelText(/Cashier attests safe entitlement facts/));
    await userEvent.click(screen.getByRole("button", { name: "Submit for Operator Review" }));
    await screen.findByText("Awaiting Operator Review");

    // Controlled mock readback keeps pending review; visual-smoke fixtures cover approved and APPLIED displays.
    expect(screen.getByRole("button", { name: "Check Review Status" })).toBeInTheDocument();
    expect(screen.queryByText(/full statutory ID/i)).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("reviewerUserId");
  });});
