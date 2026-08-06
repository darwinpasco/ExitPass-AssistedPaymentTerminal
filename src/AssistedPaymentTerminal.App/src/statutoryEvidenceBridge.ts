export type StatutoryEvidenceLifecycle =
  | "NOT_REQUIRED"
  | "REQUIRED_NOT_STARTED"
  | "ITEM_CREATED"
  | "UPLOAD_SESSION_AVAILABLE"
  | "UPLOAD_IN_PROGRESS"
  | "UPLOADED"
  | "VALIDATION_PENDING"
  | "VALIDATION_FAILED"
  | "SCAN_PENDING"
  | "SCAN_RETRYABLE"
  | "SCAN_FAILED"
  | "MALWARE_DETECTED"
  | "NOT_REVIEWABLE"
  | "REVIEWABLE"
  | "REVIEW_PENDING"
  | "APPROVED"
  | "REJECTED"
  | "APPLIED"
  | "UNKNOWN_FAIL_CLOSED";

export type StatutoryEvidenceChannelResponse = {
  classification: string;
  retryable: boolean;
  errorCode?: string | null;
  correlationId: string;
  sourceChannel: "ASSISTED_PAYMENT_TERMINAL" | string;
  evidenceRequired: boolean;
  evidenceSetReference?: string | null;
  evidenceItemReference?: string | null;
  allowedContentTypes: string[];
  maximumContentLengthBytes: number;
  maximumImageWidth?: number | null;
  maximumImageHeight?: number | null;
  maximumImagePixelCount?: number | null;
  requiredDocumentType?: string | null;
  requiredItemRole?: string | null;
  lifecycleClassification?: StatutoryEvidenceLifecycle | string | null;
  replacementPosture: "REPLACEMENT_ALLOWED" | "REPLACEMENT_NOT_ALLOWED" | string;
  readyForReview: boolean;
  readyForAptPreCash: boolean;
  blockingReasonCode?: string | null;
  evaluatedAt: string;
  safeMessage: string;
};

export type StatutoryEvidenceFileSelection = {
  cancelled: boolean;
  selectionReference?: string;
  displayName?: string;
  contentType?: string;
  byteLength?: number;
};

export type StatutoryEvidenceUploadSession = {
  classification: string;
  retryable: boolean;
  errorCode?: string | null;
  correlationId: string;
  opaqueUploadSessionReference?: string | null;
  method: string;
  expiresAt?: string | null;
  acceptedContentType: string;
  maximumContentLengthBytes: number;
  safeMessage: string;
};

export type StatutoryEvidenceBridgeError = {
  code: string;
  message: string;
  retryable: boolean;
};

export type StatutoryEvidenceBridgeResult<T> =
  | { ok: true; command: string; correlationId: string; payload: T }
  | { ok: false; command: string; correlationId: string; error: StatutoryEvidenceBridgeError };

export interface StatutoryEvidenceBridge {
  bootstrap(correlationId: string, decisionCommandId: string, clientOperationKey?: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>;
  status(correlationId: string, decisionCommandId: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>;
  revalidate(correlationId: string, decisionCommandId: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>;
  selectFile(correlationId: string, decisionCommandId: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceFileSelection>>;
  createUploadSession(correlationId: string, decisionCommandId: string, selectionReference: string, clientOperationKey: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceUploadSession>>;
  upload(correlationId: string, opaqueUploadSessionReference: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceUploadSession>>;
  cancelUpload(correlationId: string, opaqueUploadSessionReference: string): Promise<StatutoryEvidenceBridgeResult<{ cancelled: boolean; reconciliationRequired: boolean; safeMessage: string }>>;
  finalize(correlationId: string, opaqueUploadSessionReference: string, clientOperationKey: string): Promise<StatutoryEvidenceBridgeResult<StatutoryEvidenceChannelResponse>>;
}

export function createWebViewStatutoryEvidenceBridge(): StatutoryEvidenceBridge {
  return {
    bootstrap: (correlationId, decisionCommandId, clientOperationKey) => send("statutoryEvidence.bootstrap", correlationId, { statutoryDiscountDecisionCommandId: decisionCommandId, clientOperationKey }),
    status: (correlationId, decisionCommandId) => send("statutoryEvidence.status", correlationId, { statutoryDiscountDecisionCommandId: decisionCommandId }),
    revalidate: (correlationId, decisionCommandId) => send("statutoryEvidence.revalidate", correlationId, { statutoryDiscountDecisionCommandId: decisionCommandId }),
    selectFile: (correlationId, decisionCommandId) => send("statutoryEvidence.selectFile", correlationId, { statutoryDiscountDecisionCommandId: decisionCommandId }),
    createUploadSession: (correlationId, decisionCommandId, selectionReference, clientOperationKey) => send("statutoryEvidence.createUploadSession", correlationId, { statutoryDiscountDecisionCommandId: decisionCommandId, selectionReference, clientOperationKey }),
    upload: (correlationId, opaqueUploadSessionReference) => send("statutoryEvidence.upload", correlationId, { opaqueUploadSessionReference }),
    cancelUpload: (correlationId, opaqueUploadSessionReference) => send("statutoryEvidence.cancelUpload", correlationId, { opaqueUploadSessionReference }),
    finalize: (correlationId, opaqueUploadSessionReference, clientOperationKey) => send("statutoryEvidence.finalize", correlationId, { opaqueUploadSessionReference, clientOperationKey }),
  };
}

function send<T>(command: string, correlationId: string, payload: unknown): Promise<StatutoryEvidenceBridgeResult<T>> {
  const webview = window.chrome?.webview;
  if (!webview) {
    return Promise.resolve({
      ok: false,
      command,
      correlationId,
      error: {
        code: "BRIDGE_UNAVAILABLE",
        message: "The secure desktop evidence channel is unavailable.",
        retryable: false,
      },
    });
  }

  return new Promise((resolve) => {
    const listener = (event: { data: unknown }) => {
      const response = parseResponse<T>(event.data);
      if (!response || response.command !== command || response.correlationId !== correlationId) return;
      webview.removeEventListener("message", listener);
      resolve(response);
    };
    webview.addEventListener("message", listener);
    webview.postMessage(JSON.stringify({ source: "apt-statutory-evidence", command, correlationId, payload }));
  });
}

function parseResponse<T>(data: unknown): StatutoryEvidenceBridgeResult<T> | null {
  try {
    const parsed = typeof data === "string" ? JSON.parse(data) : data;
    if (!parsed || typeof parsed !== "object" || (parsed as { source?: string }).source !== "apt-statutory-evidence") return null;
    return parsed as StatutoryEvidenceBridgeResult<T>;
  } catch {
    return null;
  }
}
