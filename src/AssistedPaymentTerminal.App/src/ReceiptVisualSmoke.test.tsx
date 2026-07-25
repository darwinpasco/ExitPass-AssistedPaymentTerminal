import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ReceiptVisualSmokeShell, receiptVisualSmokeScenarios, shouldUseReceiptVisualSmoke } from "./ReceiptVisualSmoke";
import type { AptConfig } from "./config";
import { mode1Config } from "./test/testConfig";
import type { LocalJournalBridge } from "./localJournalBridge";

describe("ReceiptVisualSmokeShell", () => {
  it("is gated to development mode and an explicit query flag", () => {
    expect(shouldUseReceiptVisualSmoke("?receiptVisualSmoke=1", true)).toBe(true);
    expect(shouldUseReceiptVisualSmoke("?receiptVisualSmoke=1", false)).toBe(false);
    expect(shouldUseReceiptVisualSmoke("", true)).toBe(false);
  });

  it("does not mutate production settings when scenarios are selected", async () => {
    const bridge = createSmokeBridge();
    const config = enabledConfig();
    const before = { ...config };

    render(<ReceiptVisualSmokeShell config={config} bridge={bridge} />);
    expect(await screen.findByTestId("apt-terminal-shell")).toHaveAttribute("data-surface", "receipt-visual-smoke");
    await userEvent.click(screen.getByRole("button", { name: "Available" }));

    expect(config).toEqual(before);
    expect(screen.getByText("Development-only")).toBeInTheDocument();
  });

  it("offers every required scenario using the real receipt status and preview surface", async () => {
    const bridge = createSmokeBridge({ existingTender: true });

    render(<ReceiptVisualSmokeShell config={enabledConfig()} bridge={bridge} />);

    expect(await screen.findByLabelText("Non-live cash custody capture")).toBeInTheDocument();
    expect(screen.getByLabelText("Receipt visual smoke scenarios")).toBeInTheDocument();
    for (const scenario of receiptVisualSmokeScenarios) {
      expect(screen.getByRole("button", { name: scenario.label })).toBeInTheDocument();
    }

    await waitFor(() => expect(screen.getByLabelText("Central PMS receipt availability")).toBeInTheDocument());
    await userEvent.click(screen.getByRole("button", { name: "Retrieve / Check Receipt" }));
    expect(screen.getByText("Sales Invoice is temporarily unavailable")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: POS_SERVER_RECEIPT_PRESENTATION_UNAVAILABLE")).toBeInTheDocument();
    expect(screen.getByText("Retry receipt retrieval when eligible.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retrieve / Check Receipt" })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("TERMINAL_CASH_PAYMENT_NOT_FOUND");
    expect(document.body).not.toHaveTextContent("Receipt inconsistency - support review required");
    expect(document.body).not.toHaveTextContent("Sales Invoice format is not supported");
    expect(document.body).not.toHaveTextContent("Sales Invoice response could not be read");
    expect(screen.queryByText("SALES INVOICE")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Retrieve / Check Receipt" }));
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).toHaveBeenCalledTimes(2);
    expect(bridge.submitOrReadbackCentralPmsCashSubmission).not.toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
  });

  it("labels local custody as historical while current payment and fiscal states remain visible", async () => {
    const bridge = createSmokeBridge({ existingTender: true });

    render(<ReceiptVisualSmokeShell config={enabledConfig()} bridge={bridge} />);

    await waitFor(() => expect(screen.getByLabelText("Central PMS receipt availability")).toBeInTheDocument());

    expect(screen.getByText(/State at local cash capture:/)).toBeInTheDocument();
    expect(screen.getByText(/At this checkpoint, canonical payment had not yet been submitted/)).toBeInTheDocument();
    expect(screen.queryByText(/^Local cash only\./)).not.toBeInTheDocument();
    expect(screen.getByText("Canonical payment confirmed")).toBeInTheDocument();
    expect(screen.getByText("Fiscal document recorded")).toBeInTheDocument();
    expect(screen.getByText("Local cash custody: cash received locally.")).toBeInTheDocument();
  });

  it("does not execute payment, fiscal, receipt, or preview commands before the cashier acts", async () => {
    const bridge = createSmokeBridge();

    render(<ReceiptVisualSmokeShell config={enabledConfig()} bridge={bridge} />);
    await screen.findByLabelText("Non-live cash custody capture");

    expect(bridge.startTender).not.toHaveBeenCalled();
    expect(bridge.recordCashReceived).not.toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashSubmission).not.toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
    expect(bridge.getCentralPmsCashReceiptPreview).not.toHaveBeenCalled();
    expect(bridge.submitCentralPmsCashReceiptPrint).not.toHaveBeenCalled();
  });
});

function enabledConfig(): AptConfig {
  return {
    ...mode1Config(),
    nonLiveCashCaptureEnabled: true,
    centralPmsCashSubmissionEnabled: true,
    centralPmsFiscalIssuanceEnabled: true,
    centralPmsReceiptRetrievalEnabled: true,
    receiptPreviewEnabled: true,
    receiptPrintingEnabled: true,
    receiptPrinterName: "APT Controlled Printer",
    centralPmsBaseUrl: "http://127.0.0.1:5180",
  };
}

function createSmokeBridge({ existingTender = false }: { existingTender?: boolean } = {}) {
  const now = new Date("2026-07-22T00:00:00Z").toISOString();
  const tender = {
    id: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2001",
    cashCustodySessionId: "session-visual-smoke",
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa2001",
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd2001",
    currency: "PHP",
    amountDue: 125,
    amountTendered: 150,
    changeDue: 25,
    correlationId: "corr-local",
    localIdempotencyIdentity: "visual-smoke",
    currentLocalState: "CashReceived",
    createdAt: now,
    updatedAt: now,
  };

  const bridge = {
    health: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "localJournal.health",
      correlationId,
      payload: {
        healthy: true,
        enabled: true,
        databasePath: "D:\\Temp\\receipt-visual-smoke.db",
        cashDrawerEnabled: false,
        authorityWarning: "development fixture",
      },
    })),
    createOrGetDevelopmentSession: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "localJournal.createOrGetDevelopmentSession",
      correlationId,
      payload: {
        id: "session-visual-smoke",
        cashierId: "cashier",
        authenticatedCashierSessionReference: "auth",
        cashierShiftId: "shift",
        terminalId: "terminal",
        siteId: "site",
        siteGroupId: "site-group",
        posServerId: "pos",
        openingCashAmount: 0,
        openedAt: now,
        status: "Open",
      },
    })),
    startTender: vi.fn(),
    recordCashReceived: vi.fn(),
    readTenderByParkingSession: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "localJournal.readTenderByParkingSession",
      correlationId,
      payload: { tender: existingTender ? tender : null, events: [] },
    })),
    getCentralPmsCashSubmissionStatus: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashSubmission.getStatus",
      correlationId,
      payload: {
        enabled: true,
        configurationValid: true,
        configurationMessage: "Configured",
        command: {
          localCommandId: "payment-command",
          terminalCashTenderId: tender.id,
          cashCustodySessionId: tender.cashCustodySessionId,
          status: "Confirmed",
          statusLabel: "Canonical payment confirmed",
          attemptCount: 1,
          originalCorrelationId: "corr-payment",
          resultClassification: "CREATED",
          canonicalPaymentAttemptId: "payment-attempt",
          canonicalPaymentConfirmationId: "payment-confirmation",
          confirmedAt: now,
          nextRetryAt: null,
          lastSafeHttpStatus: null,
          lastSafeErrorCode: null,
          createdAt: now,
          updatedAt: now,
        },
      },
    })),
    submitOrReadbackCentralPmsCashSubmission: vi.fn(),
    getCentralPmsCashFiscalStatus: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashFiscal.getStatus",
      correlationId,
      payload: {
        enabled: true,
        configurationValid: true,
        configurationMessage: "Configured",
        command: {
          localFiscalCommandId: "fiscal-command",
          terminalCashTenderId: tender.id,
          relatedCashPaymentOutboxCommandId: "payment-command",
          canonicalPaymentAttemptId: "payment-attempt",
          canonicalPaymentConfirmationId: "payment-confirmation",
          status: "Recorded",
          statusLabel: "Fiscal document recorded",
          attemptCount: 1,
          fiscalCorrelationId: "corr-fiscal",
          resultClassification: "NEWLY_CREATED",
          fiscalIssuanceReferenceId: "fiscal-reference",
          fiscalIssuanceState: "FISCAL_ISSUANCE_RECORDED",
          posFiscalDocumentId: "pos-fiscal-document",
          fiscalDocumentNumber: "SI-000001",
          fiscalNumberAssignedAt: now,
          semanticHashSourceVersion: "v1",
          recordedAt: now,
          nextRetryAt: null,
          lastSafeHttpStatus: null,
          lastSafeErrorCode: null,
          createdAt: now,
          updatedAt: now,
        },
      },
    })),
    submitOrReadbackCentralPmsCashFiscal: vi.fn(),
    getCentralPmsCashReceiptPrintStatus: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashReceiptPrint.getStatus",
      correlationId,
      payload: {
        enabled: true,
        configurationValid: true,
        configurationMessage: "Configured",
        command: unavailableReceiptCommand(tender, now),
        jobs: [],
      },
    })),
    submitCentralPmsCashReceiptPrint: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashReceiptPrint.submit",
      correlationId,
      payload: {
        safeMessage: "Submitted to controlled printer.",
        job: {
          printJobId: "print-job",
          terminalCashTenderId: tender.id,
          localReceiptRetrievalId: "receipt-command",
          fiscalIssuanceReferenceId: "fiscal-reference",
          posFiscalDocumentId: "pos-fiscal-document",
          fiscalDocumentNumber: "SI-000001",
          presentationVersion: "digital-sales-invoice-presentation-json-v1",
          templateVersion: "digital-sales-invoice-json-v1",
          authoritativePayloadHash: "sha256:receipt-payload",
          semanticRequestHash: "sha256:fiscal-semantic",
          paperWidthMm: 57,
          paperProfileId: "receipt-paper-57",
          configuredPrinterName: "APT Controlled Printer",
          classification: "Original",
          classificationLabel: "Original",
          copySequence: 1,
          status: "SubmittedToSpooler",
          statusLabel: "Submitted to printer",
          requestedAt: now,
          requestedBy: null,
          submissionStartedAt: now,
          submittedToSpoolerAt: now,
          completedAt: null,
          failedAt: null,
          failureClassification: null,
          retryable: false,
          windowsSpoolerJobId: "visual-smoke-spooler-1",
          lastUpdatedAt: now,
          correlationId: "corr-print",
        },
        printDocument: {
          terminalCashTenderId: tender.id,
          fiscalDocumentId: "pos-fiscal-document",
          fiscalDocumentNumber: "SI-000001",
          authoritativePayloadHash: "sha256:receipt-payload",
          semanticRequestHash: "sha256:fiscal-semantic",
          classification: "Original",
          copySequence: 1,
          reprintedAt: null,
          reprintMarker: null,
          paperProfile: {
            id: "receipt-paper-57",
            paperWidthMm: 57,
            printableWidthMm: 48,
            innerMarginMm: 4,
            fontScale: 0.92,
            monetaryColumnBehavior: "compact-right-aligned",
            metadataDensity: "compact",
          },
          lines: ["SALES INVOICE", "Fiscal doc: SI-000001"],
        },
      },
    })),
    getSalesInvoicePrintHistoryForTender: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "salesInvoicePrintHistory.getForTender",
      correlationId,
      payload: {
        scope: "terminalCashTenderId",
        summary: {
          hasHistory: false,
          originalStatus: "No print attempts recorded",
          reprintCount: 0,
          latestCopySequence: null,
          latestStatus: "No print attempts recorded",
          latestPrinterName: null,
          latestPaperWidthMm: null,
          latestAttemptAt: null,
          requiresConfirmation: false,
          attentionRequired: false,
        },
        jobs: [],
        indicators: [
          {
            code: "NO_PRINT_HISTORY",
            label: "No print attempts recorded",
            severity: "info",
            message: "No local Sales Invoice print attempt is recorded for this scope.",
          },
        ],
      },
    })),
    getSalesInvoicePrintHistoryForFiscalDocument: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "salesInvoicePrintHistory.getForFiscalDocument",
      correlationId,
      payload: {
        scope: "fiscalDocumentId",
        summary: {
          hasHistory: false,
          originalStatus: "No print attempts recorded",
          reprintCount: 0,
          latestCopySequence: null,
          latestStatus: "No print attempts recorded",
          latestPrinterName: null,
          latestPaperWidthMm: null,
          latestAttemptAt: null,
          requiresConfirmation: false,
          attentionRequired: false,
        },
        jobs: [],
        indicators: [],
      },
    })),
    getRecentSalesInvoicePrintHistory: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "salesInvoicePrintHistory.getRecent",
      correlationId,
      payload: {
        scope: "recent",
        summary: {
          hasHistory: false,
          originalStatus: "No print attempts recorded",
          reprintCount: 0,
          latestCopySequence: null,
          latestStatus: "No print attempts recorded",
          latestPrinterName: null,
          latestPaperWidthMm: null,
          latestAttemptAt: null,
          requiresConfirmation: false,
          attentionRequired: false,
        },
        jobs: [],
        indicators: [],
      },
    })),
    getSalesInvoicePrintHistoryDetail: vi.fn(),
    getCentralPmsCashReceiptStatus: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashReceipt.getStatus",
      correlationId,
      payload: {
        enabled: true,
        configurationValid: true,
        configurationMessage: "Configured",
        command: {
          localReceiptRetrievalId: "receipt-command",
          terminalCashTenderId: tender.id,
          relatedCashPaymentOutboxCommandId: "payment-command",
          relatedFiscalCommandId: "fiscal-command",
          canonicalPaymentAttemptId: "payment-attempt",
          canonicalPaymentConfirmationId: "payment-confirmation",
          canonicalPaymentStatus: "CONFIRMED",
          fiscalIssuanceReferenceId: "fiscal-reference",
          posFiscalDocumentId: "pos-fiscal-document",
          status: "Unavailable",
          statusLabel: "Sales Invoice is temporarily unavailable",
          attemptCount: 1,
          retrievalCorrelationId: "corr-receipt",
          resultClassification: null,
          receiptAvailabilityState: null,
          fiscalDocumentNumber: "SI-000001",
          fiscalDocumentStatus: "recorded",
          presentationVersion: null,
          templateVersion: null,
          semanticRequestHash: null,
          semanticRequestHashVersion: null,
          semanticRequestHashStatus: null,
          contentType: null,
          authoritativePayloadHash: null,
          voidStatus: null,
          voidReasonCode: null,
          voidedAt: null,
          retrievedAt: null,
          nextRetryAt: now,
          lastSafeHttpStatus: 503,
          lastSafeErrorCode: "POS_SERVER_RECEIPT_PRESENTATION_UNAVAILABLE",
          lastRetryable: true,
          lastCentralPmsCorrelationId: "corr-central-pms",
          lastUpdatedFromCentralPms: now,
          createdAt: now,
          updatedAt: now,
        },
      },
    })),
    retrieveOrCheckCentralPmsCashReceipt: vi.fn(async (correlationId: string) => ({
      ok: true,
      command: "centralPmsCashReceipt.retrieveOrCheck",
      correlationId,
      payload: {
        enabled: true,
        configurationValid: true,
        configurationMessage: "Configured",
        command: unavailableReceiptCommand(tender, now),
      },
    })),
    getCentralPmsCashReceiptPreview: vi.fn(),
  };

  return bridge as unknown as LocalJournalBridge & typeof bridge;
}

function unavailableReceiptCommand(tender: { id: string; cashCustodySessionId: string }, now: string) {
  return {
    localReceiptRetrievalId: "receipt-command",
    terminalCashTenderId: tender.id,
    relatedCashPaymentOutboxCommandId: "payment-command",
    relatedFiscalCommandId: "fiscal-command",
    canonicalPaymentAttemptId: "payment-attempt",
    canonicalPaymentConfirmationId: "payment-confirmation",
    canonicalPaymentStatus: "CONFIRMED",
    fiscalIssuanceReferenceId: "fiscal-reference",
    posFiscalDocumentId: "pos-fiscal-document",
    status: "Unavailable",
    statusLabel: "Sales Invoice is temporarily unavailable",
    attemptCount: 1,
    retrievalCorrelationId: "corr-receipt",
    resultClassification: null,
    receiptAvailabilityState: null,
    fiscalDocumentNumber: "SI-000001",
    fiscalDocumentStatus: "recorded",
    presentationVersion: null,
    templateVersion: null,
    semanticRequestHash: null,
    semanticRequestHashVersion: null,
    semanticRequestHashStatus: null,
    contentType: null,
    authoritativePayloadHash: null,
    voidStatus: null,
    voidReasonCode: null,
    voidedAt: null,
    retrievedAt: null,
    nextRetryAt: now,
    lastSafeHttpStatus: 503,
    lastSafeErrorCode: "POS_SERVER_RECEIPT_PRESENTATION_UNAVAILABLE",
    lastRetryable: true,
    lastCentralPmsCorrelationId: "corr-central-pms",
    lastUpdatedFromCentralPms: now,
    createdAt: now,
    updatedAt: now,
  } as const;
}
