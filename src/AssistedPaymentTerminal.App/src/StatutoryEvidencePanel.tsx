import { useEffect, useMemo, useRef, useState } from "react";
import { createCorrelationId } from "./correlation";
import type { StatutoryEvidenceRecoveryState } from "./api/centralPmsTypes";
import type {
  StatutoryEvidenceBridge,
  StatutoryEvidenceChannelResponse,
  StatutoryEvidenceFileSelection,
  StatutoryEvidenceUploadSession,
} from "./statutoryEvidenceBridge";

type Activity = "idle" | "bootstrapping" | "selecting" | "authorizing" | "uploading" | "finalizing" | "refreshing";

export function StatutoryEvidencePanel({
  decisionCommandId,
  restored,
  recovery,
  bridge,
  onRecoveryChange,
}: {
  decisionCommandId: string;
  restored: boolean;
  recovery?: StatutoryEvidenceRecoveryState | null;
  bridge: StatutoryEvidenceBridge;
  onRecoveryChange: (recovery: StatutoryEvidenceRecoveryState) => void;
}) {
  const [authoritative, setAuthoritative] = useState<StatutoryEvidenceChannelResponse | null>(null);
  const [selection, setSelection] = useState<StatutoryEvidenceFileSelection | null>(null);
  const [uploadSession, setUploadSession] = useState<StatutoryEvidenceUploadSession | null>(() => uploadSessionFromRecovery(recovery));
  const [activity, setActivity] = useState<Activity>("bootstrapping");
  const [error, setError] = useState<{ message: string; retryable: boolean } | null>(null);
  const mounted = useRef(true);

  useEffect(() => () => {
    mounted.current = false;
  }, []);

  useEffect(() => {
    let cancelled = false;
    async function bootstrap() {
      setActivity("bootstrapping");
      setError(null);
      const result = await bridge.bootstrap(createCorrelationId(), decisionCommandId, `apt-evidence-bootstrap:${decisionCommandId}`);
      if (cancelled) return;
      if (!result.ok) {
        setActivity("idle");
        setError({ message: result.error.message, retryable: result.error.retryable });
        return;
      }
      applyAuthoritative(result.payload, recovery?.opaqueUploadSessionReference ?? null, recovery?.uploadSessionExpiresAt ?? null, true);
      setActivity("idle");
    }
    void bootstrap();
    return () => {
      cancelled = true;
    };
  }, [bridge, decisionCommandId]);

  useEffect(() => {
    if (!authoritative || !shouldPoll(authoritative.lifecycleClassification)) return;
    const timer = window.setTimeout(() => void refreshStatus(), 4000);
    return () => window.clearTimeout(timer);
  }, [authoritative?.lifecycleClassification]);

  const canSelect = authoritative ? capturePermitted(authoritative) : false;
  const canUpload = Boolean(selection?.selectionReference) && activity === "idle" && canSelect;
  const canFinalize = Boolean(uploadSession?.opaqueUploadSessionReference) &&
    authoritative?.lifecycleClassification === "UPLOADED" && activity === "idle";
  const displayFacts = useMemo(() => authoritative ? [
    ["Lifecycle", friendly(authoritative.lifecycleClassification ?? "UNKNOWN_FAIL_CLOSED")],
    ["Required item", friendly(authoritative.requiredItemRole ?? authoritative.requiredDocumentType ?? "Not specified")],
    ["Allowed media", authoritative.allowedContentTypes.join(", ") || "Unavailable"],
    ["Maximum size", formatBytes(authoritative.maximumContentLengthBytes)],
    ["Replacement", friendly(authoritative.replacementPosture)],
    ["Ready for review", authoritative.readyForReview ? "Yes" : "No"],
    ["Ready for APT pre-cash", authoritative.readyForAptPreCash ? "Yes" : "No"],
    ["Evaluated", formatDate(authoritative.evaluatedAt)],
  ] : [], [authoritative]);

  function applyAuthoritative(
    response: StatutoryEvidenceChannelResponse,
    opaqueUploadSessionReference: string | null,
    uploadSessionExpiresAt: string | null,
    reselectionRequired: boolean,
  ) {
    setAuthoritative(response);
    setError(response.classification === "REJECTED"
      ? { message: response.safeMessage, retryable: response.retryable }
      : null);
    onRecoveryChange({
      authoritative: false,
      statutoryDiscountDecisionCommandId: decisionCommandId,
      evidenceSetReference: response.evidenceSetReference ?? null,
      evidenceItemReference: response.evidenceItemReference ?? null,
      opaqueUploadSessionReference,
      uploadSessionExpiresAt,
      lifecycleClassification: response.lifecycleClassification ?? "UNKNOWN_FAIL_CLOSED",
      replacementPosture: response.replacementPosture,
      readyForReview: response.readyForReview,
      readyForAptPreCash: response.readyForAptPreCash,
      retryable: response.retryable,
      blockingReasonCode: response.blockingReasonCode ?? null,
      correlationId: response.correlationId,
      lastSynchronizedAt: new Date().toISOString(),
      fileReselectionRequired: reselectionRequired,
    });
  }

  async function refreshStatus() {
    if (activity !== "idle" && activity !== "refreshing") return;
    setActivity("refreshing");
    const result = await bridge.status(createCorrelationId(), decisionCommandId);
    if (!mounted.current) return;
    setActivity("idle");
    if (!result.ok) {
      setError({ message: result.error.message, retryable: result.error.retryable });
      return;
    }
    const sessionReference = uploadSession?.opaqueUploadSessionReference ?? recovery?.opaqueUploadSessionReference ?? null;
    const sessionExpiry = uploadSession?.expiresAt ?? recovery?.uploadSessionExpiresAt ?? null;
    applyAuthoritative(result.payload, sessionReference, sessionExpiry, !selection?.selectionReference);
  }

  async function selectFile() {
    setActivity("selecting");
    setError(null);
    const result = await bridge.selectFile(createCorrelationId(), decisionCommandId);
    if (!mounted.current) return;
    setActivity("idle");
    if (!result.ok) {
      setError({ message: result.error.message, retryable: result.error.retryable });
      return;
    }
    if (!result.payload.cancelled) setSelection(result.payload);
  }

  async function uploadSelectedFile() {
    if (!selection?.selectionReference) return;
    setActivity("authorizing");
    setError(null);
    const operationKey = `apt-evidence-upload:${decisionCommandId}:${selection.selectionReference}`;
    const authorization = await bridge.createUploadSession(
      createCorrelationId(),
      decisionCommandId,
      selection.selectionReference,
      operationKey,
    );
    if (!mounted.current) return;
    if (!authorization.ok) {
      setActivity("idle");
      setError({ message: authorization.error.message, retryable: authorization.error.retryable });
      return;
    }

    const session = authorization.payload;
    const opaqueUploadSessionReference = session.opaqueUploadSessionReference;
    if (!opaqueUploadSessionReference || ["REJECTED", "SEMANTIC_CONFLICT"].includes(session.classification)) {
      setActivity("idle");
      setError({ message: session.safeMessage, retryable: session.retryable });
      return;
    }
    setUploadSession(session);
    if (authoritative) applyAuthoritative(authoritative, opaqueUploadSessionReference, session.expiresAt ?? null, false);
    setActivity("uploading");
    const uploaded = await bridge.upload(createCorrelationId(), opaqueUploadSessionReference);
    if (!mounted.current) return;
    if (!uploaded.ok) {
      setActivity("idle");
      await refreshStatus();
      if (mounted.current) setError({ message: uploaded.error.message, retryable: uploaded.error.retryable });
      return;
    }
    if (uploaded.payload.classification !== "ACCEPTED") {
      setActivity("idle");
      await refreshStatus();
      if (mounted.current) setError({ message: uploaded.payload.safeMessage, retryable: uploaded.payload.retryable });
      return;
    }

    await finalizeUpload(session);
  }

  async function finalizeUpload(session = uploadSession) {
    if (!session?.opaqueUploadSessionReference) return;
    setActivity("finalizing");
    setError(null);
    const result = await bridge.finalize(
      createCorrelationId(),
      session.opaqueUploadSessionReference,
      `apt-evidence-finalize:${session.opaqueUploadSessionReference}`,
    );
    if (!mounted.current) return;
    setActivity("idle");
    if (!result.ok) {
      setError({ message: result.error.message, retryable: result.error.retryable });
      return;
    }
    setSelection(null);
    applyAuthoritative(result.payload, null, null, false);
  }

  async function cancelUpload() {
    if (!uploadSession?.opaqueUploadSessionReference) return;
    const result = await bridge.cancelUpload(createCorrelationId(), uploadSession.opaqueUploadSessionReference);
    if (!result.ok) setError({ message: result.error.message, retryable: result.error.retryable });
    await refreshStatus();
  }

  return (
    <section className="statutory-evidence-panel" aria-label="Statutory evidence" data-testid="statutory-evidence-panel">
      <div className="statutory-evidence-heading">
        <div>
          <h4>Statutory evidence</h4>
          <p>{authoritative?.safeMessage ?? "Checking authoritative Central PMS evidence requirements..."}</p>
        </div>
        {authoritative && <span className={`readiness-pill ${authoritative.readyForAptPreCash ? "ready" : "blocked"}`}>{authoritative.readyForAptPreCash ? "Evidence ready" : "Cash blocked"}</span>}
      </div>

      {restored && <p className="status-copy">Recovered local evidence state was advisory only. Central PMS was queried again after restart.</p>}
      {activity !== "idle" && activity !== "refreshing" && <div role="status" aria-live="polite" className="evidence-progress"><progress aria-label="Evidence operation progress" /> <span>{activityLabel(activity)}</span></div>}
      {error && <p role="alert" className="status-notice error">{error.message}</p>}

      {authoritative && (
        <>
          <dl className="central-pms-details evidence-details">
            {displayFacts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
          </dl>
          {authoritative.maximumImageWidth && authoritative.maximumImageHeight && <p>Image limit: {authoritative.maximumImageWidth} x {authoritative.maximumImageHeight} pixels.</p>}
          {authoritative.replacementPosture === "REPLACEMENT_ALLOWED" && !["REQUIRED_NOT_STARTED", "ITEM_CREATED"].includes(authoritative.lifecycleClassification ?? "") && <p className="status-notice warning">Central PMS permits replacement. A new upload supersedes prior evidence under server policy.</p>}
          {authoritative.replacementPosture === "REPLACEMENT_NOT_ALLOWED" && authoritative.evidenceRequired && <p>Replacement is locked by authoritative Central PMS state. No local override is available.</p>}
        </>
      )}

      {selection?.selectionReference && (
        <dl className="central-pms-details selected-evidence" data-testid="selected-evidence-file">
          <div><dt>Selected file</dt><dd>{selection.displayName}</dd></div>
          <div><dt>Media type</dt><dd>{selection.contentType}</dd></div>
          <div><dt>Size</dt><dd>{formatBytes(selection.byteLength ?? 0)}</dd></div>
        </dl>
      )}

      {recovery?.fileReselectionRequired && authoritative?.evidenceRequired && canSelect && !selection && <p>The original file path was not restored. Select the image again to continue safely.</p>}

      <div className="statutory-actions evidence-actions">
        <button type="button" className="secondary-action" onClick={() => void selectFile()} disabled={!canSelect || activity !== "idle"}>Select JPEG or PNG</button>
        <button type="button" className="primary-action" onClick={() => void uploadSelectedFile()} disabled={!canUpload}>Upload evidence</button>
        {activity === "uploading" && <button type="button" className="secondary-action" onClick={() => void cancelUpload()}>Cancel upload</button>}
        {canFinalize && <button type="button" className="primary-action" onClick={() => void finalizeUpload()}>Retry finalization</button>}
        <button type="button" className="secondary-action" onClick={() => void refreshStatus()} disabled={activity !== "idle"}>Refresh evidence status</button>
      </div>
    </section>
  );
}

function capturePermitted(response: StatutoryEvidenceChannelResponse): boolean {
  if (!response.evidenceRequired || !response.evidenceSetReference || !response.evidenceItemReference) return false;
  return ["REQUIRED_NOT_STARTED", "ITEM_CREATED", "UPLOAD_SESSION_AVAILABLE"].includes(response.lifecycleClassification ?? "") ||
    response.replacementPosture === "REPLACEMENT_ALLOWED";
}

function shouldPoll(lifecycle?: string | null): boolean {
  return ["UPLOADED", "VALIDATION_PENDING", "SCAN_PENDING", "SCAN_RETRYABLE", "REVIEW_PENDING"].includes(lifecycle ?? "");
}

function uploadSessionFromRecovery(recovery?: StatutoryEvidenceRecoveryState | null): StatutoryEvidenceUploadSession | null {
  if (!recovery?.opaqueUploadSessionReference) return null;
  return {
    classification: "RECOVERED_ADVISORY_SESSION",
    retryable: true,
    correlationId: recovery.correlationId ?? "",
    opaqueUploadSessionReference: recovery.opaqueUploadSessionReference,
    method: "PUT",
    expiresAt: recovery.uploadSessionExpiresAt,
    acceptedContentType: "",
    maximumContentLengthBytes: 0,
    safeMessage: "Recovered upload session requires authoritative Central PMS reconciliation.",
  };
}

function activityLabel(activity: Activity): string {
  const labels: Record<Activity, string> = {
    idle: "Ready",
    bootstrapping: "Loading authoritative requirements",
    selecting: "Opening Windows file selection",
    authorizing: "Requesting a short-lived upload session",
    uploading: "Streaming evidence through the secure APT channel",
    finalizing: "Finalizing evidence with Central PMS",
    refreshing: "Refreshing authoritative evidence status",
  };
  return labels[activity];
}

function friendly(value: string): string {
  return value.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "Unavailable";
  if (value < 1024 * 1024) return `${Math.ceil(value / 1024)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(value?: string | null): string {
  if (!value) return "Unavailable";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "Unavailable" : parsed.toLocaleString();
}
