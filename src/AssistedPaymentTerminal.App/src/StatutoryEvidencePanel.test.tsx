import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { StatutoryEvidencePanel } from "./StatutoryEvidencePanel";
import type { StatutoryEvidenceBridge, StatutoryEvidenceBridgeResult, StatutoryEvidenceChannelResponse } from "./statutoryEvidenceBridge";

const decisionId = "77777777-7777-4777-8777-777777770777";

describe("StatutoryEvidencePanel", () => {
  it("bootstraps server-derived JPEG/PNG rules and persists advisory recovery only", async () => {
    const onRecoveryChange = vi.fn();
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={onRecoveryChange} />);

    expect(await screen.findByText("Image limit: 4096 x 4096 pixels.")).toBeVisible();
    expect(screen.getByText("image/jpeg, image/png")).toBeVisible();
    expect(screen.getByRole("button", { name: "Select JPEG or PNG" })).toBeEnabled();
    expect(onRecoveryChange).toHaveBeenCalledWith(expect.objectContaining({
      authoritative: false,
      statutoryDiscountDecisionCommandId: decisionId,
      lifecycleClassification: "REQUIRED_NOT_STARTED",
      readyForAptPreCash: false,
      fileReselectionRequired: true,
    }));
    const persisted = JSON.stringify(onRecoveryChange.mock.calls.at(-1)?.[0]);
    expect(persisted).not.toMatch(/objectKey|bucket|checksum|signedUrl|sourcePath|authorization/i);
  });

  it("streams a selected JPEG through an opaque session and finalizes without exposing storage internals", async () => {
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await screen.findByRole("button", { name: "Select JPEG or PNG" });

    await userEvent.click(screen.getByRole("button", { name: "Select JPEG or PNG" }));
    expect(await screen.findByText("synthetic-evidence.jpg")).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Upload evidence" }));

    await waitFor(() => expect(bridge.createUploadSession).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(bridge.upload).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(bridge.finalize).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("Evidence validation is pending.")).toBeVisible();
    expect(document.body.textContent).not.toMatch(/minio|s3|bucket|object key|signed url/i);
  });

  it("accepts PNG selection through the same bounded flow", async () => {
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"), { contentType: "image/png", displayName: "synthetic-evidence.png" });
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: "Select JPEG or PNG" }));
    expect(await screen.findByText("image/png")).toBeVisible();
  });

  it.each([
    ["UNSUPPORTED_MEDIA_TYPE", "Only JPEG and PNG files are supported."],
    ["FILE_TOO_LARGE", "The selected image exceeds the Central PMS maximum size."],
    ["EMPTY_FILE", "Select a non-empty JPEG or PNG file."],
  ])("renders safe local selection failure %s", async (code, message) => {
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    bridge.selectFile = vi.fn().mockResolvedValue(failure("statutoryEvidence.selectFile", code, message, false));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: "Select JPEG or PNG" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(message);
  });

  it("shows bounded upload progress and cancellation reconciliation", async () => {
    let resolveUpload!: (value: StatutoryEvidenceBridgeResult<never>) => void;
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    bridge.upload = vi.fn().mockImplementation(() => new Promise((resolve) => { resolveUpload = resolve; }));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: "Select JPEG or PNG" }));
    await userEvent.click(screen.getByRole("button", { name: "Upload evidence" }));

    expect(await screen.findByLabelText("Evidence operation progress")).toBeVisible();
    expect(screen.getByText("Streaming evidence through the secure APT channel")).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Cancel upload" }));
    expect(bridge.cancelUpload).toHaveBeenCalledTimes(1);
    await act(async () => {
      resolveUpload(failure("statutoryEvidence.upload", "UPLOAD_CANCELLED", "The local evidence operation was cancelled.", true) as never);
    });
  });

  it.each([
    ["REJECTED", "Authorization expired."],
    ["SEMANTIC_CONFLICT", "The upload request conflicts with authoritative state."],
  ])("does not stream when upload-session issuance returns %s", async (classification, safeMessage) => {
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    bridge.createUploadSession = vi.fn().mockResolvedValue(success("statutoryEvidence.createUploadSession", {
      classification,
      retryable: false,
      errorCode: classification === "REJECTED" ? "AUTHORIZATION_EXPIRED" : "SEMANTIC_CONFLICT",
      correlationId: "evidence-correlation",
      opaqueUploadSessionReference: "44444444-4444-4444-8444-444444440001",
      method: "PUT",
      expiresAt: "2026-08-05T12:00:00Z",
      acceptedContentType: "image/jpeg",
      maximumContentLengthBytes: 5 * 1024 * 1024,
      safeMessage,
    }));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: "Select JPEG or PNG" }));
    await userEvent.click(screen.getByRole("button", { name: "Upload evidence" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(safeMessage);
    expect(bridge.upload).not.toHaveBeenCalled();
    expect(bridge.finalize).not.toHaveBeenCalled();
  });

  it("does not finalize when the Central PMS upload relay rejects provider storage", async () => {
    const bridge = bridgeFor(response("REQUIRED_NOT_STARTED"));
    bridge.upload = vi.fn().mockResolvedValue(success("statutoryEvidence.upload", {
      classification: "REJECTED",
      retryable: true,
      errorCode: "PROVIDER_UNAVAILABLE",
      correlationId: "evidence-correlation",
      opaqueUploadSessionReference: "44444444-4444-4444-8444-444444440001",
      method: "PUT",
      expiresAt: "2026-08-05T12:00:00Z",
      acceptedContentType: "image/jpeg",
      maximumContentLengthBytes: 5 * 1024 * 1024,
      safeMessage: "Evidence storage is temporarily unavailable.",
    }));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: "Select JPEG or PNG" }));
    await userEvent.click(screen.getByRole("button", { name: "Upload evidence" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Evidence storage is temporarily unavailable.");
    expect(bridge.finalize).not.toHaveBeenCalled();
  });

  it("re-resolves after restart and never treats restored readiness as authority", async () => {
    const bridge = bridgeFor(response("APPLIED", { readyForAptPreCash: true }));
    const onRecoveryChange = vi.fn();
    render(<StatutoryEvidencePanel
      decisionCommandId={decisionId}
      restored
      recovery={{
        authoritative: false,
        statutoryDiscountDecisionCommandId: decisionId,
        lifecycleClassification: "STALE_LOCAL_STATE",
        readyForReview: false,
        readyForAptPreCash: false,
        retryable: false,
        lastSynchronizedAt: "2026-08-01T00:00:00Z",
        fileReselectionRequired: true,
      }}
      bridge={bridge}
      onRecoveryChange={onRecoveryChange}
    />);
    expect(await screen.findByText(/Central PMS was queried again after restart/)).toBeVisible();
    expect(bridge.bootstrap).toHaveBeenCalledTimes(1);
    expect(onRecoveryChange).toHaveBeenCalledWith(expect.objectContaining({ authoritative: false, readyForAptPreCash: true }));
  });

  it("disables replacement when Central PMS denies it", async () => {
    const bridge = bridgeFor(response("REVIEW_PENDING", { replacementPosture: "REPLACEMENT_NOT_ALLOWED" }));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    expect(await screen.findByText(/Replacement is locked/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Select JPEG or PNG" })).toBeDisabled();
  });

  it.each([
    ["VALIDATION_PENDING", "Evidence validation is pending."],
    ["VALIDATION_FAILED", "Evidence validation failed. Cash remains blocked."],
    ["SCAN_PENDING", "Evidence security scanning is pending."],
    ["SCAN_RETRYABLE", "Evidence security scanning is temporarily unavailable."],
    ["MALWARE_DETECTED", "The evidence was rejected by security scanning."],
    ["NOT_REVIEWABLE", "The evidence is not reviewable."],
    ["REVIEWABLE", "The evidence is ready for authorized review."],
    ["REVIEW_PENDING", "Authorized review is pending."],
    ["APPROVED", "The statutory request is approved but the payable basis is not yet applied."],
    ["REJECTED", "The statutory request was rejected."],
    ["APPLIED", "Evidence and the statutory payable basis are applied."],
    ["UNKNOWN_FAIL_CLOSED", "Central PMS could not establish a safe evidence state. Cash remains blocked."],
  ])("keeps lifecycle %s distinct", async (lifecycle, safeMessage) => {
    const bridge = bridgeFor(response(lifecycle, { safeMessage, readyForAptPreCash: lifecycle === "APPLIED" }));
    render(<StatutoryEvidencePanel decisionCommandId={decisionId} restored={false} bridge={bridge} onRecoveryChange={vi.fn()} />);
    expect(await screen.findByText(safeMessage)).toBeVisible();
  });
});

function bridgeFor(initial: StatutoryEvidenceChannelResponse, selection: { contentType?: string; displayName?: string } = {}): StatutoryEvidenceBridge {
  const validationPending = response("VALIDATION_PENDING", { safeMessage: "Evidence validation is pending." });
  return {
    bootstrap: vi.fn().mockResolvedValue(success("statutoryEvidence.bootstrap", initial)),
    status: vi.fn().mockResolvedValue(success("statutoryEvidence.status", initial)),
    revalidate: vi.fn().mockResolvedValue(success("statutoryEvidence.revalidate", initial)),
    selectFile: vi.fn().mockResolvedValue(success("statutoryEvidence.selectFile", {
      cancelled: false,
      selectionReference: "33333333-3333-4333-8333-333333330001",
      displayName: selection.displayName ?? "synthetic-evidence.jpg",
      contentType: selection.contentType ?? "image/jpeg",
      byteLength: 1024,
    })),
    createUploadSession: vi.fn().mockResolvedValue(success("statutoryEvidence.createUploadSession", {
      classification: "ISSUED",
      retryable: false,
      correlationId: "evidence-correlation",
      opaqueUploadSessionReference: "44444444-4444-4444-8444-444444440001",
      method: "PUT",
      expiresAt: "2026-08-05T12:00:00Z",
      acceptedContentType: selection.contentType ?? "image/jpeg",
      maximumContentLengthBytes: 5 * 1024 * 1024,
      safeMessage: "Central PMS accepted the evidence upload operation.",
    })),
    upload: vi.fn().mockResolvedValue(success("statutoryEvidence.upload", {
      classification: "ACCEPTED",
      retryable: false,
      correlationId: "evidence-correlation",
      opaqueUploadSessionReference: "44444444-4444-4444-8444-444444440001",
      method: "PUT",
      expiresAt: "2026-08-05T12:00:00Z",
      acceptedContentType: selection.contentType ?? "image/jpeg",
      maximumContentLengthBytes: 5 * 1024 * 1024,
      safeMessage: "Central PMS accepted the evidence upload operation.",
    })),
    cancelUpload: vi.fn().mockResolvedValue(success("statutoryEvidence.cancelUpload", { cancelled: true, reconciliationRequired: true, safeMessage: "Reconcile with Central PMS." })),
    finalize: vi.fn().mockResolvedValue(success("statutoryEvidence.finalize", validationPending)),
  };
}

function response(lifecycle: string, overrides: Partial<StatutoryEvidenceChannelResponse> = {}): StatutoryEvidenceChannelResponse {
  return {
    classification: "RESOLVED",
    retryable: false,
    errorCode: null,
    correlationId: "evidence-correlation",
    sourceChannel: "ASSISTED_PAYMENT_TERMINAL",
    evidenceRequired: lifecycle !== "NOT_REQUIRED",
    evidenceSetReference: "11111111-1111-4111-8111-111111110001",
    evidenceItemReference: "22222222-2222-4222-8222-222222220001",
    allowedContentTypes: ["image/jpeg", "image/png"],
    maximumContentLengthBytes: 5 * 1024 * 1024,
    maximumImageWidth: 4096,
    maximumImageHeight: 4096,
    maximumImagePixelCount: 16_000_000,
    requiredDocumentType: "STATUTORY_ID_IMAGE",
    requiredItemRole: "PRIMARY_IDENTITY_EVIDENCE",
    lifecycleClassification: lifecycle,
    replacementPosture: "REPLACEMENT_ALLOWED",
    readyForReview: lifecycle === "REVIEWABLE" || lifecycle === "APPLIED",
    readyForAptPreCash: lifecycle === "APPLIED" || lifecycle === "NOT_REQUIRED",
    blockingReasonCode: lifecycle === "APPLIED" ? null : `STATUTORY_EVIDENCE_${lifecycle}`,
    evaluatedAt: "2026-08-05T10:00:00Z",
    safeMessage: lifecycle,
    ...overrides,
  };
}

function success<T>(command: string, payload: T): StatutoryEvidenceBridgeResult<T> {
  return { ok: true, command, correlationId: "evidence-correlation", payload };
}

function failure<T>(command: string, code: string, message: string, retryable: boolean): StatutoryEvidenceBridgeResult<T> {
  return { ok: false, command, correlationId: "evidence-correlation", error: { code, message, retryable } };
}
