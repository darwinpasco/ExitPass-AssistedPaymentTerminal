import type { CashCustodySessionSnapshot, CashierShiftSnapshot } from "./localJournalBridge";
import type { AptConfig } from "./config";
import {
  CASH_CUSTODY_DENOMINATIONS,
  denominationTotalMinorUnits,
  type CashCustodyDenominationDefinition,
  type CashCustodyDenominationLine,
} from "./cashCustodyDenominations";

export type CashCustodySubmissionSummary = {
  physicalCashReceiptCount: number;
  confirmedSubmissionCount: number;
  pendingSubmissionCount: number;
  conflictingSubmissionCount: number;
  rejectedSubmissionCount: number;
  unresolvedSubmissionCount: number;
  pendingFiscalIssuanceCount: number;
};

export type CashCustodySummary = {
  session: CashCustodySessionSnapshot;
  submissions: CashCustodySubmissionSummary;
  movements?: CashCustodyMovement[] | null;
};

export type CashCustodyMovementType = "SafeDrop" | "DirectRemittance" | "CashReturn";
export type CashCustodyMovementStatus = "PendingAcknowledgment" | "Completed" | "Cancelled" | "Rejected";
export type CashCustodyMovementEventType = "Initiated" | "Completed" | "Cancelled" | "Rejected" | "CorrectionNoted" | "DisputeRaised" | "DisputeResolved";

export type CashCustodyMovement = {
  id: string;
  cashCustodySessionId: string;
  movementType: CashCustodyMovementType;
  status: CashCustodyMovementStatus;
  amountMinorUnits: number;
  originalMovementId?: string | null;
  initiatingCashierId: string;
  intendedCounterpartyReference: string;
  externalHandoverReference: string;
  createdAt: string;
  denominations: Array<CashCustodyDenominationLine & { calculatedAmountMinorUnits: number }>;
  events: Array<{
    id: string;
    eventType: CashCustodyMovementEventType;
    authorizedActorId: string;
    actorSessionReference: string;
    correlationId: string;
    reasonCode?: string | null;
    controlledMetadata?: string | null;
    occurredAt: string;
  }>;
};

export type InitiateCashCustodyMovementPayload = {
  movementType: CashCustodyMovementType;
  originalMovementId: string | null;
  intendedCounterpartyReference: string;
  externalHandoverReference: string;
  idempotencyKey: string;
  denominations: CashCustodyDenominationLine[];
};

export type HumanSessionState = {
  authenticationState: "LOADING" | "UNAUTHENTICATED" | "AUTHENTICATED" | "LOCKED" | "UNAVAILABLE" | string;
  authenticated: boolean;
  deviceTrusted: boolean;
  shiftOperationsAuthorized: boolean;
  custodyOperationsAuthorized: boolean;
  cashOperationsAuthorized: boolean;
  movementInitiationAuthorized?: boolean;
  movementReadAuthorized?: boolean;
  movementCorrectionAuthorized?: boolean;
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
  activeCashCustodySummary?: CashCustodySummary | null;
  latestClosedCashCustody?: CashCustodySummary | null;
  closedCashCustodyHistory?: CashCustodySummary[] | null;
  cashCustodyDenominations?: CashCustodyDenominationDefinition[] | null;
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
  openOrResumeCustody(correlationId: string, denominations: CashCustodyDenominationLine[]): Promise<HumanSessionBridgeResult>;
  closeOwnCustody(correlationId: string, denominations: CashCustodyDenominationLine[]): Promise<HumanSessionBridgeResult>;
  closeOwnShift(correlationId: string): Promise<HumanSessionBridgeResult>;
  authorizeCash(correlationId: string): Promise<HumanSessionBridgeResult>;
  initiateCashCustodyMovement(correlationId: string, payload: InitiateCashCustodyMovementPayload): Promise<HumanSessionBridgeResult>;
  acknowledgeCashCustodyMovement(correlationId: string, movementId: string, counterpartyUsername: string, reject: boolean, reasonCode: string | null, idempotencyKey: string): Promise<HumanSessionBridgeResult>;
  cancelCashCustodyMovement(correlationId: string, movementId: string, reasonCode: string, idempotencyKey: string): Promise<HumanSessionBridgeResult>;
  appendCashCustodyMovementNote(correlationId: string, movementId: string, noteType: CashCustodyMovementEventType, counterpartyUsername: string, reasonCode: string, controlledMetadata: string, idempotencyKey: string): Promise<HumanSessionBridgeResult>;
}

export function createWebViewHumanSessionBridge(): HumanSessionBridge {
  return {
    restore: (correlationId) => send("humanSession.restore", correlationId, {}),
    login: (correlationId, username) => send("humanSession.login", correlationId, { username }),
    refresh: (correlationId) => send("humanSession.refresh", correlationId, {}),
    reauthenticate: (correlationId) => send("humanSession.reauthenticate", correlationId, {}),
    logout: (correlationId) => send("humanSession.logout", correlationId, {}),
    openOrResumeShift: (correlationId) => send("humanSession.openOrResumeShift", correlationId, {}),
    openOrResumeCustody: (correlationId, denominations) => send("humanSession.openOrResumeCustody", correlationId, { denominations }),
    closeOwnCustody: (correlationId, denominations) => send("humanSession.closeOwnCustody", correlationId, { denominations }),
    closeOwnShift: (correlationId) => send("humanSession.closeOwnShift", correlationId, {}),
    authorizeCash: (correlationId) => send("humanSession.authorizeCash", correlationId, {}),
    initiateCashCustodyMovement: (correlationId, payload) => send("humanSession.initiateCashCustodyMovement", correlationId, payload),
    acknowledgeCashCustodyMovement: (correlationId, movementId, counterpartyUsername, reject, reasonCode, idempotencyKey) =>
      send("humanSession.acknowledgeCashCustodyMovement", correlationId, { movementId, counterpartyUsername, reject, reasonCode, idempotencyKey }),
    cancelCashCustodyMovement: (correlationId, movementId, reasonCode, idempotencyKey) =>
      send("humanSession.cancelCashCustodyMovement", correlationId, { movementId, reasonCode, idempotencyKey }),
    appendCashCustodyMovementNote: (correlationId, movementId, noteType, counterpartyUsername, reasonCode, controlledMetadata, idempotencyKey) =>
      send("humanSession.appendCashCustodyMovementNote", correlationId, { movementId, noteType, counterpartyUsername, reasonCode, controlledMetadata, idempotencyKey }),
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
    movementInitiationAuthorized: true,
    movementReadAuthorized: true,
    movementCorrectionAuthorized: true,
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
    cashCustodyDenominations: CASH_CUSTODY_DENOMINATIONS,
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
    openOrResumeCustody: async (id, denominations) => {
      const shift = state.activeShift ?? developmentShift(config, state.userReference ?? "cashier-development-fixture");
      const openingCashAmount = denominationTotalMinorUnits(denominations) / 100;
      const openingDenominations = denominations.map((line) => ({
        ...line,
        calculatedAmountMinorUnits: line.denominationValueMinorUnits * line.quantity,
      }));
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
          openingDenominations,
          closingDenominations: [],
          openedAt: now.toISOString(),
          status: "Open",
        },
        activeCashCustodySummary: {
          session: {
            id: "00000000-0000-4000-8000-000000000008",
            cashierId: state.userReference ?? "cashier-development-fixture",
            authenticatedCashierSessionReference: "development-fixture-session",
            cashierShiftId: shift.id,
            terminalId: config.terminalId,
            siteId: config.siteId,
            siteGroupId: config.siteGroupId,
            posServerId: config.posServerId,
            openingCashAmount,
            openingDenominations,
            closingDenominations: [],
            openedAt: now.toISOString(),
            status: "Open",
          },
          submissions: emptySubmissionSummary(),
        },
      };
      return success("humanSession.openOrResumeCustody", id);
    },
    closeOwnCustody: async (id, denominations) => {
      const active = state.activeCashCustodySession;
      const summary = state.activeCashCustodySummary;
      const expectedCash = (active?.openingCashAmount ?? 0);
      const closingCashAmount = denominationTotalMinorUnits(denominations) / 100;
      const closingDenominations = denominations.map((line) => ({
        ...line,
        calculatedAmountMinorUnits: line.denominationValueMinorUnits * line.quantity,
      }));
      const closed: CashCustodySummary | null = active ? {
        session: {
          ...active,
          status: "Closed",
          expectedClosingCashAmount: expectedCash,
          closingCashAmount,
          varianceAmount: closingCashAmount - expectedCash,
          closingDenominations,
          closedAt: new Date().toISOString(),
          unresolvedSubmissionCountAtClose: summary?.submissions.unresolvedSubmissionCount ?? 0,
        },
        submissions: summary?.submissions ?? emptySubmissionSummary(),
      } : null;
      state = {
        ...state,
        activeCashCustodySession: null,
        activeCashCustodySummary: null,
        latestClosedCashCustody: closed,
        closedCashCustodyHistory: closed
          ? [closed, ...(state.closedCashCustodyHistory ?? []).filter((item) => item.session.id !== closed.session.id)]
          : state.closedCashCustodyHistory,
        cashOperationsAuthorized: false,
        safeMessage: "Cash custody closed. Closing cash and variance evidence were recorded.",
      };
      return success("humanSession.closeOwnCustody", id);
    },
    closeOwnShift: async (id) => {
      state = {
        ...state,
        activeShift: null,
        cashOperationsAuthorized: false,
        safeMessage: "Cashier shift closed.",
      };
      return success("humanSession.closeOwnShift", id);
    },
    authorizeCash: async (id) => success("humanSession.authorizeCash", id),
    initiateCashCustodyMovement: async (id, payload) => {
      const movement: CashCustodyMovement = {
        id: crypto.randomUUID(),
        cashCustodySessionId: state.activeCashCustodySession?.id ?? "",
        movementType: payload.movementType,
        status: "PendingAcknowledgment",
        amountMinorUnits: denominationTotalMinorUnits(payload.denominations),
        originalMovementId: payload.originalMovementId,
        initiatingCashierId: state.userReference ?? "cashier-development-fixture",
        intendedCounterpartyReference: payload.intendedCounterpartyReference,
        externalHandoverReference: payload.externalHandoverReference,
        createdAt: new Date().toISOString(),
        denominations: payload.denominations.map((line) => ({ ...line, calculatedAmountMinorUnits: line.denominationValueMinorUnits * line.quantity })),
        events: [],
      };
      if (state.activeCashCustodySummary) {
        state = { ...state, activeCashCustodySummary: { ...state.activeCashCustodySummary, movements: [movement, ...(state.activeCashCustodySummary.movements ?? [])] } };
      }
      return success("humanSession.initiateCashCustodyMovement", id);
    },
    acknowledgeCashCustodyMovement: async (id, movementId, _username, reject) => {
      if (state.activeCashCustodySummary) {
        state = { ...state, activeCashCustodySummary: { ...state.activeCashCustodySummary, movements: (state.activeCashCustodySummary.movements ?? []).map((movement) => movement.id === movementId ? { ...movement, status: reject ? "Rejected" : "Completed" } : movement) } };
      }
      return success("humanSession.acknowledgeCashCustodyMovement", id);
    },
    cancelCashCustodyMovement: async (id, movementId) => {
      if (state.activeCashCustodySummary) {
        state = { ...state, activeCashCustodySummary: { ...state.activeCashCustodySummary, movements: (state.activeCashCustodySummary.movements ?? []).map((movement) => movement.id === movementId ? { ...movement, status: "Cancelled" } : movement) } };
      }
      return success("humanSession.cancelCashCustodyMovement", id);
    },
    appendCashCustodyMovementNote: async (id) => success("humanSession.appendCashCustodyMovementNote", id),
  };
}

function emptySubmissionSummary(): CashCustodySubmissionSummary {
  return {
    physicalCashReceiptCount: 0,
    confirmedSubmissionCount: 0,
    pendingSubmissionCount: 0,
    conflictingSubmissionCount: 0,
    rejectedSubmissionCount: 0,
    unresolvedSubmissionCount: 0,
    pendingFiscalIssuanceCount: 0,
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
