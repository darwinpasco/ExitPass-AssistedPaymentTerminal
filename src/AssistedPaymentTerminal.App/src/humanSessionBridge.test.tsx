import { act, render, screen, waitFor } from "@testing-library/react";
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
} from "./humanSessionBridge";
import { mode1Config } from "./test/testConfig";

const cashierGuid = "44444444-4444-4444-8444-444444444444";
const sessionGuid = "55555555-5555-4555-8555-555555555555";

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

    expect(screen.getByRole("heading", { name: "Synthetic Cashier" })).toBeInTheDocument();
    expect(screen.getByText("Authentication locked for new cash")).toBeInTheDocument();
    expect(screen.getByText(/Physical cash custody remains open/)).toBeInTheDocument();
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
    expect(screen.getByRole("alert")).toHaveTextContent("Sign out unavailable");
    expect(screen.getByRole("alert")).toHaveTextContent("Sign out is unavailable while you have open cash custody.");
    expect(screen.getByText("Own custody").parentElement).toHaveTextContent("Open");
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
    expect(screen.getByText("Online cashier authority current")).toBeInTheDocument();
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

    expect(screen.getByRole("button", { name: "Open or resume own shift" })).toBeDisabled();

    const noCustodyAuthority = {
      ...authenticatedState(),
      custodyOperationsAuthorized: false,
      cashOperationsAuthorized: false,
      activeCashCustodySession: null,
    };
    rerender(<HumanSessionPanel state={noCustodyAuthority} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Open or resume own custody" })).toBeDisabled();
  });

  it("recognizes an authenticated owned open shift and enables custody without a manual refresh", () => {
    const state = {
      ...authenticatedState(),
      activeCashCustodySession: null,
    };

    render(<HumanSessionPanel state={state} bridge={bridgeStub()} onStateChange={vi.fn()} />);

    expect(screen.getByText("Own shift").parentElement).toHaveTextContent("Open");
    expect(screen.getByRole("button", { name: "Own shift resumed" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Open or resume own custody" })).toBeEnabled();
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
    authorizeCash: result,
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
