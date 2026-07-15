export type BridgeResult<T> =
  | { ok: true; command: string; correlationId: string; payload: T }
  | { ok: false; command: string; correlationId: string; error: BridgeError };

export type BridgeError = {
  code: string;
  message: string;
  detail?: {
    existingCashTenderId?: string;
    existingCashTenderState?: string;
  };
};

export type LocalJournalHealth = {
  healthy: boolean;
  enabled: boolean;
  databasePath: string;
  cashDrawerEnabled: boolean;
  authorityWarning: string;
};

export type CashCustodySessionSnapshot = {
  id: string;
  cashierId: string;
  authenticatedCashierSessionReference: string;
  cashierShiftId: string;
  terminalId: string;
  siteId: string;
  siteGroupId: string;
  posServerId: string;
  openingCashAmount: number;
  openedAt: string;
  status: string;
};

export type CashTenderSnapshot = {
  id: string;
  cashCustodySessionId: string;
  parkingSessionId: string;
  tariffSnapshotId: string;
  currency: string;
  amountDue: number;
  amountTendered: number;
  changeDue: number;
  correlationId: string;
  localIdempotencyIdentity: string;
  currentLocalState: string;
  createdAt: string;
  updatedAt: string;
};

export type CashTenderEventSnapshot = {
  id: string;
  cashTenderId: string;
  eventType: string;
  occurredAt: string;
  amountTendered: number;
  changeDue: number;
  cashierAttested: boolean;
  actorCashierId: string;
  correlationId: string;
  denominationEntries: Array<{
    id: string;
    denominationCode: string;
    denominationValue: number;
    quantity: number;
  }>;
};

export type LocalTenderReadback = {
  tender: CashTenderSnapshot | null;
  events: CashTenderEventSnapshot[];
};

export type CreateDevelopmentSessionPayload = {
  cashierId: string;
  authenticatedCashierSessionReference: string;
  cashierShiftId: string;
  terminalId: string;
  siteId: string;
  siteGroupId: string;
  posServerId: string;
  openingCashAmount: number;
};

export type StartTenderPayload = {
  cashCustodySessionId: string;
  parkingSessionId: string;
  tariffSnapshotId: string;
  currency: string;
  amountDue: number;
  amountTendered: number;
  localIdempotencyIdentity: string;
};

export type RecordCashReceivedPayload = {
  localCashTenderId: string;
  cashierAttested: boolean;
  denominations: Array<{
    denominationCode: string;
    denominationValue: number;
    quantity: number;
  }>;
};

export interface LocalJournalBridge {
  health(correlationId: string): Promise<BridgeResult<LocalJournalHealth>>;
  createOrGetDevelopmentSession(
    correlationId: string,
    payload: CreateDevelopmentSessionPayload,
  ): Promise<BridgeResult<CashCustodySessionSnapshot>>;
  startTender(correlationId: string, payload: StartTenderPayload): Promise<BridgeResult<CashTenderSnapshot>>;
  recordCashReceived(correlationId: string, payload: RecordCashReceivedPayload): Promise<BridgeResult<CashTenderSnapshot>>;
  readTenderByParkingSession(correlationId: string, parkingSessionId: string): Promise<BridgeResult<LocalTenderReadback>>;
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: string): void;
        addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
        removeEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
      };
    };
  }
}

export function createWebViewLocalJournalBridge(): LocalJournalBridge {
  return {
    health: (correlationId) => send("localJournal.health", correlationId, {}),
    createOrGetDevelopmentSession: (correlationId, payload) =>
      send("localJournal.createOrGetDevelopmentSession", correlationId, payload),
    startTender: (correlationId, payload) => send("localJournal.startTender", correlationId, payload),
    recordCashReceived: (correlationId, payload) => send("localJournal.recordCashReceived", correlationId, payload),
    readTenderByParkingSession: (correlationId, parkingSessionId) =>
      send("localJournal.readTenderByParkingSession", correlationId, { parkingSessionId }),
  };
}

function send<T>(command: string, correlationId: string, payload: unknown): Promise<BridgeResult<T>> {
  const webview = window.chrome?.webview;
  if (!webview) {
    return Promise.resolve({
      ok: false,
      command,
      correlationId,
      error: {
        code: "bridge_unavailable",
        message: "Local journal bridge is unavailable outside the desktop host.",
      },
    });
  }

  return new Promise((resolve) => {
    const listener = (event: { data: unknown }) => {
      const response = parseResponse<T>(event.data);
      if (!response || response.correlationId !== correlationId || response.command !== command) {
        return;
      }

      webview.removeEventListener("message", listener);
      resolve(response);
    };

    webview.addEventListener("message", listener);
    webview.postMessage(
      JSON.stringify({
        source: "apt-local-journal",
        command,
        correlationId,
        payload,
      }),
    );
  });
}

function parseResponse<T>(data: unknown): BridgeResult<T> | null {
  const parsed = typeof data === "string" ? JSON.parse(data) : data;
  if (!parsed || typeof parsed !== "object" || (parsed as { source?: string }).source !== "apt-local-journal") {
    return null;
  }

  return parsed as BridgeResult<T>;
}
