import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { App, TerminalShell } from "./App";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import type { StatutoryOrdinanceAvailabilityResult } from "./api/centralPmsTypes";
import type { LocalJournalBridge, LocalJournalHealth, LocalOperationalContext, PayableBasisStateSnapshot } from "./localJournalBridge";
import { mode1Config, rawMode1Config } from "./test/testConfig";
import { containsInternalGuid } from "./cashierSafeReferences";

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
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} localJournalBridge={bridgeWithLocalState()} />);

    expect(screen.getByLabelText("Operational context")).toBeInTheDocument();
    expect(screen.getByTestId("operational-site-summary")).toHaveTextContent("ExitPass Demo Parking");
    expect(screen.getByTestId("operational-cashier-summary")).toHaveTextContent("Not signed in");
    expect(screen.getByText("No active shift")).toBeInTheDocument();
    expect(screen.getByTestId("operational-terminal-summary")).toHaveTextContent("Development Cashier Terminal 1");
    expect(screen.getByTestId("operational-pos-readiness-summary")).toHaveTextContent("Configured");
  });

  it("does not allow configuration to fabricate cashier or shift authority on a fresh local database", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} localJournalBridge={bridgeWithLocalState()} />);

    expect(await screen.findByText("No active shift")).toBeInTheDocument();
    expect(screen.getByTestId("operational-cashier-summary")).toHaveTextContent("Not signed in");
    expect(screen.queryByText("Configured shift posture")).not.toBeInTheDocument();
  });

  it("resolves a ticket through the APT payable-basis facade and displays Central PMS readiness", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} localJournalBridge={bridgeWithLocalState()} />);

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

  it("keeps cash blocked when a durable shift exists without active cash custody", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState({ activeShift: true })} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Authoritative payable basis")).toBeInTheDocument();
    expect(screen.getAllByText("OPEN").length).toBeGreaterThan(0);
    expect(screen.getByText("No active cash-custody session is recorded in local recovery state.")).toBeInTheDocument();
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Blocked");
    expect(screen.getByTestId("continue-to-cash")).toBeDisabled();
  });

  it("displays durable active shift recovery without using a configured shift filter", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    let requestedContext: LocalOperationalContext | undefined;
    const postMessage = vi.fn();
    window.chrome = {
      webview: {
        postMessage,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      },
    };
    render(
      <TerminalShell
        config={config}
        client={new MockCentralPmsClient(config)}
        localJournalBridge={bridgeWithLocalState({
          activeShift: true,
          onHealthContext: (context) => {
            requestedContext = context;
          },
          hideRecoveredStateWhenShiftFilterPresent: true,
        })}
      />,
    );

    await waitFor(() => expect(requestedContext).toBeDefined());

    expect(requestedContext).not.toHaveProperty("cashierShiftId");
    expect(screen.getAllByText("OPEN").length).toBeGreaterThan(0);
    expect(screen.queryByText("No active shift")).not.toBeInTheDocument();
    expect(screen.getByText("Recovered shift")).toBeInTheDocument();
    expect(screen.getByTestId("recovered-shift-id")).toHaveTextContent("Open");

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Authoritative payable basis")).toBeInTheDocument();
    expect(screen.getByText("No active cash-custody session is recorded in local recovery state.")).toBeInTheDocument();
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Blocked");
    expect(screen.getByTestId("continue-to-cash")).toBeDisabled();

    const diagnostic = postMessage.mock.calls
      .map(([message]) => JSON.parse(message as string))
      .find((message) => message.source === "apt-manual-proof-diagnostic");
    expect(diagnostic).toMatchObject({
      shiftFilterSent: false,
      bridgeReturnedActiveShiftId: "SHIFT-DEV-20260714-A",
      bridgeReturnedActiveShiftStatus: "Open",
      reactReceivedActiveShiftId: "SHIFT-DEV-20260714-A",
      reactReceivedActiveShiftStatus: "Open",
      reactRenderedShiftLabel: "OPEN",
      activeCustodyId: null,
      cashBlockedWithoutCustody: true,
    });
    expect(diagnostic.bridgeRequestScope).not.toHaveProperty("cashierShiftId");
    window.chrome = undefined;
  });

  it("allows local prerequisites to pass only when durable shift and custody are recovered", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState({ activeShift: true, activeCustody: true })} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Authoritative payable basis")).toBeInTheDocument();
    expect(screen.getByTestId("central-cash-ready-value")).toHaveTextContent("true");
    expect(screen.getByTestId("local-cash-prerequisites-value")).toHaveTextContent("Satisfied");
    expect(screen.getByTestId("continue-to-cash")).toBeEnabled();
  });

  it("resolves a plate without requiring a ticket", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.click(screen.getByLabelText("Plate"));
    await userEvent.type(screen.getByLabelText("Plate number"), "PLATE-READY-1002");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("PLATE-READY-1002")).toBeInTheDocument();
    expect(screen.getByTestId("cash-readiness-status")).toHaveTextContent("Ready for cash acceptance");
  });

  it("shows a safe not-found failure and recovery path without exposing the diagnostic correlation id", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Parking session not found")).toBeInTheDocument();
    expect(screen.queryByText(/Support reference:/)).not.toBeInTheDocument();
    expect(containsInternalGuid(document.body.textContent ?? "")).toBe(false);
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
    expect(containsInternalGuid(document.body.textContent ?? "")).toBe(false);
  });

  it("submits safe statutory facts for review and keeps pending-review statutory cash blocked", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} />);

    await userEvent.type(screen.getByLabelText("Ticket reference"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Authoritative payable basis");

    await userEvent.click(await screen.findByRole("button", { name: "Start statutory request" }));
    expect(screen.getByText("Draft statutory request")).toBeInTheDocument();
    expect(screen.getByLabelText(/Cashier attests safe entitlement facts/)).not.toBeChecked();
    expect(screen.getByRole("button", { name: "Submit for Operator Review" })).toBeDisabled();

    await userEvent.type(screen.getByRole("textbox", { name: "Statutory ID" }), "AB1234567890");
    await userEvent.tab();
    expect(screen.getByRole("textbox", { name: "Statutory ID" })).toHaveValue("AB******7890");
    expect(document.body).not.toHaveTextContent("AB1234567890");
    await userEvent.click(screen.getByLabelText(/Cashier attests safe entitlement facts/));
    await userEvent.click(screen.getByRole("button", { name: "Submit for Operator Review" }));

    expect(await screen.findByText("Awaiting Operator Review")).toBeInTheDocument();
    expect(screen.getByText(/Check status performs read-only Central PMS GET readback/)).toBeInTheDocument();
    expect(screen.getByTestId("statutory-cash-blocker")).toHaveTextContent("Statutory cash remains blocked until approval");
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
    await userEvent.click(await screen.findByRole("button", { name: "Start statutory request" }));
    await userEvent.type(screen.getByRole("textbox", { name: "Statutory ID" }), "123456789012");
    await userEvent.tab();
    expect(screen.getByRole("textbox", { name: "Statutory ID" })).toHaveValue("12******9012");
    expect(document.body).not.toHaveTextContent("123456789012");
    await userEvent.click(screen.getByLabelText(/Cashier attests safe entitlement facts/));
    await userEvent.click(screen.getByRole("button", { name: "Submit for Operator Review" }));
    await screen.findByText("Awaiting Operator Review");

    // Controlled mock readback keeps pending review; visual-smoke fixtures cover approved and APPLIED displays.
    expect(screen.getByRole("button", { name: "Check Review Status" })).toBeInTheDocument();
    expect(screen.queryByText(/full statutory ID/i)).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("reviewerUserId");
  });

  it("shows both authoritative entitlement options while keeping ordinary payment separate", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-ACTIVE-1001");

    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toHaveTextContent("Available");
    expect(screen.getByTestId("pwd-ordinance-availability")).toHaveTextContent("Available");
    expect(screen.getByRole("option", { name: "Senior citizen" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Person with disability" })).toBeInTheDocument();
    expect(screen.getByTestId("ordinary-payment-preserved")).toHaveTextContent("Ordinary payment remains available");
  });

  it("shows only Senior Citizen controls when PWD is not covered", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-SENIOR-ONLY");

    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toHaveTextContent("Available");
    expect(screen.getByTestId("pwd-ordinance-availability")).toHaveTextContent("Not Available");
    expect(screen.getByTestId("covered-entitlement-selector")).toHaveValue("SENIOR_CITIZEN");
    expect(screen.queryByRole("option", { name: "Person with disability" })).not.toBeInTheDocument();
  });

  it("shows only PWD controls when Senior Citizen is not covered", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-PWD-ONLY");

    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toHaveTextContent("Not Available");
    expect(screen.getByTestId("pwd-ordinance-availability")).toHaveTextContent("Available");
    expect(screen.getByTestId("covered-entitlement-selector")).toHaveValue("PWD");
    expect(screen.queryByRole("option", { name: "Senior citizen" })).not.toBeInTheDocument();
  });

  it("suppresses statutory initiation for authoritative no coverage and preserves ordinary payment", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-NO-ORDINANCE");

    expect(await screen.findByTestId("statutory-request-unavailable")).toHaveTextContent("No statutory request option is available");
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
    expect(screen.getByTestId("ordinary-payment-preserved")).toHaveTextContent("Ordinary payment remains available");
    expect(screen.getByTestId("continue-to-cash")).toBeDisabled();
  });

  it("keeps ordinary cash available when no ordinance is covered and independent readiness passes", async () => {
    const config = { ...mode1Config(), nonLiveCashCaptureEnabled: true };
    render(
      <TerminalShell
        config={config}
        client={new MockCentralPmsClient(config)}
        localJournalBridge={bridgeWithLocalState({ activeShift: true, activeCustody: true })}
      />,
    );

    await resolveTicket("APT-NO-ORDINANCE");

    expect(await screen.findByTestId("statutory-request-unavailable")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("continue-to-cash")).toBeEnabled());
  });

  it("discards a coverage response for another Site instead of enabling stale controls", async () => {
    const config = mode1Config();
    const client = new MockCentralPmsClient(config);
    const resolveAvailability = client.resolveStatutoryOrdinanceAvailability.bind(client);
    vi.spyOn(client, "resolveStatutoryOrdinanceAvailability").mockImplementation(async (...args): Promise<StatutoryOrdinanceAvailabilityResult> => {
      const result = await resolveAvailability(...args);
      return result.ok
        ? { ok: true, response: { ...result.response, siteId: "SITE-OTHER" } }
        : result;
    });
    render(<TerminalShell config={config} client={client} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-ACTIVE-1001");

    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toHaveTextContent("Malformed Authoritative State");
    expect(screen.getByTestId("pwd-ordinance-availability")).toHaveTextContent("Malformed Authoritative State");
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
  });

  it("distinguishes retryable source failure from no coverage", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState()} />);

    await resolveTicket("APT-ORDINANCE-UNAVAILABLE");

    expect(await screen.findByTestId("senior-citizen-ordinance-availability")).toHaveTextContent("Source Unavailable");
    expect(screen.getByRole("button", { name: "Retry ordinance availability" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Start statutory request" })).not.toBeInTheDocument();
    expect(screen.getByTestId("ordinary-payment-preserved")).toHaveTextContent("Ordinary payment remains available");
  });

  it("treats restored local availability as advisory and re-resolves both entitlements", async () => {
    const config = mode1Config();
    const client = new MockCentralPmsClient(config);
    const availabilitySpy = vi.spyOn(client, "resolveStatutoryOrdinanceAvailability");
    render(
      <TerminalShell
        config={config}
        client={client}
        localJournalBridge={bridgeWithLocalState({ latestPayableBasisState: restoredPayableBasisState() })}
      />,
    );

    expect(await screen.findByText("Recovered local state was advisory only. Central PMS was checked again after restart.")).toBeInTheDocument();
    expect(availabilitySpy).toHaveBeenCalledTimes(2);
    expect(availabilitySpy.mock.calls.map((call) => call[1]).sort()).toEqual(["PWD", "SENIOR_CITIZEN"]);
    expect(await screen.findByRole("button", { name: "Start statutory request" })).toBeEnabled();
  });
});

async function resolveTicket(reference: string) {
  await userEvent.type(screen.getByLabelText("Ticket reference"), reference);
  await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
  await screen.findByText("Authoritative payable basis");
}

function bridgeWithLocalState(options: {
  activeShift?: boolean;
  activeCustody?: boolean;
  onHealthContext?: (context?: LocalOperationalContext) => void;
  hideRecoveredStateWhenShiftFilterPresent?: boolean;
  latestPayableBasisState?: PayableBasisStateSnapshot;
} = {}): LocalJournalBridge {
  function healthForContext(context?: LocalOperationalContext): LocalJournalHealth {
    const configuredShiftFilterWouldHideRecovery = options.hideRecoveredStateWhenShiftFilterPresent && Boolean(context?.cashierShiftId);
    const includeActiveShift = Boolean(options.activeShift && !configuredShiftFilterWouldHideRecovery);
    const includeActiveCustody = Boolean(options.activeCustody && includeActiveShift);

    return {
      healthy: true,
      enabled: true,
      databasePath: "C:\\Users\\darwi\\AppData\\Local\\ExitPass\\AssistedPaymentTerminal\\ManualEncryptionProof\\LocalOperations\\cash-journal.db",
      cashDrawerEnabled: false,
      authorityWarning: "Local CASH_RECEIVED is terminal-local custody evidence only.",
      localPersistence: {
        encryptionConfigured: true,
        dpapiScope: "CurrentUser",
        keyEnvelopeExists: true,
        keyAvailable: true,
        databaseExists: true,
        databaseEncrypted: true,
        legacyPlaintextDetected: false,
        migrationRequired: false,
        integrityValidated: true,
        schemaReady: true,
        persistenceReady: true,
        recoveryAllowed: true,
        cashOperationsAllowed: true,
        safeStatus: "Ready",
        safeAction: "Local encrypted persistence is ready.",
        databasePath: "C:\\Users\\darwi\\AppData\\Local\\ExitPass\\AssistedPaymentTerminal\\ManualEncryptionProof\\LocalOperations\\cash-journal.db",
        keyEnvelopePath: "C:\\Users\\darwi\\AppData\\Local\\ExitPass\\AssistedPaymentTerminal\\ManualEncryptionProof\\LocalOperations\\cash-journal.key",
      },
      operationalState: {
        activeShiftRecordCount: includeActiveShift ? 1 : 0,
        activeCashCustodySessionRecordCount: includeActiveCustody ? 1 : 0,
        activeShift: includeActiveShift
          ? {
              id: "SHIFT-DEV-20260714-A",
              cashierId: "CASHIER-DEV-001",
              authenticatedCashierSessionReference: "dev-auth:CASHIER-DEV-001:SHIFT-DEV-20260714-A",
              terminalId: "APT-DEV-001",
              siteId: "11111111-1111-1111-1111-111111111111",
              siteGroupId: "22222222-2222-2222-2222-222222222222",
              posServerId: "POS-DEV-001",
              openedAt: "2026-07-15T00:00:00Z",
              closedAt: null,
              status: "Open",
            }
          : null,
        activeCashCustodySession: includeActiveCustody
          ? {
              id: "33333333-3333-4333-8333-333333333333",
              cashierId: "CASHIER-DEV-001",
              authenticatedCashierSessionReference: "dev-auth:CASHIER-DEV-001:SHIFT-DEV-20260714-A",
              cashierShiftId: "SHIFT-DEV-20260714-A",
              terminalId: "APT-DEV-001",
              siteId: "11111111-1111-1111-1111-111111111111",
              siteGroupId: "22222222-2222-2222-2222-222222222222",
              posServerId: "POS-DEV-001",
              openingCashAmount: 0,
              openedAt: "2026-07-15T00:01:00Z",
              status: "Open",
            }
          : null,
      },
    };
  }

  return {
    health: vi.fn(async (correlationId: string, context?: LocalOperationalContext) => {
      options.onHealthContext?.(context);
      return {
      ok: true,
      command: "localJournal.health",
      correlationId,
      payload: healthForContext(context),
    };
    }),
    getLatestPayableBasisState: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "payableBasisState.getLatest",
      correlationId,
      payload: options.latestPayableBasisState ?? null,
    })),
  } as unknown as LocalJournalBridge;
}

function restoredPayableBasisState(): PayableBasisStateSnapshot {
  const now = new Date().toISOString();
  return {
    id: "restored-ordinance-proof",
    localWorkflowId: "restored-ordinance-proof",
    lookupReferenceType: "ticket",
    lookupReferenceValue: "APT-ACTIVE-1001",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    siteId: "11111111-1111-1111-1111-111111111111",
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    sitePosServerId: "POS-DEV-001",
    terminalId: "APT-DEV-001",
    authoritativeAmountMinorUnits: 12500,
    currency: "PHP",
    tariffCalculatedAt: now,
    tariffValidUntil: new Date(Date.now() + 600000).toISOString(),
    feeValidUntil: new Date(Date.now() + 600000).toISOString(),
    parkingStatus: "Active",
    paymentStatus: "Unpaid",
    sessionReadiness: "RESOLVED_PAYABLE",
    tariffReadiness: "CURRENT",
    paymentEligibility: "ELIGIBLE",
    terminalCashAvailability: "AVAILABLE",
    fiscalReadiness: "READY",
    salesInvoiceConfigurationReadiness: "READY",
    cashAcceptanceReadiness: "READY",
    readyForCashAcceptance: true,
    blockingReasonCodes: [],
    retryable: false,
    safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
    centralPmsCorrelationId: "restored-basis-correlation",
    revalidationOutcome: null,
    cashierAcknowledgementRequired: false,
    amountChanged: false,
    priorDisplayedAmountMinorUnits: null,
    statutoryDiscountStateJson: JSON.stringify({
      status: "none",
      ordinanceAvailability: {
        authoritative: false,
        parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
        siteId: "11111111-1111-1111-1111-111111111111",
        siteGroupId: "22222222-2222-2222-2222-222222222222",
        recordedAt: "2026-08-02T00:00:00Z",
      },
    }),
    resolvedAt: now,
    lastRevalidatedAt: null,
    updatedAt: now,
  };
}
