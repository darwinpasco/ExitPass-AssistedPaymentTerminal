export type BridgeResult<T> =
  | { ok: true; command: string; correlationId: string; payload: T }
  | { ok: false; command: string; correlationId: string; error: BridgeError };

export type BridgeError = {
  code: string;
  message: string;
  detail?: {
    existingCashTenderId?: string;
    existingCashTenderState?: string;
    command?: CentralPmsCashReceiptCommand;
    paperProfile?: ReceiptPreviewPaperProfile;
    paperWidthWarning?: string | null;
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

export type CentralPmsCashFiscalCommand = {
  localFiscalCommandId: string;
  terminalCashTenderId: string;
  relatedCashPaymentOutboxCommandId: string;
  canonicalPaymentAttemptId: string;
  canonicalPaymentConfirmationId: string;
  status: "Pending" | "Submitting" | "ReadbackRequired" | "RetryPending" | "Recorded" | "Conflict" | "Rejected" | "Unknown";
  statusLabel: string;
  attemptCount: number;
  fiscalCorrelationId: string;
  resultClassification: string | null;
  fiscalIssuanceReferenceId: string | null;
  fiscalIssuanceState: string | null;
  posFiscalDocumentId: string | null;
  fiscalDocumentNumber: string | null;
  fiscalNumberAssignedAt: string | null;
  semanticHashSourceVersion: string | null;
  recordedAt: string | null;
  nextRetryAt: string | null;
  lastSafeHttpStatus: number | null;
  lastSafeErrorCode: string | null;
  createdAt: string;
  updatedAt: string;
};

export type CentralPmsCashFiscalStatus = {
  enabled: boolean;
  configurationValid: boolean;
  configurationMessage: string;
  command: CentralPmsCashFiscalCommand | null;
};

export type CentralPmsCashReceiptCommand = {
  localReceiptRetrievalId: string;
  terminalCashTenderId: string;
  relatedCashPaymentOutboxCommandId: string;
  relatedFiscalCommandId: string;
  canonicalPaymentAttemptId: string;
  canonicalPaymentConfirmationId: string;
  canonicalPaymentStatus: string | null;
  fiscalIssuanceReferenceId: string;
  posFiscalDocumentId: string;
  status:
    | "Pending"
    | "Retrieving"
    | "NotReady"
    | "RetryPending"
    | "Available"
    | "Voided"
    | "Rejected"
    | "Inconsistent"
    | "Unavailable"
    | "Unsupported"
    | "Malformed";
  statusLabel: string;
  attemptCount: number;
  retrievalCorrelationId: string;
  resultClassification: string | null;
  receiptAvailabilityState: string | null;
  fiscalDocumentNumber: string | null;
  fiscalDocumentStatus: string | null;
  presentationVersion: string | null;
  templateVersion: string | null;
  semanticRequestHash: string | null;
  semanticRequestHashVersion: string | null;
  semanticRequestHashStatus: string | null;
  contentType: string | null;
  authoritativePayloadHash: string | null;
  voidStatus: string | null;
  voidReasonCode: string | null;
  voidedAt: string | null;
  retrievedAt: string | null;
  nextRetryAt: string | null;
  lastSafeHttpStatus: number | null;
  lastSafeErrorCode: string | null;
  lastRetryable: boolean | null;
  lastCentralPmsCorrelationId: string | null;
  lastUpdatedFromCentralPms: string | null;
  createdAt: string;
  updatedAt: string;
};

export type CentralPmsCashReceiptStatus = {
  enabled: boolean;
  configurationValid: boolean;
  configurationMessage: string;
  command: CentralPmsCashReceiptCommand | null;
};

export type ReceiptPreviewPaperProfile = {
  id: "receipt-paper-57" | "receipt-paper-58" | "receipt-paper-80";
  paperWidthMm: 57 | 58 | 80;
  printableWidthMm: number;
  innerMarginMm: number;
  fontScale: number;
  monetaryColumnBehavior: string;
  metadataDensity: string;
};

export type ReceiptPreviewField = {
  key: string;
  label: string;
  value: string;
  isPlaceholder: boolean;
};

export type ReceiptPreviewRow = {
  fields: ReceiptPreviewField[];
};

export type ReceiptPreviewSection = {
  title: string;
  fields: ReceiptPreviewField[];
  rows: ReceiptPreviewRow[];
};

export type ReceiptPreviewDocument = {
  terminalCashTenderId: string;
  localReceiptRetrievalId: string;
  fiscalIssuanceReferenceId: string;
  posFiscalDocumentId: string;
  fiscalDocumentNumber: string | null;
  fiscalDocumentStatus: string | null;
  receiptAvailabilityState: string | null;
  presentationVersion: string | null;
  templateVersion: string | null;
  contentType: string | null;
  authoritativePayloadHash: string | null;
  semanticRequestHash: string | null;
  semanticRequestHashVersion: string | null;
  semanticRequestHashStatus: string | null;
  retrievedAt: string | null;
  retrievalCorrelationId: string;
  centralPmsCorrelationId: string | null;
  voided: boolean;
  voidStatus: string | null;
  voidReasonCode: string | null;
  voidedAt: string | null;
  paperProfile: ReceiptPreviewPaperProfile;
  hasPlaceholders: boolean;
  configurationCompleteness: "Incomplete" | "Complete";
  sections: ReceiptPreviewSection[];
};

export type CentralPmsCashReceiptPreview = {
  enabled: boolean;
  command: CentralPmsCashReceiptCommand;
  preview: ReceiptPreviewDocument;
  paperProfile: ReceiptPreviewPaperProfile;
  paperWidthWarning: string | null;
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
  localCashTenderId?: string;
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
  getCentralPmsCashFiscalStatus(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashFiscalStatus>>;
  submitOrReadbackCentralPmsCashFiscal(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashFiscalStatus>>;
  getCentralPmsCashReceiptStatus(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashReceiptStatus>>;
  retrieveOrCheckCentralPmsCashReceipt(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashReceiptStatus>>;
  getCentralPmsCashReceiptPreview(
    correlationId: string,
    localCashTenderId: string,
  ): Promise<BridgeResult<CentralPmsCashReceiptPreview>>;
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
    getCentralPmsCashFiscalStatus: (correlationId, localCashTenderId) =>
      send("centralPmsCashFiscal.getStatus", correlationId, { localCashTenderId }),
    submitOrReadbackCentralPmsCashFiscal: (correlationId, localCashTenderId) =>
      send("centralPmsCashFiscal.submitOrReadback", correlationId, { localCashTenderId }),
    getCentralPmsCashReceiptStatus: (correlationId, localCashTenderId) =>
      send("centralPmsCashReceipt.getStatus", correlationId, { localCashTenderId }),
    retrieveOrCheckCentralPmsCashReceipt: (correlationId, localCashTenderId) =>
      send("centralPmsCashReceipt.retrieveOrCheck", correlationId, { localCashTenderId }),
    getCentralPmsCashReceiptPreview: (correlationId, localCashTenderId) =>
      send("centralPmsCashReceipt.getPreview", correlationId, { localCashTenderId }),
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
