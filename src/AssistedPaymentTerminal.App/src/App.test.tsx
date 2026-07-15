import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { App, TerminalShell } from "./App";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import { mode1Config, rawMode1Config } from "./test/testConfig";

describe("App startup and terminal workflow", () => {
  it("refuses unsupported profile at startup", async () => {
    window.__APT_CONFIG__ = { ...rawMode1Config, APT_PROFILE: "ADMIN_WORKSTATION" };

    render(<App />);

    expect(await screen.findByText("Unsupported terminal profile")).toBeInTheDocument();
    expect(screen.getByText(/Unsupported APT_PROFILE/)).toBeInTheDocument();
  });

  it("displays Cashier-Assisted Terminal without numbered-mode wording", () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    expect(screen.getByTestId("apt-mode1-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.getByRole("heading", { name: "Cashier-Assisted Terminal" })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/Mode 1|Mode 2|Mode1|Mode2/);
  });

  it("displays compact operational context", () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    expect(screen.getByLabelText("Operational context")).toBeInTheDocument();
    expect(screen.getByText("Site")).toBeInTheDocument();
    expect(screen.getByText("ExitPass Demo Parking")).toBeInTheDocument();
    expect(screen.getByText("Cashier")).toBeInTheDocument();
    expect(screen.getByText("Development Cashier")).toBeInTheDocument();
    expect(screen.getByText("Shift")).toBeInTheDocument();
    expect(screen.getByText("OPEN")).toBeInTheDocument();
    expect(screen.getByText("Terminal")).toBeInTheDocument();
    expect(screen.getByText("Development Cashier Terminal 1")).toBeInTheDocument();
    expect(screen.getByText("POS readiness")).toBeInTheDocument();
    expect(screen.getByText("Configured: POS-DEV-001")).toBeInTheDocument();
  });

  it("collapses and expands terminal technical details", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    const details = screen.getByText("Terminal details").closest("details");
    expect(details).not.toHaveAttribute("open");

    await userEvent.click(screen.getByText("Terminal details"));
    expect(details).toHaveAttribute("open");
    expect(screen.getByText("APT-DEV-001")).toBeInTheDocument();
    expect(screen.getByText("POS-DEV-001")).toBeInTheDocument();
    expect(screen.getByText("SHIFT-DEV-20260714-A")).toBeInTheDocument();

    await userEvent.click(screen.getByText("Terminal details"));
    expect(details).not.toHaveAttribute("open");
  });

  it("resolves a valid ticket and keeps payment stage disabled", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Authoritative payable basis")).toBeInTheDocument();
    expect(screen.getByText("APT-ACTIVE-1001")).toBeInTheDocument();
    expect(screen.getByText("Payable basis is current")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Collect payment" })).toBeDisabled();
  });

  it("shows ticket-not-found failure with support reference and recovery path", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Ticket not found")).toBeInTheDocument();
    expect(screen.getByText(/Support reference:/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Back to ticket lookup" }));
    expect(screen.queryByText("Ticket not found")).not.toBeInTheDocument();
  });

  it("shows inactive session failure", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-INACTIVE-3001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Session inactive or invalid")).toBeInTheDocument();
  });

  it("shows service unavailable failure", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-UNAVAILABLE-503");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Central PMS unavailable")).toBeInTheDocument();
    expect(screen.getByText(/Support reference:/)).toBeInTheDocument();
  });

  it("shows malformed response failure", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-MALFORMED-502");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Malformed Central PMS response")).toBeInTheDocument();
  });

  it("blocks payment for expired tariff and recalculates through mock", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-EXPIRED-2001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Tariff expired")).toBeInTheDocument();
    expect(screen.getByText("Blocked by expired tariff")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Collect payment" })).toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "Recalculate Fee" }));

    expect(await screen.findByText("Payable basis is current")).toBeInTheDocument();
    expect(screen.getByText("Recalculated")).toBeInTheDocument();
  });

  it("shows recalculation failure with support reference", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-RECALC-FAIL");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    expect(await screen.findByText("Tariff expired")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Recalculate Fee" }));

    expect(await screen.findByText("Central PMS unavailable")).toBeInTheDocument();
    expect(screen.getByText(/Support reference:/)).toBeInTheDocument();
  });

  it("generates a fresh correlation id per lookup action", async () => {
    const resolveTicket = vi.fn(async (_ticket: string, correlationId: string) => ({
      ok: false as const,
      kind: "not_found" as const,
      error: {
        errorCode: "SESSION_NOT_FOUND",
        message: "Vendor parking session was not found.",
        correlationId,
        retryable: false,
      },
    }));

    render(<TerminalShell config={mode1Config()} client={{ resolveTicket, recalculateFee: resolveTicket }} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Ticket not found");
    await userEvent.click(screen.getByRole("button", { name: "Back to ticket lookup" }));

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    await waitFor(() => expect(resolveTicket).toHaveBeenCalledTimes(2));
    expect(resolveTicket.mock.calls[0][1]).not.toBe(resolveTicket.mock.calls[1][1]);
  });
});
