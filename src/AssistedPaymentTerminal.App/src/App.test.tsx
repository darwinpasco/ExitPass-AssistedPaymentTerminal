import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { App, TerminalShell } from "./App";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import type { CentralPmsClient, PayableBasisResponse, ProjectedSessionResponse, StatutoryDiscountWorkflowState } from "./api/centralPmsTypes";
import type { LocalJournalBridge, LocalJournalHealth, LocalOperationalContext, PayableBasisStateSnapshot } from "./localJournalBridge";
import { mode1Config, rawMode1Config } from "./test/testConfig";
import { containsInternalGuid } from "./cashierSafeReferences";

describe("App cashier workflow", () => {
  it("refuses an unsupported terminal profile at startup", async () => {
    window.__APT_CONFIG__ = { ...rawMode1Config, APT_PROFILE: "ADMIN_WORKSTATION" };
    render(<App />);
    expect(await screen.findByText("Unsupported terminal profile")).toBeInTheDocument();
  });

  it("shows ticket and plate inputs together without lookup-mode controls", () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);
    expect(screen.getByLabelText("Ticket number")).toBeInTheDocument();
    expect(screen.getByLabelText("Plate number")).toBeInTheDocument();
    expect(screen.queryByRole("radio")).not.toBeInTheDocument();
  });

  it("resolves a ticket and shows only the approved cashier summary", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} localJournalBridge={bridgeWithLocalState()} />);
    await resolveTicket("APT-ACTIVE-1001");

    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("125.00");
    expect(screen.getByText("Discount Request")).toBeInTheDocument();
    expect(screen.getByText("Customer Information for Sales Invoice")).toBeInTheDocument();
    expect(screen.getByLabelText("Amount summary")).toBeVisible();
    expect(screen.queryByTestId("statutory-applied-facts")).not.toBeInTheDocument();
    expect(screen.queryByText(/Site ordinance availability|Statutory ID|Customer name|readyForCashAcceptance/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Continue to Cash" })).not.toBeInTheDocument();
  });

  it("renders the authoritative seven-line statutory VAT breakdown in Parking Session Details", () => {
    renderAppliedStatutorySummary();

    const calculation = within(screen.getByLabelText("Parking Session Details")).getByTestId("statutory-applied-facts");
    expect(calculation).toHaveTextContent("Original parking fee (VAT-inclusive)");
    expect(calculation).toHaveTextContent("112.00");
    expect(calculation).toHaveTextContent("Original VAT component (12%)");
    expect(calculation).toHaveTextContent("12.00");
    expect(calculation).toHaveTextContent("Original VAT-exclusive parking fee");
    expect(calculation).toHaveTextContent("100.00");
    expect(calculation).toHaveTextContent("LGU ordinance discount (20%)");
    expect(calculation).toHaveTextContent(/-.*20\.00/);
    expect(calculation).toHaveTextContent("Final VATable sales");
    expect(calculation).toHaveTextContent("80.00");
    expect(calculation).toHaveTextContent("Final applicable VAT (12%)");
    expect(calculation).toHaveTextContent("9.60");
    expect(calculation).toHaveTextContent("Total amount payable");
    expect(calculation).toHaveTextContent("89.60");
    expect(calculation).toHaveTextContent("City Ordinance No. 48");
    expect(screen.queryByLabelText("Amount summary")).not.toBeInTheDocument();
  });

  it("renders a full-fee benefit without inventing a percentage or payment", () => {
    renderAppliedStatutorySummary(
      appliedStatutoryState({
        benefitType: "FULL_FEE_EXEMPTION",
        discountPercentageBasisPoints: 10000,
        statutoryDiscountAmountMinorUnits: 10000,
        finalVatableSalesMinorUnits: 0,
        finalVatAmountMinorUnits: 0,
        finalPayableAmountMinorUnits: 0,
      }),
      appliedStatutoryBasis(0),
    );

    const calculation = screen.getByTestId("statutory-applied-facts");
    expect(calculation).toHaveTextContent("Original parking fee (VAT-inclusive)");
    expect(calculation).toHaveTextContent("112.00");
    expect(calculation).toHaveTextContent("Original VAT component (12%)");
    expect(calculation).toHaveTextContent("12.00");
    expect(calculation).toHaveTextContent("LGU ordinance benefit");
    expect(calculation).toHaveTextContent(/-.*100\.00/);
    expect(calculation).not.toHaveTextContent("LGU ordinance discount (");
    expect(calculation).toHaveTextContent("Final VATable sales");
    expect(calculation).toHaveTextContent("Final applicable VAT (12%)");
    expect(calculation).toHaveTextContent("Total amount payable");
    expect(calculation).toHaveTextContent("0.00");
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
  });

  it("keeps the authoritative VAT breakdown visible after persisted state readback", async () => {
    const config = mode1Config();
    const restoredState = appliedStatutoryState();
    const restoredSnapshot: PayableBasisStateSnapshot = {
      id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
      localWorkflowId: "restored-statutory-workflow",
      lookupReferenceType: "ticket",
      lookupReferenceValue: "APT-STATUTORY-1001",
      parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
      tariffSnapshotId: "99999999-9999-4999-8999-999999999999",
      siteId: "11111111-1111-1111-1111-111111111111",
      siteGroupId: "22222222-2222-2222-2222-222222222222",
      sitePosServerId: "POS-DEV-001",
      terminalId: "APT-DEV-001",
      authoritativeAmountMinorUnits: 8960,
      currency: "PHP",
      tariffValidUntil: "2026-10-27T10:00:00Z",
      parkingStatus: "Active",
      paymentStatus: "Unpaid",
      readyForCashAcceptance: true,
      blockingReasonCodes: [],
      retryable: false,
      safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
      centralPmsCorrelationId: "restored-statutory-correlation",
      cashierAcknowledgementRequired: false,
      amountChanged: true,
      statutoryDiscountStateJson: JSON.stringify(restoredState),
      resolvedAt: "2026-10-27T06:00:00Z",
      lastRevalidatedAt: "2026-10-27T06:01:00Z",
      updatedAt: "2026-10-27T06:01:00Z",
    };
    const bridge: LocalJournalBridge = {
      ...bridgeWithLocalState(),
      getLatestPayableBasisState: vi.fn(async (correlationId: string) => ({
        ok: true as const,
        command: "payableBasisState.getLatest",
        correlationId,
        payload: restoredSnapshot,
      })),
    };
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridge} />);

    const calculation = await screen.findByTestId("statutory-applied-facts");
    expect(calculation).toHaveTextContent("Original VAT component (12%)");
    expect(calculation).toHaveTextContent("12.00");
    expect(calculation).toHaveTextContent("Final applicable VAT (12%)");
    expect(calculation).toHaveTextContent("9.60");
    expect(calculation).toHaveTextContent("Total amount payable");
    expect(calculation).toHaveTextContent("89.60");
  });

  it("fails closed when an applied statutory response omits an authoritative VAT fact", () => {
    renderAppliedStatutorySummary(appliedStatutoryState({ originalVatAmountMinorUnits: null }));

    const calculation = screen.getByTestId("statutory-applied-facts");
    expect(within(calculation).getByTestId("statutory-financial-facts-unavailable")).toHaveTextContent(
      "No substitute values were calculated",
    );
    expect(calculation).not.toHaveTextContent("Original VAT component (12%)");
    expect(screen.queryByLabelText("Amount summary")).not.toBeInTheDocument();
  });

  it("discovers an existing pending statutory request and canonical customer-information presence", async () => {
    const config = mode1Config();
    const mock = new MockCentralPmsClient(config);
    const resolvePayableBasis = vi.fn(async (...args: Parameters<typeof mock.resolvePayableBasis>) => {
      const result = await mock.resolvePayableBasis(...args);
      if (!result.ok) return result;

      return {
        ok: true as const,
        response: {
          ...result.response,
          customerInformationSubmitted: true,
          statutoryDiscountReadiness: {
            applicable: true,
            ready: false,
            statutoryDiscountDecisionCommandId: "77777777-7777-4777-8777-777777770777",
            entitlementType: "SENIOR_CITIZEN",
            decisionStatus: "AWAITING_REVIEW",
            decisionResultStatus: "NOT_DECIDED",
            decisionCommandStatus: "AWAITING_REVIEW",
            applicationCommandStatus: "NOT_REQUESTED",
            applicationResultClassification: "NOT_REQUESTED",
            payableBasisReady: false,
            payableBasisReadinessStatus: "AWAITING_REVIEW",
            payableBasisReadinessAction: "POLL_READBACK",
            retryable: true,
            recoveryClassification: "PENDING_REVIEW",
            recoveryAction: "POLL_READBACK",
            message: "Statutory discount is awaiting review.",
          },
        },
      };
    });

    render(
      <TerminalShell
        config={config}
        client={{
          resolvePayableBasis,
          revalidatePayableBasis: (basis, correlationId) => mock.revalidatePayableBasis(basis, correlationId),
        }}
      />,
    );

    await resolveTicket("APT-ACTIVE-1001");

    expect(screen.getByText("Discount Request").parentElement).toHaveTextContent("Submitted");
    expect(screen.getByText("Customer Information for Sales Invoice").parentElement).toHaveTextContent("Submitted");
    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("125.00");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
    expect(resolvePayableBasis).toHaveBeenCalledTimes(1);
  });

  it("keeps cash blocked for the actual missing-custody reason", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} localJournalBridge={bridgeWithLocalState({ activeShift: true })} />);
    await resolveTicket("APT-ACTIVE-1001");

    expect(screen.getByTestId("local-cash-prerequisites-notice")).toHaveTextContent("Open or resume your cash custody");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
  });

  it("enables direct cash entry when durable shift and custody are recovered", async () => {
    const config = mode1Config();
    render(
      <TerminalShell
        config={config}
        client={new MockCentralPmsClient(config)}
        localJournalBridge={bridgeWithLocalState({ activeShift: true, activeCustody: true })}
      />,
    );
    await resolveTicket("APT-ACTIVE-1001");

    expect(screen.getByLabelText("Amount tendered")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Continue to Cash" })).not.toBeInTheDocument();
  });

  it("resolves a plate without requiring a ticket", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);
    await userEvent.type(screen.getByLabelText("Plate number"), "PLATE-READY-1002");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByRole("heading", { name: "Parking session details" })).toBeInTheDocument();
    expect(screen.getByText("PLATE-READY-1002")).toBeInTheDocument();
  });

  it.each([
    ["ticket", "Ticket number", "1474119573147"],
    ["plate", "Plate number", "ABC1147"],
  ] as const)("shows a projected session by %s while keeping cash unavailable", async (_referenceType, label, value) => {
    const revalidatePayableBasis = vi.fn();
    const client: CentralPmsClient = {
      resolvePayableBasis: vi.fn(async () => ({ ok: true as const, response: projectedSessionPayload() })),
      revalidatePayableBasis,
    };

    render(<TerminalShell config={mode1Config()} client={client} localJournalBridge={bridgeWithLocalState({ activeShift: true, activeCustody: true })} />);
    await userEvent.type(screen.getByLabelText(label), value);
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByRole("heading", { name: "Session found from projection" })).toBeInTheDocument();
    expect(screen.getByText("1474119573147")).toBeInTheDocument();
    expect(screen.getByText("ABC1147")).toBeInTheDocument();
    expect(screen.getByText("PITX Level 3")).toBeInTheDocument();
    expect(screen.getByText("Continuity projection")).toBeInTheDocument();
    expect(screen.getByTestId("projection-payable-basis-blocked")).toHaveTextContent("Cash acceptance remains blocked");
    expect(screen.queryByTestId("payable-basis-amount")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record Cash Received" })).not.toBeInTheDocument();
    expect(revalidatePayableBasis).not.toHaveBeenCalled();
  });

  it("retries the same lookup and transitions from projection to a live payable basis", async () => {
    const config = mode1Config();
    const liveClient = new MockCentralPmsClient(config);
    const resolvePayableBasis = vi.fn()
      .mockResolvedValueOnce({ ok: true as const, response: projectedSessionPayload() })
      .mockImplementation((...args: Parameters<typeof liveClient.resolvePayableBasis>) => liveClient.resolvePayableBasis(...args));
    const client: CentralPmsClient = {
      resolvePayableBasis,
      revalidatePayableBasis: (basis, correlationId) => liveClient.revalidatePayableBasis(basis, correlationId),
    };

    render(<TerminalShell config={config} client={client} localJournalBridge={bridgeWithLocalState()} />);
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    expect(await screen.findByRole("heading", { name: "Session found from projection" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Retry live fee" }));

    expect(await screen.findByRole("heading", { name: "Parking session details" })).toBeInTheDocument();
    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("125.00");
    expect(resolvePayableBasis).toHaveBeenCalledTimes(2);
  });

  it("shows a complete continuity payable basis and keeps normal cash acceptance available", async () => {
    const config = mode1Config();
    const liveClient = new MockCentralPmsClient(config);
    const resolvePayableBasis = vi.fn(async (...args: Parameters<typeof liveClient.resolvePayableBasis>) => {
      const result = await liveClient.resolvePayableBasis(...args);
      if (!result.ok) return result;
      return {
        ok: true as const,
        response: {
          ...result.response,
          sessionSource: "VENDOR_SESSION_PROJECTION",
          degraded: true,
          payableBasisAvailable: true as const,
          tariffSource: "EXITPASS_CONTINUITY",
          manualExitRequired: true,
        },
      };
    });

    render(<TerminalShell config={config} client={{
      resolvePayableBasis,
      revalidatePayableBasis: (basis, correlationId) => liveClient.revalidatePayableBasis(basis, correlationId),
    }} localJournalBridge={bridgeWithLocalState()} />);
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-ACTIVE-1001");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByRole("heading", { name: "Parking session details" })).toBeInTheDocument();
    expect(screen.getByText(/Site continuity tariff/i)).toBeInTheDocument();
    expect(screen.getByTestId("payable-basis-amount")).toHaveTextContent("125.00");
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeInTheDocument();
  });

  it("accepts matching ticket and plate values", async () => {
    const config = mode1Config();
    const mock = new MockCentralPmsClient(config);
    const resolvePayableBasis = vi.fn(async (referenceType: "ticket" | "plate", referenceValue: string, correlationId: string) => {
      const result = await mock.resolvePayableBasis("ticket", "APT-ACTIVE-1001", correlationId);
      if (!result.ok) return result;
      return {
        ok: true as const,
        response: {
          ...result.response,
          ticketReference: referenceType === "ticket" ? referenceValue : "APT-ACTIVE-1001",
          plateNumber: referenceType === "plate" ? referenceValue : "NCR-4421",
        },
      };
    });
    render(
      <TerminalShell
        config={config}
        client={{ resolvePayableBasis, revalidatePayableBasis: (basis, correlationId) => mock.revalidatePayableBasis(basis, correlationId) }}
      />,
    );

    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-ACTIVE-1001");
    await userEvent.type(screen.getByLabelText("Plate number"), "NCR-4421");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByRole("heading", { name: "Parking session details" })).toBeInTheDocument();
    expect(resolvePayableBasis).toHaveBeenCalledTimes(2);
  });

  it("rejects ticket and plate values that identify different sessions", async () => {
    const config = mode1Config();
    render(<TerminalShell config={config} client={new MockCentralPmsClient(config)} />);
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-ACTIVE-1001");
    await userEvent.type(screen.getByLabelText("Plate number"), "PLATE-READY-1002");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Ticket and plate do not identify the same parking session.")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Parking session details" })).not.toBeInTheDocument();
  });

  it("shows a safe not-found error without exposing an internal identifier", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText("Parking session not found")).toBeInTheDocument();
    expect(containsInternalGuid(document.body.textContent ?? "")).toBe(false);
  });

  it("shows one concise fiscal blocker without a readiness matrix", async () => {
    render(<TerminalShell config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} />);
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-FISCAL-BLOCKED");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText(/Sales Invoice configuration is incomplete|Cash acceptance is blocked/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
    expect(screen.queryByText("Fiscal Readiness")).not.toBeInTheDocument();
  });

  it("does not call ordinance-availability endpoints during ordinary lookup", async () => {
    const config = mode1Config();
    const client = new MockCentralPmsClient(config);
    const availabilitySpy = vi.spyOn(client, "resolveStatutoryOrdinanceAvailability");
    render(<TerminalShell config={config} client={client} />);
    await resolveTicket("APT-ACTIVE-1001");

    expect(availabilitySpy).not.toHaveBeenCalled();
    expect(screen.queryByText(/ordinance|policy source|verification unknown/i)).not.toBeInTheDocument();
  });

  it("generates a fresh correlation id per lookup action", async () => {
    const calls: string[] = [];
    const mock = new MockCentralPmsClient(mode1Config());
    const resolvePayableBasis = async (...args: Parameters<typeof mock.resolvePayableBasis>) => {
      calls.push(args[2]);
      return mock.resolvePayableBasis(...args);
    };
    render(
      <TerminalShell
        config={mode1Config()}
        client={{ resolvePayableBasis, revalidatePayableBasis: (basis, correlationId) => mock.revalidatePayableBasis(basis, correlationId) }}
      />,
    );

    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    await screen.findByText("Parking session not found");
    await userEvent.click(screen.getByRole("button", { name: "Back to lookup" }));
    await userEvent.type(screen.getByLabelText("Ticket number"), "APT-NOTFOUND-404");
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls[0]).not.toBe(calls[1]);
  });
});

async function resolveTicket(reference: string) {
  await userEvent.type(screen.getByLabelText("Ticket number"), reference);
  await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
  await screen.findByRole("heading", { name: "Parking session details" });
}

function bridgeWithLocalState(options: {
  activeShift?: boolean;
  activeCustody?: boolean;
} = {}): LocalJournalBridge {
  function healthForContext(context?: LocalOperationalContext): LocalJournalHealth {
    void context;
    const includeActiveShift = Boolean(options.activeShift);
    const includeActiveCustody = Boolean(options.activeCustody && includeActiveShift);

    return {
      healthy: true,
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
      payload: null,
    })),
    readTenderByParkingSession: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "localJournal.readTenderByParkingSession",
      correlationId,
      payload: { tender: null, events: [] },
    })),
  } as unknown as LocalJournalBridge;
}

function projectedSessionPayload(): ProjectedSessionResponse {
  return {
    operation: "resolve",
    parkingSessionId: null,
    tariffSnapshotId: null,
    sessionFound: true,
    sessionSource: "VENDOR_SESSION_PROJECTION",
    degraded: true,
    payableBasisAvailable: false,
    vendorSessionProjectionId: "455bfa51-98b3-4fbf-9efb-336339596a34",
    projectionStatus: "ACTIVE",
    projectionLastRefreshedAt: "2026-10-05T01:00:00Z",
    projectionFreshnessAgeSeconds: 15,
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    siteId: "11111111-1111-1111-1111-111111111111",
    siteName: "PITX Level 3",
    vendorSystemId: "VENDOR-PMS-DEV",
    ticketReference: "1474119573147",
    plateNumber: "ABC1147",
    entryTimestamp: "2026-10-05T00:30:00Z",
    parkingStatus: "ACTIVE",
    paymentStatus: null,
    authoritativeAmountMinorUnits: null,
    currency: null,
    tariffValidUntil: null,
    readyForCashAcceptance: false,
    blockingReasonCodes: ["PROJECTION_SESSION_PAYABLE_BASIS_UNAVAILABLE"],
    retryable: true,
    safeUserFacingClassification: "PROJECTION_SESSION_PAYABLE_BASIS_UNAVAILABLE",
    safeMessage: "Session found from projection; live payable amount is temporarily unavailable.",
    correlationId: "projection-correlation",
  };
}

function renderAppliedStatutorySummary(
  state: StatutoryDiscountWorkflowState = appliedStatutoryState(),
  basis: PayableBasisResponse = appliedStatutoryBasis(),
) {
  const config = mode1Config();
  render(
    <TerminalShell
      config={config}
      client={new MockCentralPmsClient(config)}
      localJournalBridge={bridgeWithLocalState()}
      initialResolvedBasis={basis}
      initialStatutoryState={state}
      restorePayableBasisOnMount={false}
    />,
  );
}

function appliedStatutoryState(
  overrides: Partial<StatutoryDiscountWorkflowState> = {},
): StatutoryDiscountWorkflowState {
  return {
    status: "applied",
    appliedTariffSnapshotId: "99999999-9999-4999-8999-999999999999",
    originalAmountMinorUnits: 11200,
    originalVatAmountMinorUnits: 1200,
    vatExclusiveBasisAmountMinorUnits: 10000,
    statutoryDiscountAmountMinorUnits: 2000,
    finalVatableSalesMinorUnits: 8000,
    finalVatAmountMinorUnits: 960,
    finalPayableAmountMinorUnits: 8960,
    discountPercentageBasisPoints: 2000,
    benefitType: "PERCENTAGE_DISCOUNT",
    ordinanceReference: "City Ordinance No. 48",
    vatTreatment: "VAT_INCLUSIVE_NO_EXEMPTION",
    currency: "PHP",
    payableBasisReady: true,
    payableBasisReadinessStatus: "READY",
    amountAcknowledged: true,
    ...overrides,
  };
}

function appliedStatutoryBasis(finalPayableAmountMinorUnits = 8960): PayableBasisResponse {
  return {
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    appliedTariffSnapshotId: "99999999-9999-4999-8999-999999999999",
    siteGroupId: "22222222-2222-2222-2222-222222222222",
    siteId: "11111111-1111-1111-1111-111111111111",
    terminalId: "APT-DEV-001",
    ticketReference: "APT-STATUTORY-1001",
    parkingStatus: "Active",
    paymentStatus: "Unpaid",
    authoritativeAmountMinorUnits: finalPayableAmountMinorUnits,
    currency: "PHP",
    tariffValidUntil: "2026-10-27T10:00:00Z",
    readyForCashAcceptance: true,
    blockingReasonCodes: [],
    retryable: false,
    safeUserFacingClassification: "READY_FOR_CASH_ACCEPTANCE",
    correlationId: "safe-statutory-basis-correlation",
  };
}
