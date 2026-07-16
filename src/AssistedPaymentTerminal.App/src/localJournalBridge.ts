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

export type CentralPmsCashSubmissionCommand = {
  localCommandId: string;
  terminalCashTenderId: string;
  cashCustodySessionId: string;
  status: "Pending" | "Submitting" | "ReadbackRequired" | "RetryPending" | "Confirmed" | "Conflict" | "Rejected";
  statusLabel: string;
  attemptCount: number;
  originalCorrelationId: string;
  resultClassification: string | null;
  canonicalPaymentAttemptId: string | null;
  canonicalPaymentConfirmationId: string | null;
  confirmedAt: string | null;
  nextRetryAt: string | null;
  lastSafeHttpStatus: number | null;
  lastSafeErrorCode: string | null;
  createdAt: string;
  updatedAt: string;
};

export type CentralPmsCashSubmissionStatus = {
  enabled: boolean;
  configurationValid: boolean;
  configurationMessage: string;
  command: CentralPmsCashSubmissionCommand | null;
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
  getCentralPmsCashSubmissionStatus(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashSubmissionStatus>>;
  submitOrReadbackCentralPmsCashSubmission(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashSubmissionStatus>>;
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
    getCentralPmsCashSubmissionStatus: (correlationId, localCashTenderId) =>
      send("centralPmsCashSubmission.getStatus", correlationId, { localCashTenderId }),
    submitOrReadbackCentralPmsCashSubmission: (correlationId, localCashTenderId) =>
      send("centralPmsCashSubmission.submitOrReadback", correlationId, { localCashTenderId }),
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
