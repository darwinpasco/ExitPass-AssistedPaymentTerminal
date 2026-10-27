import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuthenticatedTerminal, CashierLoginPanel, HumanSessionPanel } from "./App";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import {
  createDevelopmentHumanSessionBridge,
  mayUseDevelopmentHumanSessionFixture,
  type HumanSessionBridge,
  type HumanSessionBridgeResult,
  type HumanSessionState,
  type CashCustodySummary,
  type CashCustodyMovement,
} from "./humanSessionBridge";
import { CASH_CUSTODY_DENOMINATIONS, type CashCustodyDenominationLine } from "./cashCustodyDenominations";
import { mode1Config } from "./test/testConfig";

const cashierGuid = "44444444-4444-4444-8444-444444444444";
const sessionGuid = "55555555-5555-4555-8555-555555555555";
const receiverGuid = "66666666-6666-4666-8666-666666666666";

describe("APT human-session presentation boundary", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    window.history.replaceState({}, "", "/");
    localStorage.clear();
    sessionStorage.clear();
  });

  it("keeps password entry out of WebView and requests the native credential prompt", async () => {
    let capturedUsername: string | null = null;
    let release: ((result: HumanSessionBridgeResult) => void) | undefined;
    const loginResult = new Promise<HumanSessionBridgeResult>((resolve) => { release = resolve; });
    const bridge = bridgeStub({
      login: async (_correlationId, username) => {
        capturedUsername = username;
        return loginResult;
      },
    });
    const onResult = vi.fn();
    render(<CashierLoginPanel state={unauthenticatedState()} bridge={bridge} onResult={onResult} />);

    await userEvent.type(screen.getByLabelText("Username"), "cashier.synthetic");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(capturedUsername).toBe("cashier.synthetic");
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(screen.getByText(/secure Windows dialog/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/totp|mfa|verification code/i)).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("synthetic-password");
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);

    release!({ ok: true, command: "humanSession.login", correlationId: "corr", payload: authenticatedState() });
    await waitFor(() => expect(onResult).toHaveBeenCalledTimes(1));
  });

  it("coalesces duplicate form submissions into one native credential request", async () => {
    let release: ((result: HumanSessionBridgeResult) => void) | undefined;
    const pending = new Promise<HumanSessionBridgeResult>((resolve) => { release = resolve; });
    const login = vi.fn(async () => pending);
    const { container } = render(<CashierLoginPanel
      state={unauthenticatedState()}
      bridge={bridgeStub({ login })}
      onResult={vi.fn()}
    />);
    await userEvent.type(screen.getByLabelText("Username"), "cashier.synthetic");
    const form = container.querySelector("form") as HTMLFormElement;

    await act(async () => {
      form.requestSubmit();
      form.requestSubmit();
    });

    expect(login).toHaveBeenCalledTimes(1);
    await act(async () => {
      release!(successResult("humanSession.login", authenticatedState()));
      await pending;
    });
    await waitFor(() => expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled());
  });

  it("cannot send browser-populated credential data through the login component", async () => {
    const login = vi.fn();
    const { container } = render(<CashierLoginPanel state={unauthenticatedState()} bridge={bridgeStub({ login })} onResult={vi.fn()} />);
    const rogueInput = document.createElement("input");
    rogueInput.type = "password";
    rogueInput.value = "simulated-browser-autofill";
    container.appendChild(rogueInput);

    await userEvent.type(screen.getByLabelText("Username"), "cashier.synthetic");
    await act(async () => {
      (container.querySelector("form") as HTMLFormElement).requestSubmit();
    });

    await waitFor(() => expect(login).toHaveBeenCalledWith(expect.any(String), "cashier.synthetic"));
    expect(login.mock.calls[0]).toHaveLength(2);
    expect(login.mock.calls.flat()).not.toContain("simulated-browser-autofill");
  });

  it("marks the initialized unauthenticated application without mounting the operational terminal", async () => {
    const state = unauthenticatedState();
    const bridge = bridgeStub({ restore: async () => successResult("humanSession.restore", state) });

    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);

    expect(await screen.findByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.queryByTestId("apt-terminal-shell")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Collect payment" })).not.toBeInTheDocument();
  });

  it("transitions from the initialized login shell to the authenticated terminal shell", async () => {
    const bridge = bridgeStub({
      restore: async () => successResult("humanSession.restore", unauthenticatedState()),
      login: async () => successResult("humanSession.login", authenticatedState()),
    });

    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);
    await screen.findByTestId("apt-human-login-shell");
    await userEvent.type(screen.getByLabelText("Username"), "cashier.synthetic");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByTestId("apt-terminal-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.queryByTestId("apt-human-login-shell")).not.toBeInTheDocument();
  });

  it("keeps invalid credentials in the initialized login shell", async () => {
    const invalid = {
      ...unauthenticatedState(),
      errorCode: "INVALID_CREDENTIALS",
      safeMessage: "The username or password is incorrect.",
    };
    const bridge = bridgeStub({
      restore: async () => successResult("humanSession.restore", unauthenticatedState()),
      login: async () => successResult("humanSession.login", invalid),
    });

    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);
    await screen.findByTestId("apt-human-login-shell");
    await userEvent.type(screen.getByLabelText("Username"), "cashier.synthetic");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("The username or password is incorrect.")).toBeInTheDocument();
    expect(screen.getByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.queryByTestId("apt-terminal-shell")).not.toBeInTheDocument();
  });

  it.each([
    ["SESSION_EXPIRED", "The cashier session expired. Sign in again."],
    ["SESSION_REVOKED", "The cashier session was revoked. Sign in again."],
  ])("returns %s to the initialized login shell while keeping cash unavailable", async (errorCode, safeMessage) => {
    const login = vi.fn();
    const reauthenticate = vi.fn();
    const locked = {
      ...authenticatedState(),
      authenticationState: "LOCKED",
      authenticated: false,
      shiftOperationsAuthorized: false,
      custodyOperationsAuthorized: false,
      cashOperationsAuthorized: false,
      errorCode,
      safeMessage,
    };
    const bridge = bridgeStub({
      restore: async () => successResult("humanSession.restore", authenticatedState()),
      refresh: async () => successResult("humanSession.refresh", locked),
      login,
      reauthenticate,
    });

    const validationTimer = captureAuthorityValidationTimer();
    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);
    await screen.findByTestId("apt-terminal-shell");
    await validationTimer.run();

    expect(await screen.findByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.getByText(safeMessage)).toBeInTheDocument();
    expect(screen.getByRole("status", { name: "Preserved cash accountability" })).toBeInTheDocument();
    expect(screen.getByText("New cash authority").parentElement).toHaveTextContent("Locked");
    expect(screen.queryByText("Online cashier authority current")).not.toBeInTheDocument();
    expect(screen.queryByText("Session status")).not.toBeInTheDocument();
    expect(screen.queryByTestId("apt-terminal-shell")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Collect payment" })).not.toBeInTheDocument();
    expect(login).not.toHaveBeenCalled();
    expect(reauthenticate).not.toHaveBeenCalled();
  });

  it("offers contextual native sign-in instead of session-plumbing actions after authority loss", () => {
    const locked = {
      ...unauthenticatedState(),
      authenticationState: "LOCKED",
      errorCode: "SESSION_REVOKED",
      safeMessage: "Your session is no longer active. Sign in again to continue. Cash operations are locked.",
      activeShift: authenticatedState().activeShift,
      activeCashCustodySession: authenticatedState().activeCashCustodySession,
    };

    render(<CashierLoginPanel state={locked} bridge={bridgeStub()} onResult={vi.fn()} />);

    expect(screen.getByText(locked.safeMessage)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Refresh authority" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reauthenticate" })).not.toBeInTheDocument();
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(screen.getByText("New cash authority").parentElement).toHaveTextContent("Locked");
  });

  it("clears authenticated presentation when a refresh bridge failure is returned", async () => {
    const bridge = bridgeStub({
      restore: async () => successResult("humanSession.restore", authenticatedState()),
      refresh: async () => ({
        ok: false,
        command: "humanSession.refresh",
        correlationId: "refresh-failure",
        error: { code: "HUMAN_SESSION_UNAVAILABLE", message: "Online cashier authority could not be confirmed. New cash remains locked." },
      }),
    });

    const validationTimer = captureAuthorityValidationTimer();
    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);
    await screen.findByTestId("apt-terminal-shell");
    await validationTimer.run();

    expect(await screen.findByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
    expect(screen.getByText("Online cashier authority could not be confirmed. New cash remains locked.")).toBeInTheDocument();
    expect(screen.queryByText("Online cashier authority current")).not.toBeInTheDocument();
    expect(screen.queryByTestId("apt-terminal-shell")).not.toBeInTheDocument();
  });

  it("disables login until the host reports device trust", () => {
    render(<CashierLoginPanel state={unauthenticatedState(false)} bridge={bridgeStub()} onResult={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Sign in" })).toBeDisabled();
    expect(screen.getAllByText(/device trust/i)).toHaveLength(2);
  });

  it("renders safe cashier identity and custody lock without internal user or session GUIDs", () => {
    const state = {
      ...authenticatedState(),
      authenticationState: "LOCKED",
      authenticated: true,
      shiftOperationsAuthorized: false,
      custodyOperationsAuthorized: false,
      cashOperationsAuthorized: false,
      safeMessage: "The cashier session expired. Sign in again.",
      errorCode: "SESSION_EXPIRED",
    };
    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByText("Synthetic Cashier")).toBeInTheDocument();
    expect(screen.getByTestId("cashier-header-shift")).toHaveTextContent("OPEN");
    expect(screen.getByTestId("cashier-header-custody")).toHaveTextContent("OPEN");
    expect(document.body).not.toHaveTextContent(cashierGuid);
    expect(document.body).not.toHaveTextContent(sessionGuid);
    expect(screen.queryByText(/permission/i)).not.toBeInTheDocument();
  });

  it("does not expose manual authority refresh, generic reauthentication, or browser credentials", () => {
    const refresh = vi.fn();
    const reauthenticate = vi.fn();
    render(<HumanSessionPanel state={authenticatedState()} bridge={bridgeStub({ refresh, reauthenticate })} onStateChange={vi.fn()} />);

    expect(screen.queryByRole("button", { name: "Refresh authority" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reauthenticate" })).not.toBeInTheDocument();
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(refresh).not.toHaveBeenCalled();
    expect(reauthenticate).not.toHaveBeenCalled();
  });

  it("explains that sign out is blocked while open cash custody is preserved", async () => {
    const onStateChange = vi.fn();
    const blocked = {
      ...authenticatedState(),
      errorCode: "OPEN_CUSTODY_LOGOUT_BLOCKED",
      safeMessage: "Sign out is unavailable while you have open cash custody.",
    };
    const { rerender } = render(<HumanSessionPanel
      state={authenticatedState()}
      bridge={bridgeStub({ logout: async () => successResult("humanSession.logout", blocked) })}
      onStateChange={onStateChange}
    />);

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() => expect(onStateChange).toHaveBeenCalledWith(blocked));
    rerender(<HumanSessionPanel state={blocked} bridge={bridgeStub()} onStateChange={onStateChange} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Sign out is unavailable while you have open cash custody.");
    expect(screen.getByTestId("cashier-header-custody")).toHaveTextContent("OPEN");
  });

  it("automatically keeps active online authority current at the bounded validation cadence", async () => {
    const refresh = vi.fn(async () => successResult("humanSession.refresh", authenticatedState()));
    const bridge = bridgeStub({
      restore: async () => successResult("humanSession.restore", authenticatedState()),
      refresh,
    });
    const validationTimer = captureAuthorityValidationTimer();
    render(<AuthenticatedTerminal config={mode1Config()} client={new MockCentralPmsClient(mode1Config())} humanSessionBridge={bridge} />);
    await screen.findByTestId("apt-terminal-shell");

    await validationTimer.run();

    expect(refresh).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("cashier-header-shift")).toHaveTextContent("OPEN");
    expect(screen.getByTestId("cashier-header-custody")).toHaveTextContent("OPEN");
    expect(screen.getByTestId("apt-terminal-shell")).toHaveAttribute("data-app-ready", "true");
  });

  it("uses operation-specific host authorization hints for shift and custody controls", () => {
    const noShiftAuthority = {
      ...authenticatedState(),
      shiftOperationsAuthorized: false,
      custodyOperationsAuthorized: true,
      cashOperationsAuthorized: false,
      activeShift: null,
      activeCashCustodySession: null,
    };
    const { rerender } = render(<HumanSessionPanel state={noShiftAuthority} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Open Shift" })).toBeDisabled();

    const noCustodyAuthority = {
      ...authenticatedState(),
      custodyOperationsAuthorized: false,
      cashOperationsAuthorized: false,
      activeCashCustodySession: null,
    };
    rerender(<HumanSessionPanel state={noCustodyAuthority} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Review opening count" })).toBeDisabled();
  });

  it("closes only owned custody before allowing the owned shift to close", async () => {
    const custodyClosed = { ...authenticatedState(), activeCashCustodySession: null };
    const shiftClosed = { ...custodyClosed, activeShift: null };
    const closeOwnCustody = vi.fn(async (_correlationId: string, _denominations: CashCustodyDenominationLine[]) =>
      successResult("humanSession.closeOwnCustody", custodyClosed));
    const closeOwnShift = vi.fn(async () => successResult("humanSession.closeOwnShift", shiftClosed));
    const onStateChange = vi.fn();
    const bridge = bridgeStub({ closeOwnCustody, closeOwnShift });
    const { rerender } = render(<HumanSessionPanel state={authenticatedState()} bridge={bridge} onStateChange={onStateChange} />);

    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));
    expect(screen.getByRole("button", { name: "Close Cashier Shift" })).toBeDisabled();
    await userEvent.clear(screen.getByLabelText("PHP 100 quantity"));
    await userEvent.type(screen.getByLabelText("PHP 100 quantity"), "1");
    await userEvent.click(screen.getByRole("button", { name: "Review actual count" }));
    await userEvent.click(screen.getByRole("button", { name: "Confirm and close" }));

    await waitFor(() => expect(closeOwnCustody).toHaveBeenCalledWith(
      expect.any(String),
      expect.arrayContaining([
        { denominationCode: "PHP-100", denominationValueMinorUnits: 10_000, quantity: 1 },
        { denominationCode: "PHP-0.01", denominationValueMinorUnits: 1, quantity: 0 },
      ]),
    ));
    expect(closeOwnCustody.mock.calls[0][1]).toHaveLength(13);
    expect(onStateChange).toHaveBeenCalledWith(custodyClosed);
    rerender(<HumanSessionPanel state={custodyClosed} bridge={bridge} onStateChange={onStateChange} />);
    expect(screen.getByRole("button", { name: "Close Cashier Shift" })).toBeEnabled();

    await userEvent.click(screen.getByRole("button", { name: "Close Cashier Shift" }));
    await waitFor(() => expect(closeOwnShift).toHaveBeenCalledTimes(1));
    expect(onStateChange).toHaveBeenCalledWith(shiftClosed);
  });

  it("shows operational custody counts while preserving blind cash counting", async () => {
    const state = {
      ...authenticatedState(),
      activeCashCustodySession: {
        ...authenticatedState().activeCashCustodySession!,
        expectedClosingCashAmount: 987_654,
        varianceAmount: 123_456,
      },
      activeCashCustodySummary: {
        session: authenticatedState().activeCashCustodySession!,
        submissions: {
          physicalCashReceiptCount: 36,
          confirmedSubmissionCount: 34,
          pendingSubmissionCount: 1,
          conflictingSubmissionCount: 1,
          rejectedSubmissionCount: 0,
          unresolvedSubmissionCount: 2,
          pendingFiscalIssuanceCount: 1,
        },
      },
    };

    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));

    expect(screen.getByLabelText("Open cash custody summary")).toHaveTextContent("Physical cash receipts36");
    expect(screen.getByLabelText("Open cash custody summary")).toHaveTextContent("Confirmed submissions34");
    expect(screen.getByLabelText("Open cash custody summary")).toHaveTextContent("Pending submissions1");
    expect(screen.getByLabelText("Open cash custody summary")).toHaveTextContent("Conflicting submissions1");
    expect(screen.getAllByText("Calculated on close")).toHaveLength(2);
    expect(document.body).not.toHaveTextContent("987,654");
    expect(document.body).not.toHaveTextContent("123,456");
    expect(screen.getByTestId("cash-custody-unresolved-warning")).toHaveTextContent("2 cash receipts require reconciliation before custody close.");
    expect(screen.getByTestId("cash-custody-fiscal-warning")).toHaveTextContent("1 confirmed payment awaiting fiscal issuance");
  });

  it("captures a complete movement count and creates only a pending handover", async () => {
    const initiateCashCustodyMovement = vi.fn(async (
      _correlationId: string,
      _payload: Parameters<HumanSessionBridge["initiateCashCustodyMovement"]>[1],
    ) => successResult("humanSession.initiateCashCustodyMovement", authenticatedState()));
    const state = movementState([]);
    render(<HumanSessionPanel
      state={state}
      bridge={bridgeStub({ initiateCashCustodyMovement })}
      onStateChange={vi.fn()}
    />);
    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));
    const panel = screen.getByLabelText("Cash custody movements");

    await waitFor(() => expect(within(panel).getByLabelText("Receiving Person")).toBeEnabled());
    await userEvent.selectOptions(within(panel).getByLabelText("Receiving Person"), receiverGuid);
    await userEvent.type(within(panel).getByLabelText("Handover or remittance reference"), "DROP-001");
    await userEvent.clear(within(panel).getByLabelText("PHP 100 quantity"));
    await userEvent.type(within(panel).getByLabelText("PHP 100 quantity"), "1");
    await userEvent.click(within(panel).getByRole("button", { name: "Review movement count" }));

    expect(within(panel).getByRole("status")).toHaveTextContent("100.00");
    await userEvent.click(within(panel).getByRole("button", { name: "Confirm movement count" }));
    await waitFor(() => expect(initiateCashCustodyMovement).toHaveBeenCalledTimes(1));

    const payload = initiateCashCustodyMovement.mock.calls[0][1];
    expect(payload.movementType).toBe("SafeDrop");
    expect(payload.intendedCounterpartyReference).toBe(receiverGuid);
    expect(payload.externalHandoverReference).toBe("DROP-001");
    expect(payload.denominations).toHaveLength(13);
    expect(payload.denominations).toContainEqual({ denominationCode: "PHP-100", denominationValueMinorUnits: 10_000, quantity: 1 });
    expect(document.body).not.toHaveTextContent("Available Cash");
  });

  it("distinguishes duplicate display names and binds the selected stable identity", async () => {
    const secondReceiver = "77777777-7777-4777-8777-777777777777";
    const initiateCashCustodyMovement = vi.fn(async (
      _correlationId: string,
      _payload: Parameters<HumanSessionBridge["initiateCashCustodyMovement"]>[1],
    ) => successResult("humanSession.initiateCashCustodyMovement", authenticatedState()));
    const listCashCustodyMovementCounterparties = vi.fn(async () =>
      successResult("humanSession.listCashCustodyMovementCounterparties", {
        ...authenticatedState(),
        movementCounterparties: [
          { userReference: receiverGuid, displayName: "Duplicate Name", roleCode: "OPERATIONS_SUPERVISOR", roleLabel: "Operations Supervisor" },
          { userReference: secondReceiver, displayName: "Duplicate Name", roleCode: "APT_SITE_CUSTODIAN", roleLabel: "APT Site Custodian" },
        ],
      }));
    render(<HumanSessionPanel
      state={movementState([])}
      bridge={bridgeStub({ listCashCustodyMovementCounterparties, initiateCashCustodyMovement })}
      onStateChange={vi.fn()}
    />);
    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));
    const panel = screen.getByLabelText("Cash custody movements");

    const selector = await within(panel).findByLabelText("Receiving Person");
    expect(within(selector).getByRole("option", { name: "Duplicate Name - Operations Supervisor (66666666)" })).toBeInTheDocument();
    expect(within(selector).getByRole("option", { name: "Duplicate Name - APT Site Custodian (77777777)" })).toBeInTheDocument();
    expect(within(panel).queryByLabelText("Authorized counterparty username")).not.toBeInTheDocument();
    await userEvent.selectOptions(selector, secondReceiver);
    await userEvent.type(within(panel).getByLabelText("Handover or remittance reference"), "DROP-DUPLICATE-NAME");
    await userEvent.clear(within(panel).getByLabelText("PHP 100 quantity"));
    await userEvent.type(within(panel).getByLabelText("PHP 100 quantity"), "1");
    await userEvent.click(within(panel).getByRole("button", { name: "Review movement count" }));
    await userEvent.click(within(panel).getByRole("button", { name: "Confirm movement count" }));

    await waitFor(() => expect(initiateCashCustodyMovement).toHaveBeenCalledTimes(1));
    expect(initiateCashCustodyMovement.mock.calls[0][1].intendedCounterpartyReference).toBe(secondReceiver);
  });

  it("blocks custody close for a pending movement and requests native receiver acknowledgment", async () => {
    const movement = movementFixture("PendingAcknowledgment");
    const acknowledgeCashCustodyMovement = vi.fn(async (
      _correlationId: string,
      _movementId: string,
      _reject: boolean,
      _reasonCode: string | null,
      _idempotencyKey: string,
    ) =>
      successResult("humanSession.acknowledgeCashCustodyMovement", movementState([{ ...movement, status: "Completed" }])));
    render(<HumanSessionPanel
      state={movementState([movement])}
      bridge={bridgeStub({ acknowledgeCashCustodyMovement })}
      onStateChange={vi.fn()}
    />);
    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));

    expect(screen.getByText("1 movement awaiting acknowledgment")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Review actual count" })).toBeDisabled();
    expect(screen.getByText("Resolve all pending cash movements before closing custody.")).toBeInTheDocument();
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(screen.getAllByText("Calculated on close")).toHaveLength(2);

    await userEvent.click(screen.getByRole("button", { name: "Receiver Acknowledgment" }));
    await waitFor(() => expect(acknowledgeCashCustodyMovement).toHaveBeenCalledTimes(1));
    expect(acknowledgeCashCustodyMovement.mock.calls[0][1]).toBe(movement.id);
    expect(acknowledgeCashCustodyMovement.mock.calls[0][2]).toBe(false);
    expect(document.querySelector('input[type="password"]')).toBeNull();
  });

  it("renders committed close evidence and lets the cashier inspect immutable history", async () => {
    const latest = closedCustodySummary("77777777-7777-4777-8777-777777777777", 1_100, 1_090, -10, 3, 1);
    latest.session.openingDenominations = [
      { denominationCode: "PHP-200", denominationValueMinorUnits: 20_000, quantity: 5, calculatedAmountMinorUnits: 100_000 },
    ];
    latest.session.closingDenominations = [
      { denominationCode: "PHP-200", denominationValueMinorUnits: 20_000, quantity: 5, calculatedAmountMinorUnits: 100_000 },
      { denominationCode: "PHP-50", denominationValueMinorUnits: 5_000, quantity: 1, calculatedAmountMinorUnits: 5_000 },
      { denominationCode: "PHP-20", denominationValueMinorUnits: 2_000, quantity: 2, calculatedAmountMinorUnits: 4_000 },
    ];
    const prior = closedCustodySummary("88888888-8888-4888-8888-888888888888", 500, 505, 5, 2, 0);
    const state = {
      ...authenticatedState(),
      activeCashCustodySession: null,
      activeCashCustodySummary: null,
      latestClosedCashCustody: latest,
      closedCashCustodyHistory: [latest, prior],
    };

    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    const result = screen.getByLabelText("Committed cash custody close result");
    expect(result).toHaveTextContent("CUST-77777777");
    expect(result).toHaveTextContent("1,100.00");
    expect(result).toHaveTextContent("1,090.00");
    expect(result).toHaveTextContent("-₱10.00");
    expect(result).toHaveTextContent("Unresolved at close1");
    expect(screen.getByLabelText("Opening denomination breakdown")).toHaveTextContent("PHP-200");
    expect(screen.getByLabelText("Closing denomination breakdown")).toHaveTextContent("PHP-50");
    expect(screen.getByLabelText("Closed cash custody history")).toHaveTextContent("CUST-88888888");

    await userEvent.click(screen.getByRole("button", { name: "CUST-88888888" }));
    expect(screen.getByLabelText("Committed cash custody close result")).toHaveTextContent("CUST-88888888");
    expect(screen.getByLabelText("Committed cash custody close result")).toHaveTextContent("505.00");
  });

  it("recognizes an authenticated owned open shift and enables custody without a manual refresh", () => {
    const state = {
      ...authenticatedState(),
      activeCashCustodySession: null,
    };

    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByTestId("cashier-header-shift")).toHaveTextContent("OPEN");
    expect(screen.getByTestId("cashier-header-custody")).toHaveTextContent("CLOSED");
    expect(screen.getByRole("button", { name: "Review opening count" })).toBeEnabled();
  });

  it("captures every opening denomination and calculates exact centavo totals before confirmation", async () => {
    const openOrResumeCustody = vi.fn(async (_correlationId: string, _denominations: CashCustodyDenominationLine[]) =>
      successResult("humanSession.openOrResumeCustody", authenticatedState()));
    const state = { ...authenticatedState(), activeCashCustodySession: null };
    render(<HumanSessionPanel state={state} bridge={bridgeStub({ openOrResumeCustody })} onStateChange={vi.fn()} />);

    await userEvent.clear(screen.getByLabelText("PHP 200 quantity"));
    await userEvent.type(screen.getByLabelText("PHP 200 quantity"), "1");
    for (const label of ["PHP 0.25", "PHP 0.10", "PHP 0.05", "PHP 0.01"]) {
      await userEvent.clear(screen.getByLabelText(`${label} quantity`));
      await userEvent.type(screen.getByLabelText(`${label} quantity`), "1");
    }

    expect(screen.getByTestId("opening-denomination-total")).toHaveTextContent("₱200.41");
    await userEvent.click(screen.getByRole("button", { name: "Review opening count" }));
    expect(screen.getByRole("status")).toHaveTextContent("₱200.41");
    await userEvent.click(screen.getByRole("button", { name: "Confirm opening" }));

    await waitFor(() => expect(openOrResumeCustody).toHaveBeenCalledTimes(1));
    const submitted = openOrResumeCustody.mock.calls[0][1];
    expect(submitted).toHaveLength(13);
    expect(submitted).toContainEqual({ denominationCode: "PHP-200", denominationValueMinorUnits: 20_000, quantity: 1 });
    expect(submitted).toContainEqual({ denominationCode: "PHP-1000", denominationValueMinorUnits: 100_000, quantity: 0 });
    expect(submitted).toContainEqual({ denominationCode: "PHP-0.01", denominationValueMinorUnits: 1, quantity: 1 });
  });

  it("labels historical custody without denomination evidence as a legacy record", async () => {
    const history = closedCustodySummary("88888888-8888-4888-8888-888888888888", 500, 505, 5, 2, 0);
    const state = {
      ...authenticatedState(),
      latestClosedCashCustody: history,
      closedCashCustodyHistory: [history],
    };
    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Cashier Session" }));

    expect(screen.getAllByText("Legacy record — denomination breakdown unavailable")).toHaveLength(2);
  });

  it("does not let mock Central PMS mode bypass the host without an explicit loopback fixture flag", () => {
    const config = mode1Config();
    window.history.replaceState({}, "", "/");
    expect(config.centralPmsConnectionMode).toBe("mock");
    expect(mayUseDevelopmentHumanSessionFixture(config)).toBe(false);

    window.history.replaceState({}, "", "/?humanSessionFixture=1");
    expect(mayUseDevelopmentHumanSessionFixture(config)).toBe(true);
    expect(createDevelopmentHumanSessionBridge(config)).toBeDefined();
  });
});

function unauthenticatedState(deviceTrusted = true): HumanSessionState {
  return {
    authenticationState: "UNAUTHENTICATED",
    authenticated: false,
    deviceTrusted,
    shiftOperationsAuthorized: false,
    custodyOperationsAuthorized: false,
    cashOperationsAuthorized: false,
    userReference: null,
    username: null,
    displayName: null,
    audience: null,
    assurance: null,
    privilegedAccount: false,
    mfaRequired: false,
    idleExpiresAt: null,
    absoluteExpiresAt: null,
    safeSupportReference: "Unavailable",
    safeMessage: deviceTrusted ? "Cashier sign-in is required." : "Device trust is unavailable.",
    errorCode: null,
    retryable: false,
    activeShift: null,
    activeCashCustodySession: null,
  };
}

function authenticatedState(): HumanSessionState {
  const now = new Date().toISOString();
  return {
    authenticationState: "AUTHENTICATED",
    authenticated: true,
    deviceTrusted: true,
    shiftOperationsAuthorized: true,
    custodyOperationsAuthorized: true,
    cashOperationsAuthorized: true,
    userReference: cashierGuid,
    username: "cashier.synthetic",
    displayName: "Synthetic Cashier",
    audience: "APT",
    assurance: "PASSWORD",
    privilegedAccount: false,
    mfaRequired: false,
    idleExpiresAt: now,
    absoluteExpiresAt: now,
    safeSupportReference: "APT-ABC12345",
    safeMessage: "Current cashier authority confirmed online.",
    errorCode: null,
    retryable: false,
    activeShift: {
      id: "SHIFT-SYNTHETIC",
      cashierId: cashierGuid,
      authenticatedCashierSessionReference: sessionGuid,
      terminalId: "APT-TERMINAL-001",
      siteId: "SITE-SYNTHETIC",
      siteGroupId: "SITE-GROUP-SYNTHETIC",
      posServerId: "POS-SYNTHETIC",
      openedAt: now,
      closedAt: null,
      status: "Open",
    },
    activeCashCustodySession: {
      id: "66666666-6666-4666-8666-666666666666",
      cashierId: cashierGuid,
      authenticatedCashierSessionReference: sessionGuid,
      cashierShiftId: "SHIFT-SYNTHETIC",
      terminalId: "APT-TERMINAL-001",
      siteId: "SITE-SYNTHETIC",
      siteGroupId: "SITE-GROUP-SYNTHETIC",
      posServerId: "POS-SYNTHETIC",
      openingCashAmount: 100,
      openedAt: now,
      status: "Open",
    },
  };
}

function movementState(movements: CashCustodyMovement[]): HumanSessionState {
  const state = authenticatedState();
  return {
    ...state,
    movementInitiationAuthorized: true,
    movementReadAuthorized: true,
    movementCorrectionAuthorized: false,
    cashCustodyDenominations: CASH_CUSTODY_DENOMINATIONS,
    activeCashCustodySummary: {
      session: state.activeCashCustodySession!,
      submissions: {
        physicalCashReceiptCount: 0,
        confirmedSubmissionCount: 0,
        pendingSubmissionCount: 0,
        conflictingSubmissionCount: 0,
        rejectedSubmissionCount: 0,
        unresolvedSubmissionCount: 0,
        pendingFiscalIssuanceCount: 0,
      },
      movements,
    },
  };
}

function movementFixture(status: CashCustodyMovement["status"]): CashCustodyMovement {
  const now = new Date().toISOString();
  return {
    id: "99999999-9999-4999-8999-999999999999",
    cashCustodySessionId: authenticatedState().activeCashCustodySession!.id,
    movementType: "SafeDrop",
    status,
    amountMinorUnits: 10_000,
    originalMovementId: null,
    initiatingCashierId: cashierGuid,
    intendedCounterpartyReference: receiverGuid,
    externalHandoverReference: "DROP-001",
    createdAt: now,
    denominations: CASH_CUSTODY_DENOMINATIONS.map((denomination) => ({
      denominationCode: denomination.code,
      denominationValueMinorUnits: denomination.valueMinorUnits,
      quantity: denomination.code === "PHP-100" ? 1 : 0,
      calculatedAmountMinorUnits: denomination.code === "PHP-100" ? 10_000 : 0,
    })),
    events: [{
      id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
      eventType: "Initiated",
      authorizedActorId: cashierGuid,
      actorSessionReference: sessionGuid,
      correlationId: "correlation-movement-001",
      occurredAt: now,
    }],
  };
}

function closedCustodySummary(
  id: string,
  expected: number,
  actual: number,
  variance: number,
  receipts: number,
  unresolvedAtClose: number,
): CashCustodySummary {
  return {
    session: {
      ...authenticatedState().activeCashCustodySession!,
      id,
      status: "Closed",
      expectedClosingCashAmount: expected,
      closingCashAmount: actual,
      varianceAmount: variance,
      closedAt: "2026-10-21T08:00:00+08:00",
      unresolvedSubmissionCountAtClose: unresolvedAtClose,
    },
    submissions: {
      physicalCashReceiptCount: receipts,
      confirmedSubmissionCount: receipts - unresolvedAtClose,
      pendingSubmissionCount: unresolvedAtClose,
      conflictingSubmissionCount: 0,
      rejectedSubmissionCount: 0,
      unresolvedSubmissionCount: unresolvedAtClose,
      pendingFiscalIssuanceCount: 0,
    },
  };
}

function bridgeStub(overrides: Partial<HumanSessionBridge> = {}): HumanSessionBridge {
  const result = async (): Promise<HumanSessionBridgeResult> => ({
    ok: true,
    command: "test",
    correlationId: "corr",
    payload: authenticatedState(),
  });
  return {
    restore: result,
    login: result,
    refresh: result,
    reauthenticate: result,
    logout: result,
    openOrResumeShift: result,
    openOrResumeCustody: result,
    closeOwnCustody: result,
    closeOwnShift: result,
    authorizeCash: result,
    listCashCustodyMovementCounterparties: async () => successResult("humanSession.listCashCustodyMovementCounterparties", {
      ...authenticatedState(),
      movementCounterparties: [{
        userReference: receiverGuid,
        displayName: "Synthetic Receiver",
        roleCode: "OPERATIONS_SUPERVISOR",
        roleLabel: "Operations Supervisor",
      }],
    }),
    initiateCashCustodyMovement: result,
    acknowledgeCashCustodyMovement: result,
    cancelCashCustodyMovement: result,
    appendCashCustodyMovementNote: result,
    ...overrides,
  };
}

function successResult(command: string, payload: HumanSessionState): HumanSessionBridgeResult {
  return { ok: true, command, correlationId: "corr", payload };
}

function captureAuthorityValidationTimer() {
  let callback: (() => void) | null = null;
  vi.spyOn(window, "setInterval").mockImplementation((handler: TimerHandler, timeout?: number) => {
    if (timeout === 60_000 && typeof handler === "function") {
      callback = handler as () => void;
    }
    return 1 as unknown as ReturnType<typeof window.setInterval>;
  });

  return {
    async run() {
      expect(callback).not.toBeNull();
      await act(async () => {
        callback!();
        await Promise.resolve();
      });
    },
  };
}
