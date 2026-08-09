import type { CashCustodySessionSnapshot, CashierShiftSnapshot } from "./localJournalBridge";
import type { AptConfig } from "./config";

export type HumanSessionState = {
  authenticationState: "LOADING" | "UNAUTHENTICATED" | "AUTHENTICATED" | "LOCKED" | "UNAVAILABLE" | string;
  authenticated: boolean;
  deviceTrusted: boolean;
  shiftOperationsAuthorized: boolean;
  custodyOperationsAuthorized: boolean;
  cashOperationsAuthorized: boolean;
  userReference: string | null;
  username: string | null;
  displayName: string | null;
  audience: string | null;
  assurance: string | null;
  privilegedAccount: boolean;
  mfaRequired: boolean;
  idleExpiresAt: string | null;
  absoluteExpiresAt: string | null;
  safeSupportReference: string;
  safeMessage: string;
  errorCode: string | null;
  retryable: boolean;
  activeShift: CashierShiftSnapshot | null;
  activeCashCustodySession: CashCustodySessionSnapshot | null;
};

export type HumanSessionBridgeResult =
  | { ok: true; command: string; correlationId: string; payload: HumanSessionState }
  | { ok: false; command: string; correlationId: string; error: { code: string; message: string } };

export interface HumanSessionBridge {
  restore(correlationId: string): Promise<HumanSessionBridgeResult>;
  login(correlationId: string, username: string): Promise<HumanSessionBridgeResult>;
  refresh(correlationId: string): Promise<HumanSessionBridgeResult>;
  reauthenticate(correlationId: string): Promise<HumanSessionBridgeResult>;
  logout(correlationId: string): Promise<HumanSessionBridgeResult>;
  openOrResumeShift(correlationId: string): Promise<HumanSessionBridgeResult>;
  openOrResumeCustody(correlationId: string, openingCashAmount: number): Promise<HumanSessionBridgeResult>;
  authorizeCash(correlationId: string): Promise<HumanSessionBridgeResult>;
}

export function createWebViewHumanSessionBridge(): HumanSessionBridge {
  return {
    restore: (correlationId) => send("humanSession.restore", correlationId, {}),
    login: (correlationId, username) => send("humanSession.login", correlationId, { username }),
    refresh: (correlationId) => send("humanSession.refresh", correlationId, {}),
    reauthenticate: (correlationId) => send("humanSession.reauthenticate", correlationId, {}),
    logout: (correlationId) => send("humanSession.logout", correlationId, {}),
    openOrResumeShift: (correlationId) => send("humanSession.openOrResumeShift", correlationId, {}),
    openOrResumeCustody: (correlationId, openingCashAmount) => send("humanSession.openOrResumeCustody", correlationId, { openingCashAmount }),
    authorizeCash: (correlationId) => send("humanSession.authorizeCash", correlationId, {}),
  };
}

export function createDevelopmentHumanSessionBridge(config: AptConfig): HumanSessionBridge {
  const now = new Date();
  let state: HumanSessionState = {
    authenticationState: "AUTHENTICATED",
    authenticated: true,
    deviceTrusted: true,
    shiftOperationsAuthorized: true,
    custodyOperationsAuthorized: true,
    cashOperationsAuthorized: true,
    userReference: "cashier-development-fixture",
    username: "cashier.fixture",
    displayName: "Development Cashier",
    audience: "APT",
    assurance: "PASSWORD",
    privilegedAccount: false,
    mfaRequired: false,
    idleExpiresAt: new Date(now.getTime() + 15 * 60_000).toISOString(),
    absoluteExpiresAt: new Date(now.getTime() + 12 * 60 * 60_000).toISOString(),
    safeSupportReference: "APT-FIXTURE",
    safeMessage: "Explicit development fixture session.",
    errorCode: null,
    retryable: false,
    activeShift: null,
    activeCashCustodySession: null,
  };
  const success = (command: string, correlationId: string): HumanSessionBridgeResult => ({ ok: true, command, correlationId, payload: state });
  return {
    restore: async (id) => success("humanSession.restore", id),
    login: async (id) => success("humanSession.login", id),
    refresh: async (id) => success("humanSession.refresh", id),
    reauthenticate: async (id) => success("humanSession.reauthenticate", id),
    logout: async (id) => {
      state = {
        ...state,
        authenticationState: "UNAUTHENTICATED",
        authenticated: false,
        shiftOperationsAuthorized: false,
        custodyOperationsAuthorized: false,
        cashOperationsAuthorized: false,
      };
      return success("humanSession.logout", id);
    },
    openOrResumeShift: async (id) => {
      state = {
        ...state,
        activeShift: developmentShift(config, state.userReference ?? "cashier-development-fixture"),
      };
      return success("humanSession.openOrResumeShift", id);
    },
    openOrResumeCustody: async (id, openingCashAmount) => {
      const shift = state.activeShift ?? developmentShift(config, state.userReference ?? "cashier-development-fixture");
      state = {
        ...state,
        activeShift: shift,
        activeCashCustodySession: {
          id: "00000000-0000-4000-8000-000000000008",
          cashierId: state.userReference ?? "cashier-development-fixture",
          authenticatedCashierSessionReference: "development-fixture-session",
          cashierShiftId: shift.id,
          terminalId: config.terminalId,
          siteId: config.siteId,
          siteGroupId: config.siteGroupId,
          posServerId: config.posServerId,
          openingCashAmount,
          openedAt: now.toISOString(),
          status: "Open",
        },
      };
      return success("humanSession.openOrResumeCustody", id);
    },
    authorizeCash: async (id) => success("humanSession.authorizeCash", id),
  };
}

export function mayUseDevelopmentHumanSessionFixture(config: AptConfig): boolean {
  const loopback = ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);
  const explicitFixture = new URLSearchParams(window.location.search).get("humanSessionFixture") === "1";
  return config.centralPmsConnectionMode === "mock" && loopback && explicitFixture;
}

function developmentShift(config: AptConfig, cashierId: string): CashierShiftSnapshot {
  return {
    id: "SHIFT-DEVELOPMENT-FIXTURE",
    cashierId,
    authenticatedCashierSessionReference: "development-fixture-session",
    terminalId: config.terminalId,
    siteId: config.siteId,
    siteGroupId: config.siteGroupId,
    posServerId: config.posServerId,
    openedAt: new Date().toISOString(),
    closedAt: null,
    status: "Open",
  };
}

function send(command: string, correlationId: string, payload: unknown): Promise<HumanSessionBridgeResult> {
  return new Promise((resolve) => {
    const webview = window.chrome?.webview;
    if (!webview) {
      resolve({
        ok: false,
        command,
        correlationId,
        error: { code: "DESKTOP_HOST_REQUIRED", message: "Secure cashier login requires the Windows desktop host." },
      });
      return;
    }

    const listener = (event: { data: unknown }) => {
      const response = typeof event.data === "string" ? safeParse(event.data) : event.data;
      if (!isResponse(response, command, correlationId)) return;
      webview.removeEventListener("message", listener);
      resolve(response);
    };
    webview.addEventListener("message", listener);
    webview.postMessage(JSON.stringify({ source: "apt-human-session", command, correlationId, payload }));
  });
}

function safeParse(value: string): unknown {
  try { return JSON.parse(value); } catch { return null; }
}

function isResponse(value: unknown, command: string, correlationId: string): value is HumanSessionBridgeResult {
  const candidate = value as Partial<HumanSessionBridgeResult> | null;
  return Boolean(candidate && candidate.command === command && candidate.correlationId === correlationId && typeof candidate.ok === "boolean");
}
