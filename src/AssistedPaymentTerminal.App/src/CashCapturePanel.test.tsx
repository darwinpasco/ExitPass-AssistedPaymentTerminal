import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { CashCapturePanel } from "./CashCapturePanel";
import type { ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import type { AptConfig } from "./config";
import type {
  BridgeResult,
  CashCustodySessionSnapshot,
  CashTenderSnapshot,
  CentralPmsCashFiscalStatus,
  CentralPmsCashReceiptPrintStatus,
  CentralPmsCashReceiptPrintSubmit,
  CentralPmsCashReceiptPreview,
  CentralPmsCashReceiptStatus,
  CentralPmsCashSubmissionStatus,
  LocalJournalBridge,
  LocalJournalHealth,
  LocalTenderReadback,
  RecordCashReceivedPayload,
  SalesInvoicePrintHistory,
  SalesInvoicePrintHistoryDetail,
  StartTenderPayload,
} from "./localJournalBridge";
import { mode1Config } from "./test/testConfig";
import { buildTerminalContext } from "./terminalContext";

describe("CashCapturePanel", () => {
  it("is hidden when non-live capture is disabled", () => {
    renderPanel({ config: mode1Config(), bridge: new FakeBridge() });

    expect(screen.queryByLabelText("Non-live cash custody capture")).not.toBeInTheDocument();
  });

  it("is unavailable for expired tariff", () => {
    renderPanel({ config: enabledConfig(), tariffExpired: true, bridge: new FakeBridge() });

    expect(screen.getByLabelText("Non-live cash capture unavailable")).toBeInTheDocument();
    expect(screen.getByText("Cash capture unavailable")).toBeInTheDocument();
  });

  it("rejects amount tendered below amount due", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });
    await screen.findByText("Local cash custody capture");

    await userEvent.clear(screen.getByLabelText("Amount tendered"));
    await userEvent.type(screen.getByLabelText("Amount tendered"), "100");
    await userEvent.click(screen.getByLabelText(/I attest/));
    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByText("Amount tendered must be greater than or equal to amount due.")).toBeInTheDocument();
  });

  it("requires cashier attestation", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });
    await screen.findByText("Local cash custody capture");

    await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));

    expect(await screen.findByText("Cashier attestation is required before CASH_RECEIVED.")).toBeInTheDocument();
  });

  it("calculates and displays change due", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });
    await screen.findByText("Local cash custody capture");

    await userEvent.clear(screen.getByLabelText("Amount tendered"));
    await userEvent.type(screen.getByLabelText("Amount tendered"), "150");

    expect(screen.getByLabelText("Change due")).toHaveValue("25.00");
  });

  it("renders all supported Philippine denomination inputs in descending order", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });
    await screen.findByText("Local cash custody capture");

    const labels = ["PHP-1000", "PHP-500", "PHP-100", "PHP-50", "PHP-20", "PHP-10", "PHP-5", "PHP-1"];
    for (const label of labels) {
      expect(screen.getByLabelText(label)).toHaveValue(0);
    }
  });

  it("submits only non-zero denomination counts, including new and existing denominations", async () => {
    const bridge = new FakeBridge();
    renderPanel({ config: enabledConfig(), bridge });
    await screen.findByText("Local cash custody capture");

    await userEvent.clear(screen.getByLabelText("PHP-100"));
    await userEvent.type(screen.getByLabelText("PHP-100"), "1");
    await userEvent.clear(screen.getByLabelText("PHP-20"));
    await userEvent.type(screen.getByLabelText("PHP-20"), "2");
    await userEvent.clear(screen.getByLabelText("PHP-10"));
    await userEvent.type(screen.getByLabelText("PHP-10"), "3");
    await userEvent.clear(screen.getByLabelText("PHP-5"));
    await userEvent.type(screen.getByLabelText("PHP-5"), "4");
    await userEvent.clear(screen.getByLabelText("PHP-1"));
    await userEvent.type(screen.getByLabelText("PHP-1"), "5");

    await recordCashReceived();

    const payload = bridge.recordCashReceived.mock.calls[0][1];
    expect(payload.denominations).toEqual([
      { denominationCode: "PHP-100", denominationValue: 100, quantity: 1 },
      { denominationCode: "PHP-20", denominationValue: 20, quantity: 2 },
      { denominationCode: "PHP-10", denominationValue: 10, quantity: 3 },
      { denominationCode: "PHP-5", denominationValue: 5, quantity: 4 },
      { denominationCode: "PHP-1", denominationValue: 1, quantity: 5 },
    ]);
    expect(payload.denominations.map((denomination) => denomination.denominationCode)).not.toContain("PHP-1000");
    expect(payload.denominations.map((denomination) => denomination.denominationCode)).not.toContain("PHP-500");
    expect(payload.denominations.map((denomination) => denomination.denominationCode)).not.toContain("PHP-50");
  });

  it("displays local tender ID and historical local-custody checkpoint after CASH_RECEIVED", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });

    await recordCashReceived();

    expect(await screen.findByText("Cash received locally")).toBeInTheDocument();
    expect(screen.getByText("Local tender ID: tender-001")).toBeInTheDocument();
    expect(screen.getByText(/State at local cash capture:/)).toBeInTheDocument();
    expect(screen.getByText(/At this checkpoint, canonical payment had not yet been submitted/)).toBeInTheDocument();
    expect(screen.getByText(/fiscal issuance had not yet started/)).toBeInTheDocument();
    expect(screen.queryByText(/^Local cash only\./)).not.toBeInTheDocument();
  });

  it("does not send a deterministic fixture tender ID in normal cashier flow", async () => {
    const bridge = new FakeBridge();
    renderPanel({ config: enabledConfig(), bridge });

    await recordCashReceived();

    expect(bridge.startTender.mock.calls[0][1]).not.toHaveProperty("localCashTenderId");
  });

  it("uses a deterministic tender ID only when a development fixture supplies one", async () => {
    const bridge = new FakeBridge();
    renderPanel({
      config: enabledConfig(),
      bridge,
      developmentFixtureLocalCashTenderId: "eeeeeeee-eeee-4eee-8eee-eeeeeeee2001",
    });

    await recordCashReceived();

    expect(bridge.startTender.mock.calls[0][1]).toHaveProperty(
      "localCashTenderId",
      "eeeeeeee-eeee-4eee-8eee-eeeeeeee2001",
    );
  });

  it("shows deterministic conflict when duplicate unresolved tender is rejected", async () => {
    const bridge = new FakeBridge({ duplicateOnStart: true });
    renderPanel({ config: enabledConfig(), bridge });

    await recordCashReceived();

    expect(await screen.findByText("Duplicate local cash tender rejected.")).toBeInTheDocument();
    expect(screen.getByText("Existing local tender ID: tender-existing")).toBeInTheDocument();
  });

  it("does not show payment-confirmed or fiscal-complete wording", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });

    await recordCashReceived();

    const text = document.body.textContent ?? "";
    expect(text).not.toMatch(/payment confirmed/i);
    expect(text).not.toMatch(/fiscal complete/i);
    expect(text).not.toMatch(/fiscalized/i);
  });

  it("works with cash drawer disabled", async () => {
    const bridge = new FakeBridge();
    renderPanel({ config: enabledConfig(), bridge });

    await recordCashReceived();

    expect(bridge.health).toHaveBeenCalled();
    expect(await screen.findByText("Cash received locally")).toBeInTheDocument();
  });

  it("hides Central PMS section when submission is disabled", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });

    await recordCashReceived();

    expect(screen.queryByLabelText("Central PMS canonical payment")).not.toBeInTheDocument();
  });

  it("shows unavailable state for invalid Central PMS configuration and does not submit", async () => {
    const bridge = new FakeBridge();
    renderPanel({ config: enabledConfig({ centralPmsCashSubmissionEnabled: true }), bridge });

    await recordCashReceived();

    expect(await screen.findByText("CENTRAL_PMS_BASE_URL is not configured for cash submission.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Submit / Check Central PMS" })).not.toBeInTheDocument();
    expect(bridge.submitOrReadbackCentralPmsCashSubmission).not.toHaveBeenCalled();
  });

  it("shows pending Central PMS command without canonical confirmation", async () => {
    renderPanel({ config: centralEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("Pending") }) });

    await recordCashReceived();

    expect(await screen.findByText("Canonical payment not yet confirmed")).toBeInTheDocument();
    expect(screen.getByText("Fiscal issuance not started. Exit authorization unavailable.")).toBeInTheDocument();
  });

  it("displays canonical confirmation IDs after submission", async () => {
    renderPanel({
      config: centralEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Pending"), submitStatus: centralStatus("Confirmed") }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "Submit / Check Central PMS" }));

    expect(await screen.findByText("Canonical payment confirmed")).toBeInTheDocument();
    expect(screen.getByText("payment-attempt-001")).toBeInTheDocument();
    expect(screen.getByText("payment-confirmation-001")).toBeInTheDocument();
    expect(screen.getByText("Fiscal issuance not started. Exit authorization unavailable.")).toBeInTheDocument();
  });

  it("shows idempotent replay as confirmed without duplicate-payment wording", async () => {
    renderPanel({
      config: centralEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed", { resultClassification: "IDEMPOTENT_REPLAY" }) }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Canonical payment confirmed")).toBeInTheDocument();
    expect(screen.getByText(/Idempotent replay confirmed/)).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/duplicate payment/i);
  });

  it("shows blocking support-review state for Central PMS conflict", async () => {
    renderPanel({ config: centralEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("Conflict") }) });

    await recordCashReceived();

    expect(await screen.findByText("Conflict - support review required")).toBeInTheDocument();
    expect(screen.getByText(/Supervisor or support review is required/)).toBeInTheDocument();
  });

  it("shows rejected safe error details while preserving local CASH_RECEIVED wording", async () => {
    renderPanel({ config: centralEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("Rejected") }) });

    await recordCashReceived();

    expect(await screen.findByText("Rejected - reconciliation required")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: INVALID_CASH_AMOUNTS")).toBeInTheDocument();
    expect(screen.getByText("Cash received locally")).toBeInTheDocument();
  });

  it("never displays confirmed wording for retry-pending Central PMS status", async () => {
    renderPanel({ config: centralEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("RetryPending") }) });

    await recordCashReceived();

    expect(await screen.findByText("Canonical payment not yet confirmed")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/Canonical payment confirmed/);
  });

  it("renders restart-loaded confirmed status without creating another command", async () => {
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-restored" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
    });
    renderPanel({ config: centralEnabledConfig(), bridge });

    expect(await screen.findByText("Canonical payment confirmed")).toBeInTheDocument();
    expect(bridge.startTender).not.toHaveBeenCalled();
    expect(bridge.getCentralPmsCashSubmissionStatus).toHaveBeenCalled();
  });

  it("hides fiscal action when fiscal issuance is disabled", async () => {
    renderPanel({ config: centralEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed") }) });

    await recordCashReceived();

    expect(await screen.findByText("Central PMS fiscal issuance is disabled.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Issue / Check Fiscal Document" })).not.toBeInTheDocument();
  });

  it("shows fiscal unavailable state for invalid configuration and does not submit", async () => {
    const bridge = new FakeBridge({ centralStatus: centralStatus("Confirmed") });
    renderPanel({
      config: enabledConfig({ centralPmsCashSubmissionEnabled: true, centralPmsFiscalIssuanceEnabled: true }),
      bridge,
    });

    await recordCashReceived();

    expect(await screen.findByText("CENTRAL_PMS_BASE_URL is not configured for cash submission.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Central PMS fiscal issuance")).not.toBeInTheDocument();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
  });

  it("does not show fiscal action before canonical payment confirmation", async () => {
    renderPanel({ config: fiscalEnabledConfig(), bridge: new FakeBridge({ centralStatus: centralStatus("Pending") }) });

    await recordCashReceived();

    expect(await screen.findByText("Canonical payment not yet confirmed")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Issue / Check Fiscal Document" })).not.toBeInTheDocument();
  });

  it("renders pending fiscal status separately after canonical payment confirmation", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Pending") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Fiscal issuance pending")).toBeInTheDocument();
    expect(screen.getByText("Receipt not rendered or printed. Exit authorization unavailable.")).toBeInTheDocument();
  });

  it("shows recorded fiscal status with identifiers", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Recorded") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Fiscal document recorded")).toBeInTheDocument();
    expect(screen.getByText("fiscal-reference-001")).toBeInTheDocument();
    expect(screen.getByText("pos-fiscal-document-001")).toBeInTheDocument();
    expect(screen.getByText("SI-000001")).toBeInTheDocument();
  });

  it("shows fiscal replay without duplicate-document wording", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded", { resultClassification: "IDEMPOTENT_REPLAY" }),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText(/Idempotent replay restored/)).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/duplicate invoice|duplicate charge/i);
  });

  it("shows fiscal conflict support-review guidance", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Conflict") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Fiscal conflict - support review required")).toBeInTheDocument();
    expect(screen.getByText(/Supervisor or support review is required/)).toBeInTheDocument();
  });

  it("shows fiscal rejection while preserving canonical payment", async () => {
    const bridge = new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Rejected") });
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge,
    });

    await recordCashReceived();

    expect(await screen.findByText("Canonical payment confirmed")).toBeInTheDocument();
    expect(await screen.findByText("Fiscal rejected - reconciliation required")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: CORRELATION_ID_REQUIRED")).toBeInTheDocument();
    expect(screen.queryByText("Fiscal document recorded")).not.toBeInTheDocument();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
  });

  it("never shows fiscal recorded for retry state", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("RetryPending") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Retry pending")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/Fiscal document recorded/);
  });

  it("explicit fiscal action invokes the fiscal bridge command", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Pending"),
      fiscalSubmitStatus: fiscalStatus("Recorded"),
    });
    renderPanel({ config: fiscalEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "Issue / Check Fiscal Document" }));

    expect(bridge.submitOrReadbackCentralPmsCashFiscal).toHaveBeenCalled();
    expect(await screen.findByText("Fiscal document recorded")).toBeInTheDocument();
  });

  it("status load does not automatically submit fiscal issuance", async () => {
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-restored" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
    });
    renderPanel({ config: fiscalEnabledConfig(), bridge });

    expect(await screen.findByText("Fiscal document recorded")).toBeInTheDocument();
    expect(bridge.getCentralPmsCashFiscalStatus).toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
  });

  it("hides receipt action when receipt retrieval is disabled", async () => {
    renderPanel({
      config: fiscalEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Recorded") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Central PMS receipt retrieval is disabled.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retrieve / Check Receipt" })).not.toBeInTheDocument();
  });

  it("shows receipt unavailable state for invalid configuration and does not retrieve", async () => {
    const bridge = new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Recorded") });
    renderPanel({
      config: enabledConfig({
        centralPmsCashSubmissionEnabled: true,
        centralPmsFiscalIssuanceEnabled: true,
        centralPmsReceiptRetrievalEnabled: true,
      }),
      bridge,
    });

    await recordCashReceived();

    expect(await screen.findByText("CENTRAL_PMS_BASE_URL is not configured for cash submission.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Central PMS receipt availability")).not.toBeInTheDocument();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
  });

  it("does not show receipt action before fiscal recording", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({ centralStatus: centralStatus("Confirmed"), fiscalStatus: fiscalStatus("Pending") }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Fiscal issuance pending")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retrieve / Check Receipt" })).not.toBeInTheDocument();
  });

  it("renders recorded fiscal state with pending receipt separately", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Pending"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt not yet retrieved")).toBeInTheDocument();
    expect(within(screen.getByLabelText("Central PMS receipt availability")).getByText("Receipt not rendered or printed. Exit authorization unavailable.")).toBeInTheDocument();
  });

  it("shows available receipt metadata and payload hash without raw payload", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    const receiptPanel = within(screen.getByLabelText("Central PMS receipt availability"));
    expect(receiptPanel.getByText("SI-000001")).toBeInTheDocument();
    expect(receiptPanel.getByText("RECORDED")).toBeInTheDocument();
    expect(receiptPanel.getByText("dsi-presentation-v1")).toBeInTheDocument();
    expect(receiptPanel.getByText("template-v1")).toBeInTheDocument();
    expect(receiptPanel.getByText("application/vnd.exitpass.digital-sales-invoice+json")).toBeInTheDocument();
    expect(receiptPanel.getByText("sha256:receipt-payload")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/authoritativePresentation|receiptLine|taxes|totals|merchantHeader/i);
  });

  it("not-ready receipt state does not claim availability", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("NotReady"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation not ready")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/Receipt presentation available/);
  });

  it("retry or unavailable receipt state does not claim successful retrieval", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("RetryPending"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Retry pending")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/Receipt presentation available/);
  });

  it("shows receipt inconsistency support-review guidance", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Inconsistent"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt inconsistency - support review required")).toBeInTheDocument();
    expect(screen.getByText(/Supervisor or support review is required/)).toBeInTheDocument();
  });

  it("shows receipt rejection while preserving fiscal recording", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Rejected"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Fiscal document recorded")).toBeInTheDocument();
    expect(await screen.findByText("Receipt rejected - reconciliation required")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: RECEIPT_PRESENTATION_REJECTED")).toBeInTheDocument();
  });

  it("shows voided receipt posture separately from active printable receipt", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Voided"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation available - fiscal document voided")).toBeInTheDocument();
    expect(screen.getByText("voided")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/active printable receipt/i);
  });

  it("restart-loaded available receipt renders without another retrieval", async () => {
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-restored" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
    });

    renderPanel({ config: receiptEnabledConfig(), bridge });

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    expect(bridge.getCentralPmsCashReceiptStatus).toHaveBeenCalled();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
  });

  it("explicit receipt action invokes the receipt bridge command", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Pending"),
      receiptRetrieveStatus: receiptStatus("Available"),
    });
    renderPanel({ config: receiptEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "Retrieve / Check Receipt" }));

    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).toHaveBeenCalled();
    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
  });

  it("status loading does not automatically retrieve receipt", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
    });
    renderPanel({ config: receiptEnabledConfig(), bridge });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    expect(bridge.getCentralPmsCashReceiptStatus).toHaveBeenCalled();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
  });

  it("hides receipt preview action when preview feature is disabled", async () => {
    renderPanel({
      config: receiptEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "View Receipt Preview" })).not.toBeInTheDocument();
  });

  it("hides receipt preview action before receipt is available", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("NotReady"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation not ready")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "View Receipt Preview" })).not.toBeInTheDocument();
  });

  it("shows unsupported receipt presentation as a terminal safe state without retry action", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Unsupported"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Sales Invoice format is not supported")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED")).toBeInTheDocument();
    expect(screen.getByText(/No local fiscal receipt was created/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retrieve / Check Receipt" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "View Receipt Preview" })).not.toBeInTheDocument();
  });

  it("shows malformed receipt presentation as a terminal safe state without fallback content", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Malformed"),
      }),
    });

    await recordCashReceived();

    expect(await screen.findByText("Sales Invoice response could not be read")).toBeInTheDocument();
    expect(screen.getByText("Safe error code: POS_SERVER_RECEIPT_PRESENTATION_MALFORMED")).toBeInTheDocument();
    expect(screen.getByText(/No local fiscal receipt was created/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Read-only receipt body")).not.toBeInTheDocument();
  });

  it("blocks placeholder-marked preview payloads without rendering a receipt body", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ complete: false }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Receipt presentation is incomplete")).toBeInTheDocument();
    expect(screen.getByText(/No local placeholders were rendered/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Read-only receipt body")).not.toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/\[[A-Z -]+\]/);
  });

  it("opens a read-only receipt preview from complete authoritative data", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreview: receiptPreview({ complete: true }),
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(bridge.getCentralPmsCashReceiptPreview).toHaveBeenCalled();
    expect(await screen.findByRole("dialog")).toHaveTextContent("Receipt preview");
    expect(screen.getByText("Read-only authoritative presentation")).toBeInTheDocument();
    expect(document.body).toHaveTextContent("Not printed");
    expect(document.body).toHaveTextContent("Exit authorization unavailable");
    expect(document.body).toHaveTextContent("Paper width: 57 mm");
    expect(screen.getByText("Configuration completeness: Complete")).toBeInTheDocument();
    expect(screen.queryByText(/Development preview: some Sales Invoice fields are placeholders/)).not.toBeInTheDocument();
    expect(screen.getAllByText("SALES INVOICE").length).toBeGreaterThan(0);
    expect(screen.getByText("GOVERNED REGISTERED BUSINESS NAME")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED REGISTERED BUSINESS ADDRESS")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED TIN")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED POS SERIAL NUMBER")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED MACHINE IDENTIFICATION NUMBER")).toBeInTheDocument();
    expect(screen.getByText("Parking fee - cash")).toBeInTheDocument();
    expect(screen.getByText("Qty")).toBeInTheDocument();
    expect(screen.getByText("Unit price")).toBeInTheDocument();
    expect(screen.getAllByText("PHP 125.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("PHP 125.00").length).toBeGreaterThan(0);
    expect(screen.queryByText("SALES INVOICE DETAILS")).not.toBeInTheDocument();
    expect(screen.getAllByText("SALES INVOICE").length).toBeGreaterThan(0);
    expect(screen.getByText("Sales Invoice No.")).toBeInTheDocument();
    expect(screen.queryByText("Fiscal Identity")).not.toBeInTheDocument();
    expect(screen.queryByText("Fiscal document no.")).not.toBeInTheDocument();
    expect(screen.getByText("PARKING DETAILS")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED PLATE NUMBER")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED ENTRY TIME")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED EXIT TIME")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED DURATION")).toBeInTheDocument();
    expect(screen.getByText("VAT BREAKDOWN")).toBeInTheDocument();
    expect(screen.getAllByText("PHP 0.00").length).toBeGreaterThan(0);
    expect(screen.getByText("Output VAT")).toBeInTheDocument();
    expect(screen.getAllByText("Subtotal").length).toBeGreaterThan(0);
    expect(screen.getAllByText("PHP 125.00").length).toBeGreaterThan(0);
    expect(screen.queryByText("grand_total")).not.toBeInTheDocument();
    expect(screen.getByText("Payment method")).toBeInTheDocument();
    expect(screen.getByText("CASH")).toBeInTheDocument();
    expect(screen.getByText("Total Paid")).toBeInTheDocument();
    expect(screen.getByText("Change")).toBeInTheDocument();
    expect(screen.getByText("THIS SERVES AS YOUR SALES INVOICE")).toBeInTheDocument();
    expect(screen.getByText("THANK YOU FOR CHOOSING OUR SERVICE")).toBeInTheDocument();
    expect(screen.getByText("BIR ACCREDITATION AND PTU INFORMATION")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED BIR ACCREDITATION NO.")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED BIR ACCREDITATION DATE ISSUED")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED BIR ACCREDITATION VALID UNTIL")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED PTU NO.")).toBeInTheDocument();
    expect(screen.getByText("GOVERNED PTU DATE ISSUED")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/\[[A-Z -]+\]/);
    expect(document.body.textContent ?? "").not.toMatch(/authoritativePresentation|\{"presentation"/i);
    expect(document.body.textContent ?? "").not.toMatch(/merchant Name|site Name|display Amount|total Type|tender Type|change Display|message|VAT REG TIN|Demo Corporation|Sample TIN/i);
    expect(screen.queryByRole("button", { name: /^(Print Sales Invoice|Reprint Sales Invoice|Export|PDF|Email|SMS|Share)$/i })).not.toBeInTheDocument();
  });

  it("prints only an available authoritative receipt and does not retrieve another presentation", async () => {
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({
          id: "tender-001",
          state: "CashReceived",
          correlationId: "corr-001",
        }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptRetrieveStatus: receiptStatus("Available"),
      receiptPrintStatus: receiptPrintStatus([]),
    });
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPrintingEnabled: true,
        receiptPrinterName: "APT Controlled Printer",
      }),
      bridge,
    });

    await userEvent.click(await screen.findByRole("button", { name: "Print Sales Invoice" }));

    expect(bridge.submitCentralPmsCashReceiptPrint).toHaveBeenCalledTimes(1);
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
    expect(screen.getByText("Submitted to printer.")).toBeInTheDocument();
    expect(screen.getByText("Submitted to printer")).toBeInTheDocument();
    expect(screen.getByLabelText("Prepared print output")).toHaveTextContent("SALES INVOICE");
    expect(screen.getByLabelText("Prepared print output")).not.toHaveTextContent("REPRINTED:");
    expect(screen.getByLabelText("Prepared print output")).not.toHaveTextContent("SALES INVOICE DETAILS");
  });

  it("renders durable REPRINTED marker above Sales Invoice for reprint output", async () => {
    const reprintJob = receiptPrintJob({
      classification: "Reprint",
      classificationLabel: "Reprint",
      copySequence: 2,
      submittedToSpoolerAt: "2026-07-24T07:42:00Z",
      windowsSpoolerJobId: "controlled-spooler-2",
    });
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({
          id: "tender-001",
          state: "CashReceived",
          correlationId: "corr-001",
        }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPrintStatus: receiptPrintStatus([
        receiptPrintJob({
          classification: "Original",
          classificationLabel: "Original",
          copySequence: 1,
          submittedToSpoolerAt: "2026-07-24T07:30:00Z",
        }),
      ]),
      receiptPrintSubmit: receiptPrintSubmit(reprintJob),
    });
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPrintingEnabled: true,
        receiptPrinterName: "APT Controlled Printer",
      }),
      bridge,
    });

    await userEvent.click(await screen.findByRole("button", { name: "Reprint Sales Invoice" }));

    const output = screen.getByLabelText("Prepared print output");
    expect(output).toHaveTextContent("REPRINTED: 2026-07-24 15:42");
    expect(output).toHaveTextContent("SALES INVOICE");
    expect(output).not.toHaveTextContent("SALES INVOICE DETAILS");
    expect((output.textContent ?? "").indexOf("REPRINTED: 2026-07-24 15:42")).toBeLessThan(
      (output.textContent ?? "").indexOf("SALES INVOICE"),
    );
    expect(output).toHaveTextContent("Fiscal doc: SI-000001");
  });

  it("shows read-only Sales Invoice print history summary, filters, and detail", async () => {
    const original = receiptPrintJob({
      printJobId: "print-job-original",
      classification: "Original",
      classificationLabel: "Original",
      copySequence: 1,
      requestedAt: "2026-07-24T07:30:00Z",
      submittedToSpoolerAt: "2026-07-24T07:30:02Z",
    });
    const reprint = receiptPrintJob({
      printJobId: "print-job-reprint",
      classification: "Reprint",
      classificationLabel: "Reprint",
      copySequence: 2,
      requestedAt: "2026-07-24T07:42:00Z",
      submittedToSpoolerAt: "2026-07-24T07:42:03Z",
      windowsSpoolerJobId: "controlled-spooler-2",
    });
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-001" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPrintStatus: receiptPrintStatus([original, reprint]),
      receiptPrintHistory: printHistory([original, reprint]),
      receiptPrintHistoryDetail: printHistoryDetail(reprint),
    });
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPrintingEnabled: true,
        receiptPrinterName: "APT Controlled Printer",
      }),
      bridge,
    });

    const historyPanel = await screen.findByLabelText("Sales Invoice Print History");
    await waitFor(() => expect(within(historyPanel).getByText("Reprint count")).toBeInTheDocument());
    await waitFor(() => expect(within(historyPanel).getByText("1")).toBeInTheDocument());
    expect(within(historyPanel).getByText("Latest copy sequence")).toBeInTheDocument();
    expect(within(historyPanel).getByText("2")).toBeInTheDocument();
    expect(within(historyPanel).getByText("APT Controlled Printer")).toBeInTheDocument();
    expect(within(historyPanel).getAllByText("Submitted to printer").length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole("button", { name: "Open Print History" }));
    expect(screen.getByLabelText("Print history filters")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Original.*Copy sequence 1.*Submitted to printer/s })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Reprint.*Copy sequence 2.*Submitted to printer/s })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Reprint" }));
    expect(screen.queryByRole("button", { name: /Original.*Copy sequence 1/s })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Reprint.*Copy sequence 2/s }));

    expect(await screen.findByLabelText("Print attempt detail")).toHaveTextContent("Physical paper output is not separately confirmed");
    expect(screen.getByText("Payload hash evidence")).toBeInTheDocument();
    expect(screen.queryByText(/\{\"presentation\"/)).not.toBeInTheDocument();
    expect(bridge.getSalesInvoicePrintHistoryForTender).toHaveBeenCalled();
    expect(bridge.getSalesInvoicePrintHistoryDetail).toHaveBeenCalledWith(expect.any(String), "print-job-reprint");
  });

  it("opening and filtering print history is read-only and creates no print or authority side effects", async () => {
    const job = receiptPrintJob({ status: "UnknownAfterRestart", statusLabel: "Print result requires confirmation", failureClassification: "SPOOLER_OUTCOME_UNKNOWN_AFTER_RESTART" });
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-001" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPrintStatus: receiptPrintStatus([job]),
      receiptPrintHistory: printHistory([job]),
      receiptPrintHistoryDetail: printHistoryDetail(job),
    });
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPrintingEnabled: true,
        receiptPrinterName: "APT Controlled Printer",
      }),
      bridge,
    });

    await userEvent.click(await screen.findByRole("button", { name: "Open Print History" }));
    await userEvent.click(screen.getByRole("button", { name: "Requires confirmation" }));
    await userEvent.click(screen.getByRole("button", { name: /Original.*Print result requires confirmation/s }));

    expect(screen.getByText(/will not resolve or resubmit/)).toBeInTheDocument();
    expect(bridge.submitCentralPmsCashReceiptPrint).not.toHaveBeenCalled();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
    expect(bridge.getCentralPmsCashReceiptPreview).not.toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashSubmission).not.toHaveBeenCalled();
    expect(bridge.submitOrReadbackCentralPmsCashFiscal).not.toHaveBeenCalled();
  });

  it("opening receipt preview does not create a print job", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreview: receiptPreview({ complete: true }),
      receiptPrintStatus: receiptPrintStatus([]),
    });
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPrintingEnabled: true,
        receiptPrinterName: "APT Controlled Printer",
      }),
      bridge,
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(screen.getByLabelText("Read-only receipt body")).toBeInTheDocument();
    expect(bridge.submitCentralPmsCashReceiptPrint).not.toHaveBeenCalled();
  });

  it("uses authoritative values instead of corresponding placeholders when supplied", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ complete: true }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    await screen.findByText("GOVERNED REGISTERED BUSINESS NAME");
    const body = screen.getByLabelText("Read-only receipt body");
    expect(document.body).toHaveTextContent("Configuration completeness: Complete");
    expect(screen.queryByText(/Development preview: some Sales Invoice fields are placeholders/)).not.toBeInTheDocument();
    expect(body.textContent ?? "").not.toMatch(/merchant Name|site Name|Fiscal Identity|fiscal Document Number|Fiscal document no\.|display Amount|total Type|tender Type|change Display|message/i);
    expect(within(body).getAllByText("GOVERNED REGISTERED BUSINESS NAME")).toHaveLength(1);
    expect(within(body).getAllByText("GOVERNED PARKING LOCATION")).toHaveLength(1);
    expect(within(body).getAllByText("SI-000001")).toHaveLength(1);
    expect(within(body).getByText("Sales Invoice No.")).toBeInTheDocument();
    expect(screen.queryByText("[REGISTERED BUSINESS NAME]")).not.toBeInTheDocument();
    expect(screen.queryByText("[TIN]")).not.toBeInTheDocument();
    expect(screen.queryByText("[PLATE NUMBER]")).not.toBeInTheDocument();
    expect(screen.queryByText("[SALES INVOICE FOOTER]")).not.toBeInTheDocument();
    expect(within(body).getByText("GOVERNED TIN")).toBeInTheDocument();
    expect(within(body).getByText("BIR Accr. No.")).toBeInTheDocument();
    expect(within(body).getByText("GOVERNED BIR ACCREDITATION NO.")).toBeInTheDocument();
    expect(within(body).getByText("GOVERNED BIR ACCREDITATION DATE ISSUED")).toBeInTheDocument();
    expect(within(body).getByText("GOVERNED BIR ACCREDITATION VALID UNTIL")).toBeInTheDocument();
    expect(within(body).getByText("GOVERNED PTU NO.")).toBeInTheDocument();
    expect(within(body).getByText("GOVERNED PTU DATE ISSUED")).toBeInTheDocument();
    expect(within(body).getAllByText("Date Issued")).toHaveLength(2);
    expect(screen.queryByText("[BIR ACCREDITATION NO.]")).not.toBeInTheDocument();
    expect(screen.queryByText("[BIR ACCREDITATION DATE ISSUED]")).not.toBeInTheDocument();
    expect(screen.queryByText("[BIR ACCREDITATION VALID UNTIL]")).not.toBeInTheDocument();
    expect(screen.queryByText("[PTU DATE ISSUED]")).not.toBeInTheDocument();
    expect(within(body).getByText("THIS SERVES AS YOUR SALES INVOICE")).toBeInTheDocument();
    expect(within(body).getByText("THANK YOU FOR CHOOSING OUR SERVICE")).toBeInTheDocument();
    expect(body.textContent ?? "").not.toMatch(/Demo Corporation|Sample TIN|ABC 1234|ACC-001|PTU-001/i);
  });

  it("collapses technical receipt metadata by default while keeping primary metadata visible", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ complete: true }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Sales Invoice No. SI-000001")).toBeInTheDocument();
    expect(screen.getByText("Paper width: 57 mm")).toBeInTheDocument();
    expect(screen.getByText("Configuration completeness: Complete")).toBeInTheDocument();
    expect(screen.getByText("Not printed")).toBeInTheDocument();
    expect(screen.getByText("Exit authorization unavailable")).toBeInTheDocument();
    const details = screen.getByText("Receipt technical details").closest("details");
    expect(details).not.toHaveAttribute("open");
  });

  it("closes receipt preview and returns to the cashier workflow", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ complete: true }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));
    await userEvent.click(await screen.findByRole("button", { name: "Close preview" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload local tender" })).toBeInTheDocument();
  });

  it("shows unsupported version guidance without rendering receipt body", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreviewFailure: { code: "receipt_preview_unsupported_version", message: "Unsupported receipt presentation version." },
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Unsupported receipt presentation version")).toBeInTheDocument();
    expect(screen.getByText(/support review or application upgrade/i)).toBeInTheDocument();
    expect(screen.queryByLabelText("Read-only receipt body")).not.toBeInTheDocument();
  });

  it("shows payload-integrity failure as a blocking support state", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreviewFailure: { code: "receipt_preview_integrity_failed", message: "Receipt payload integrity check failed." },
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Receipt payload integrity check failed")).toBeInTheDocument();
    expect(screen.queryByLabelText("Read-only receipt body")).not.toBeInTheDocument();
  });

  it("shows malformed payload decode failure safely", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreviewFailure: { code: "receipt_preview_decode_failed", message: "Receipt presentation could not be safely decoded." },
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Receipt presentation could not be safely decoded")).toBeInTheDocument();
    expect(screen.queryByLabelText("Read-only receipt body")).not.toBeInTheDocument();
  });

  it("renders voided preview with prominent void posture", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig(),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Voided"),
        receiptPreview: receiptPreview({ voided: true, complete: true }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("VOIDED FISCAL DOCUMENT")).toBeInTheDocument();
    expect(screen.getByText("Void reason: SUPERVISOR_VOID")).toBeInTheDocument();
    expect(screen.getByText(/Not valid as an active receipt/)).toBeInTheDocument();
  });

  it.each([
    [undefined, "57 mm"],
    [57 as const, "57 mm"],
    [58 as const, "58 mm"],
    [80 as const, "80 mm"],
  ])("displays active paper width %s", async (width, expectedText) => {
    const preview = receiptPreview({ paperWidthMm: width ?? 57, complete: true });
    renderPanel({
      config: receiptPreviewEnabledConfig(width ? { receiptPaperWidthMm: width } : {}),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: preview,
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    await screen.findByText("Receipt preview");
    expect(document.body).toHaveTextContent(`Paper width: ${expectedText}`);
    expect(screen.getByText("Parking fee - cash")).toBeInTheDocument();
    expect(screen.getByText("Receipt technical details")).toBeInTheDocument();
  });

  it("displays invalid-width fallback warning and keeps receipt facts unchanged", async () => {
    renderPanel({
      config: receiptPreviewEnabledConfig({
        receiptPaperWidthMm: 57,
        receiptPaperWidthWarning: "Unsupported APT_RECEIPT_PAPER_WIDTH_MM value '99'. Falling back to 57 mm.",
      }),
      bridge: new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({
          paperWidthMm: 57,
          paperWidthWarning: "Unsupported APT_RECEIPT_PAPER_WIDTH_MM value '99'. Falling back to 57 mm.",
          complete: true,
        }),
      }),
    });

    await recordCashReceived();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    await screen.findByText("Receipt preview");
    expect(document.body).toHaveTextContent("Paper width: 57 mm");
    expect(screen.getByText(/Falling back to 57 mm/)).toBeInTheDocument();
    expect(screen.getAllByText("SI-000001").length).toBeGreaterThan(0);
    expect(screen.getByText("Parking fee - cash")).toBeInTheDocument();
  });

  it("preserves identical receipt facts across 57 58 and 80 mm profiles", async () => {
    const factsByWidth: Array<string> = [];
    for (const width of [57, 58, 80] as const) {
      const bridge = new FakeBridge({
        centralStatus: centralStatus("Confirmed"),
        fiscalStatus: fiscalStatus("Recorded"),
        receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ paperWidthMm: width, complete: true }),
      });
      const { unmount } = renderPanel({ config: receiptPreviewEnabledConfig({ receiptPaperWidthMm: width }), bridge });

      await recordCashReceived();
      await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));
      const receiptBody = await screen.findByLabelText("Read-only receipt body");
      factsByWidth.push(receiptBody.textContent ?? "");
      expect(screen.getByText(`Paper width: ${width} mm`)).toBeInTheDocument();
      unmount();
    }

    expect(factsByWidth[1]).toBe(factsByWidth[0]);
    expect(factsByWidth[2]).toBe(factsByWidth[0]);
  });

  it("restart-loaded available receipt can be previewed without retrieval", async () => {
    const bridge = new FakeBridge({
      initialReadback: {
        tender: tender({ id: "tender-001", state: "CashReceived", correlationId: "corr-restored" }),
        events: [],
      },
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
        receiptPreview: receiptPreview({ complete: true }),
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: "View Receipt Preview" }));

    expect(await screen.findByText("Receipt preview")).toBeInTheDocument();
    expect(bridge.retrieveOrCheckCentralPmsCashReceipt).not.toHaveBeenCalled();
  });

  it("status loading does not automatically open or decode preview", async () => {
    const bridge = new FakeBridge({
      centralStatus: centralStatus("Confirmed"),
      fiscalStatus: fiscalStatus("Recorded"),
      receiptStatus: receiptStatus("Available"),
      receiptPreview: receiptPreview({ complete: true }),
    });
    renderPanel({ config: receiptPreviewEnabledConfig(), bridge });

    await recordCashReceived();

    expect(await screen.findByText("Receipt presentation available")).toBeInTheDocument();
    expect(bridge.getCentralPmsCashReceiptPreview).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Receipt preview")).not.toBeInTheDocument();
  });
});

function renderPanel({
  config,
  tariffExpired = false,
  bridge,
  developmentFixtureLocalCashTenderId,
}: {
  config: AptConfig;
  tariffExpired?: boolean;
  bridge: LocalJournalBridge;
  developmentFixtureLocalCashTenderId?: string;
}) {
  return render(
    <CashCapturePanel
      config={config}
      context={buildTerminalContext(config)}
      session={activeSession()}
      tariffExpired={tariffExpired}
      bridge={bridge}
      developmentFixtureLocalCashTenderId={developmentFixtureLocalCashTenderId}
    />,
  );
}

async function recordCashReceived() {
  await waitFor(() => expect(screen.queryByText("Checking local journal readiness...")).not.toBeInTheDocument());
  await userEvent.clear(screen.getByLabelText("Amount tendered"));
  await userEvent.type(screen.getByLabelText("Amount tendered"), "150");
  await userEvent.click(screen.getByLabelText(/I attest/));
  await userEvent.click(screen.getByRole("button", { name: "Record Cash Received" }));
}

function enabledConfig(overrides: Partial<AptConfig> = {}): AptConfig {
  return { ...mode1Config(), nonLiveCashCaptureEnabled: true, ...overrides };
}

function centralEnabledConfig(): AptConfig {
  return enabledConfig({
    centralPmsCashSubmissionEnabled: true,
    centralPmsBaseUrl: "http://127.0.0.1:18080",
  });
}

function fiscalEnabledConfig(): AptConfig {
  return enabledConfig({
    centralPmsCashSubmissionEnabled: true,
    centralPmsFiscalIssuanceEnabled: true,
    centralPmsBaseUrl: "http://127.0.0.1:18080",
  });
}

function receiptEnabledConfig(): AptConfig {
  return enabledConfig({
    centralPmsCashSubmissionEnabled: true,
    centralPmsFiscalIssuanceEnabled: true,
    centralPmsReceiptRetrievalEnabled: true,
    centralPmsBaseUrl: "http://127.0.0.1:18080",
  });
}

function receiptPreviewEnabledConfig(overrides: Partial<AptConfig> = {}): AptConfig {
  return {
    ...receiptEnabledConfig(),
    receiptPreviewEnabled: true,
    receiptPaperWidthMm: 57,
    receiptPaperWidthWarning: null,
    ...overrides,
  };
}

function activeSession(): ResolveVendorParkingResponse {
  const now = Date.now();
  return {
    parkingSessionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
    tariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    siteGroupId: "22222222-2222-4222-8222-222222222222",
    siteId: "11111111-1111-4111-8111-111111111111",
    lookupOutcome: "resolved",
    plateNumber: "NCR-4421",
    ticketReference: "APT-ACTIVE-1001",
    entryTime: new Date(now - 120000).toISOString(),
    currentFeeCalculationTime: new Date(now).toISOString(),
    netPayableMinorUnits: 12500,
    currency: "PHP",
    tariffExpiresAt: new Date(now + 900000).toISOString(),
    feeValidUntil: new Date(now + 900000).toISOString(),
    parkingStatus: "Active",
    paymentStatus: "Not Started",
    statutoryDiscountApplied: false,
    effectiveTariffSnapshotId: "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
    vendorSystemId: "VENDOR-PMS-DEV",
    correlationId: "corr-session",
  };
}

class FakeBridge implements LocalJournalBridge {
  private readonly duplicateOnStart: boolean;
  private readonly centralStatus: CentralPmsCashSubmissionStatus;
  private readonly submitStatus: CentralPmsCashSubmissionStatus;
  private readonly fiscalStatus: CentralPmsCashFiscalStatus;
  private readonly fiscalSubmitStatus: CentralPmsCashFiscalStatus;
  private readonly receiptStatus: CentralPmsCashReceiptStatus;
  private readonly receiptRetrieveStatus: CentralPmsCashReceiptStatus;
  private readonly receiptPreview: CentralPmsCashReceiptPreview;
  private readonly receiptPrintStatus: CentralPmsCashReceiptPrintStatus;
  private readonly receiptPrintSubmit: CentralPmsCashReceiptPrintSubmit;
  private readonly receiptPrintHistory: SalesInvoicePrintHistory;
  private readonly receiptPrintHistoryDetail: SalesInvoicePrintHistoryDetail;
  private readonly receiptPreviewFailure?: { code: string; message: string };
  private readonly initialReadback: LocalTenderReadback;

  public health = vi.fn(async (correlationId: string): Promise<BridgeResult<LocalJournalHealth>> => ({
    ok: true,
    command: "localJournal.health",
    correlationId,
    payload: {
      healthy: true,
      enabled: true,
      databasePath: "C:\\Temp\\cash-ui-test.db",
      cashDrawerEnabled: false,
      authorityWarning: "Local only",
    },
  }));

  public createOrGetDevelopmentSession = vi.fn(async (correlationId: string): Promise<BridgeResult<CashCustodySessionSnapshot>> => ({
    ok: true,
    command: "localJournal.createOrGetDevelopmentSession",
    correlationId,
    payload: {
      id: "cash-session-001",
      cashierId: "cashier",
      authenticatedCashierSessionReference: "auth",
      cashierShiftId: "shift",
      terminalId: "terminal",
      siteId: "site",
      siteGroupId: "site-group",
      posServerId: "pos",
      openingCashAmount: 0,
      openedAt: new Date().toISOString(),
      status: "Open",
    },
  }));

  public startTender = vi.fn(async (correlationId: string, payload: StartTenderPayload): Promise<BridgeResult<CashTenderSnapshot>> => {
    if (this.duplicateOnStart) {
      return {
        ok: false,
        command: "localJournal.startTender",
        correlationId,
        error: {
          code: "DuplicateUnresolvedTender",
          message: "Parking session already has an unresolved local cash tender.",
          detail: {
            existingCashTenderId: "tender-existing",
            existingCashTenderState: "CashReceived",
          },
        },
      };
    }

    return {
      ok: true,
      command: "localJournal.startTender",
      correlationId,
      payload: tender({ ...payload, id: payload.localCashTenderId ?? "tender-001", state: "TenderStarted", correlationId }),
    };
  });

  public recordCashReceived = vi.fn(async (
    correlationId: string,
    _payload: RecordCashReceivedPayload,
  ): Promise<BridgeResult<CashTenderSnapshot>> => ({
    ok: true,
    command: "localJournal.recordCashReceived",
    correlationId,
    payload: tender({ id: "tender-001", state: "CashReceived", correlationId }),
  }));

  public readTenderByParkingSession = vi.fn(async (correlationId: string): Promise<BridgeResult<LocalTenderReadback>> => ({
    ok: true,
    command: "localJournal.readTenderByParkingSession",
    correlationId,
    payload: this.initialReadback,
  }));

  public getCentralPmsCashSubmissionStatus = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashSubmissionStatus>> => ({
    ok: true,
    command: "centralPmsCashSubmission.getStatus",
    correlationId,
    payload: this.centralStatus,
  }));

  public submitOrReadbackCentralPmsCashSubmission = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashSubmissionStatus>> => ({
    ok: true,
    command: "centralPmsCashSubmission.submitOrReadback",
    correlationId,
    payload: this.submitStatus,
  }));

  public getCentralPmsCashFiscalStatus = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashFiscalStatus>> => ({
    ok: true,
    command: "centralPmsCashFiscal.getStatus",
    correlationId,
    payload: this.fiscalStatus,
  }));

  public submitOrReadbackCentralPmsCashFiscal = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashFiscalStatus>> => ({
    ok: true,
    command: "centralPmsCashFiscal.submitOrReadback",
    correlationId,
    payload: this.fiscalSubmitStatus,
  }));

  public getCentralPmsCashReceiptStatus = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashReceiptStatus>> => ({
    ok: true,
    command: "centralPmsCashReceipt.getStatus",
    correlationId,
    payload: this.receiptStatus,
  }));

  public retrieveOrCheckCentralPmsCashReceipt = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashReceiptStatus>> => ({
    ok: true,
    command: "centralPmsCashReceipt.retrieveOrCheck",
    correlationId,
    payload: this.receiptRetrieveStatus,
  }));

  public getCentralPmsCashReceiptPreview = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashReceiptPreview>> => {
    if (this.receiptPreviewFailure) {
      return {
        ok: false,
        command: "centralPmsCashReceipt.getPreview",
        correlationId,
        error: {
          code: this.receiptPreviewFailure.code,
          message: this.receiptPreviewFailure.message,
          detail: {
            command: this.receiptStatus.command ?? undefined,
            paperProfile: this.receiptPreview.paperProfile,
            paperWidthWarning: this.receiptPreview.paperWidthWarning,
          },
        },
      };
    }

    return {
      ok: true,
      command: "centralPmsCashReceipt.getPreview",
      correlationId,
      payload: this.receiptPreview,
    };
  });

  public getCentralPmsCashReceiptPrintStatus = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashReceiptPrintStatus>> => ({
    ok: true,
    command: "centralPmsCashReceiptPrint.getStatus",
    correlationId,
    payload: this.receiptPrintStatus,
  }));

  public submitCentralPmsCashReceiptPrint = vi.fn(async (correlationId: string): Promise<BridgeResult<CentralPmsCashReceiptPrintSubmit>> => ({
    ok: true,
    command: "centralPmsCashReceiptPrint.submit",
    correlationId,
    payload: this.receiptPrintSubmit,
  }));

  public getSalesInvoicePrintHistoryForTender = vi.fn(async (correlationId: string): Promise<BridgeResult<SalesInvoicePrintHistory>> => ({
    ok: true,
    command: "salesInvoicePrintHistory.getForTender",
    correlationId,
    payload: this.receiptPrintHistory,
  }));

  public getSalesInvoicePrintHistoryForFiscalDocument = vi.fn(async (correlationId: string): Promise<BridgeResult<SalesInvoicePrintHistory>> => ({
    ok: true,
    command: "salesInvoicePrintHistory.getForFiscalDocument",
    correlationId,
    payload: this.receiptPrintHistory,
  }));

  public getRecentSalesInvoicePrintHistory = vi.fn(async (correlationId: string): Promise<BridgeResult<SalesInvoicePrintHistory>> => ({
    ok: true,
    command: "salesInvoicePrintHistory.getRecent",
    correlationId,
    payload: this.receiptPrintHistory,
  }));

  public getSalesInvoicePrintHistoryDetail = vi.fn(async (correlationId: string): Promise<BridgeResult<SalesInvoicePrintHistoryDetail>> => ({
    ok: true,
    command: "salesInvoicePrintHistory.getDetail",
    correlationId,
    payload: this.receiptPrintHistoryDetail,
  }));

  public constructor(options: {
    duplicateOnStart?: boolean;
    centralStatus?: CentralPmsCashSubmissionStatus;
    submitStatus?: CentralPmsCashSubmissionStatus;
    fiscalStatus?: CentralPmsCashFiscalStatus;
    fiscalSubmitStatus?: CentralPmsCashFiscalStatus;
    receiptStatus?: CentralPmsCashReceiptStatus;
    receiptRetrieveStatus?: CentralPmsCashReceiptStatus;
    receiptPreview?: CentralPmsCashReceiptPreview;
    receiptPrintStatus?: CentralPmsCashReceiptPrintStatus;
    receiptPrintSubmit?: CentralPmsCashReceiptPrintSubmit;
    receiptPrintHistory?: SalesInvoicePrintHistory;
    receiptPrintHistoryDetail?: SalesInvoicePrintHistoryDetail;
    receiptPreviewFailure?: { code: string; message: string };
    initialReadback?: LocalTenderReadback;
  } = {}) {
    this.duplicateOnStart = options.duplicateOnStart ?? false;
    this.centralStatus = options.centralStatus ?? centralStatus("Pending");
    this.submitStatus = options.submitStatus ?? this.centralStatus;
    this.fiscalStatus = options.fiscalStatus ?? fiscalStatus("Pending");
    this.fiscalSubmitStatus = options.fiscalSubmitStatus ?? this.fiscalStatus;
    this.receiptStatus = options.receiptStatus ?? receiptStatus("Pending");
    this.receiptRetrieveStatus = options.receiptRetrieveStatus ?? this.receiptStatus;
    this.receiptPreview = options.receiptPreview ?? receiptPreview();
    this.receiptPrintStatus = options.receiptPrintStatus ?? receiptPrintStatus();
    this.receiptPrintSubmit = options.receiptPrintSubmit ?? receiptPrintSubmit();
    this.receiptPrintHistory = options.receiptPrintHistory ?? printHistory(this.receiptPrintStatus.jobs);
    this.receiptPrintHistoryDetail = options.receiptPrintHistoryDetail ?? printHistoryDetail(this.receiptPrintHistory.jobs[0] ?? receiptPrintJob());
    this.receiptPreviewFailure = options.receiptPreviewFailure;
    this.initialReadback = options.initialReadback ?? { tender: null, events: [] };
  }
}

function tender({
  id,
  state,
  correlationId,
  cashCustodySessionId = "cash-session-001",
  parkingSessionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001",
  tariffSnapshotId = "dddddddd-dddd-4ddd-8ddd-dddddddd1001",
  currency = "PHP",
  amountDue = 125,
  amountTendered = 150,
}: Partial<StartTenderPayload> & { id: string; state: string; correlationId: string }): CashTenderSnapshot {
  return {
    id,
    cashCustodySessionId,
    parkingSessionId,
    tariffSnapshotId,
    currency,
    amountDue,
    amountTendered,
    changeDue: amountTendered - amountDue,
    correlationId,
    localIdempotencyIdentity: "idem-ui",
    currentLocalState: state,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function centralStatus(
  status: CentralPmsCashSubmissionStatus["command"] extends infer Command
    ? Command extends { status: infer Status }
      ? Status
      : never
    : never,
  overrides: Partial<NonNullable<CentralPmsCashSubmissionStatus["command"]>> = {},
): CentralPmsCashSubmissionStatus {
  const confirmed = status === "Confirmed";
  const rejected = status === "Rejected";
  const conflict = status === "Conflict";
  return {
    enabled: true,
    configurationValid: true,
    configurationMessage: "Central PMS cash submission is available.",
    command: {
      localCommandId: "command-001",
      terminalCashTenderId: "tender-001",
      cashCustodySessionId: "cash-session-001",
      status,
      statusLabel: status === "RetryPending" ? "Retry pending" : status,
      attemptCount: status === "Pending" ? 0 : 1,
      originalCorrelationId: "corr-central-pms",
      resultClassification: confirmed ? "CREATED" : conflict ? "CONFLICT" : rejected ? "REJECTED" : "UNCERTAIN",
      canonicalPaymentAttemptId: confirmed ? "payment-attempt-001" : null,
      canonicalPaymentConfirmationId: confirmed ? "payment-confirmation-001" : null,
      confirmedAt: confirmed ? new Date().toISOString() : null,
      nextRetryAt: status === "RetryPending" ? new Date().toISOString() : null,
      lastSafeHttpStatus: conflict ? 409 : rejected ? 400 : null,
      lastSafeErrorCode: conflict ? "DUPLICATE_CASH_TENDER" : rejected ? "INVALID_CASH_AMOUNTS" : null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      ...overrides,
    },
  };
}

function fiscalStatus(
  status: CentralPmsCashFiscalStatus["command"] extends infer Command
    ? Command extends { status: infer Status }
      ? Status
      : never
    : never,
  overrides: Partial<NonNullable<CentralPmsCashFiscalStatus["command"]>> = {},
): CentralPmsCashFiscalStatus {
  const recorded = status === "Recorded";
  const rejected = status === "Rejected";
  const conflict = status === "Conflict";
  return {
    enabled: true,
    configurationValid: true,
    configurationMessage: "Central PMS fiscal issuance is available.",
    command: {
      localFiscalCommandId: "fiscal-command-001",
      terminalCashTenderId: "tender-001",
      relatedCashPaymentOutboxCommandId: "command-001",
      canonicalPaymentAttemptId: "payment-attempt-001",
      canonicalPaymentConfirmationId: "payment-confirmation-001",
      status,
      statusLabel: status === "RetryPending" ? "Retry pending" : status,
      attemptCount: status === "Pending" ? 0 : 1,
      fiscalCorrelationId: "corr-fiscal",
      resultClassification: recorded ? "NEWLY_CREATED" : conflict ? "CONFLICT" : rejected ? "REJECTED" : "UNCERTAIN",
      fiscalIssuanceReferenceId: recorded ? "fiscal-reference-001" : null,
      fiscalIssuanceState: recorded ? "FISCAL_ISSUANCE_RECORDED" : status === "Pending" ? "PENDING_FISCAL_ISSUANCE" : null,
      posFiscalDocumentId: recorded ? "pos-fiscal-document-001" : null,
      fiscalDocumentNumber: recorded ? "SI-000001" : null,
      fiscalNumberAssignedAt: recorded ? new Date().toISOString() : null,
      semanticHashSourceVersion: recorded ? "pos-server-semantic-hash:sha256:v1" : null,
      recordedAt: recorded ? new Date().toISOString() : null,
      nextRetryAt: status === "RetryPending" ? new Date().toISOString() : null,
      lastSafeHttpStatus: conflict ? 409 : rejected ? 400 : null,
      lastSafeErrorCode: conflict ? "TERMINAL_CASH_FISCAL_SEMANTIC_CONFLICT" : rejected ? "CORRELATION_ID_REQUIRED" : null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      ...overrides,
    },
  };
}

function receiptStatus(
  status: CentralPmsCashReceiptStatus["command"] extends infer Command
    ? Command extends { status: infer Status }
      ? Status
      : never
    : never,
  overrides: Partial<NonNullable<CentralPmsCashReceiptStatus["command"]>> = {},
): CentralPmsCashReceiptStatus {
  const available = status === "Available";
  const voided = status === "Voided";
  const rejected = status === "Rejected";
  const inconsistent = status === "Inconsistent";
  const unsupported = status === "Unsupported";
  const malformed = status === "Malformed";
  return {
    enabled: true,
    configurationValid: true,
    configurationMessage: "Central PMS receipt retrieval is available.",
    command: {
      localReceiptRetrievalId: "receipt-command-001",
      terminalCashTenderId: "tender-001",
      relatedCashPaymentOutboxCommandId: "command-001",
      relatedFiscalCommandId: "fiscal-command-001",
      canonicalPaymentAttemptId: "payment-attempt-001",
      canonicalPaymentConfirmationId: "payment-confirmation-001",
      canonicalPaymentStatus: "CONFIRMED",
      fiscalIssuanceReferenceId: "fiscal-reference-001",
      posFiscalDocumentId: "pos-fiscal-document-001",
      status,
      statusLabel: status === "NotReady" ? "Not ready" : status === "RetryPending" ? "Retry pending" : status,
      attemptCount: status === "Pending" ? 0 : 1,
      retrievalCorrelationId: "corr-receipt",
      resultClassification: available || voided
        ? "AVAILABLE"
        : inconsistent
          ? "INCONSISTENT"
          : rejected
            ? "REJECTED"
            : unsupported
              ? "UNSUPPORTED"
              : malformed
                ? "MALFORMED"
                : "PENDING",
      receiptAvailabilityState: available || voided ? "AVAILABLE" : null,
      fiscalDocumentNumber: available || voided ? "SI-000001" : null,
      fiscalDocumentStatus: available ? "RECORDED" : voided ? "VOIDED" : null,
      presentationVersion: available || voided ? "dsi-presentation-v1" : null,
      templateVersion: available || voided ? "template-v1" : null,
      semanticRequestHash: available || voided ? "sha256:fiscal-semantic" : null,
      semanticRequestHashVersion: available || voided ? "pos-server-semantic-hash:sha256:v1" : null,
      semanticRequestHashStatus: available || voided ? "MATCHED" : null,
      contentType: available || voided ? "application/vnd.exitpass.digital-sales-invoice+json" : null,
      authoritativePayloadHash: available || voided ? "sha256:receipt-payload" : null,
      voidStatus: voided ? "voided" : null,
      voidReasonCode: voided ? "SUPERVISOR_VOID" : null,
      voidedAt: voided ? new Date().toISOString() : null,
      retrievedAt: available || voided ? new Date().toISOString() : null,
      nextRetryAt: status === "RetryPending" || status === "NotReady" ? new Date().toISOString() : null,
      lastSafeHttpStatus: inconsistent || unsupported || malformed ? 409 : rejected ? 400 : null,
      lastSafeErrorCode: inconsistent
        ? "RECEIPT_REFERENCE_MISMATCH"
        : rejected
          ? "RECEIPT_PRESENTATION_REJECTED"
          : unsupported
            ? "POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED"
            : malformed
              ? "POS_SERVER_RECEIPT_PRESENTATION_MALFORMED"
              : null,
      lastRetryable: status === "RetryPending" || status === "NotReady",
      lastCentralPmsCorrelationId: available || voided ? "corr-central-pms-receipt" : null,
      lastUpdatedFromCentralPms: available || voided ? new Date().toISOString() : null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      ...overrides,
    },
  };
}

function receiptPrintStatus(
  jobs: CentralPmsCashReceiptPrintStatus["jobs"] = [],
  overrides: Partial<CentralPmsCashReceiptPrintStatus> = {},
): CentralPmsCashReceiptPrintStatus {
  return {
    enabled: true,
    configurationValid: true,
    configurationMessage: "Sales Invoice printing is configured.",
    command: receiptStatus("Available").command,
    jobs,
    ...overrides,
  };
}

function printHistory(jobs: CentralPmsCashReceiptPrintStatus["jobs"] = []): SalesInvoicePrintHistory {
  const latest = jobs.at(-1) ?? null;
  const original = jobs.find((job) => job.classification === "Original") ?? null;
  const hasAttention = jobs.some((job) => job.status === "UnknownAfterRestart" || job.retryable);
  return {
    scope: "terminalCashTenderId",
    summary: {
      hasHistory: jobs.length > 0,
      originalStatus: original?.statusLabel ?? "No print attempts recorded",
      reprintCount: jobs.filter((job) => job.classification === "Reprint").length,
      latestCopySequence: latest?.copySequence ?? null,
      latestStatus: latest?.statusLabel ?? "No print attempts recorded",
      latestPrinterName: latest?.configuredPrinterName ?? null,
      latestPaperWidthMm: latest?.paperWidthMm ?? null,
      latestAttemptAt: latest?.requestedAt ?? null,
      requiresConfirmation: jobs.some((job) => job.status === "UnknownAfterRestart"),
      attentionRequired: hasAttention,
    },
    jobs,
    indicators: jobs.length === 0
      ? [{ code: "NO_PRINT_HISTORY", label: "No print attempts recorded", severity: "info", message: "No local Sales Invoice print attempt is recorded for this scope." }]
      : [
          { code: "ORIGINAL_SUBMITTED", label: "Original submitted", severity: "info", message: "At least one original print attempt has local spooler evidence." },
          ...(jobs.some((job) => job.status === "UnknownAfterRestart")
            ? [{ code: "PRINT_RESULT_REQUIRES_CONFIRMATION", label: "Print result requires confirmation", severity: "attention", message: "A print submission was interrupted and the terminal will not silently resubmit it." }]
            : []),
        ],
  };
}

function printHistoryDetail(job: CentralPmsCashReceiptPrintStatus["jobs"][number]): SalesInvoicePrintHistoryDetail {
  return {
    job,
    statusExplanation: job.status === "SubmittedToSpooler"
      ? "The Sales Invoice print attempt was accepted by the Windows printer queue. Physical paper output is not separately confirmed by this local evidence."
      : "Submission had started before restart and the final printer result requires confirmation. This view will not resubmit the job.",
    shortAuthoritativePayloadHash: "sha256:receipt-payload",
    shortSemanticRequestHash: "sha256:fiscal-semantic",
    indicators: printHistory([job]).indicators,
  };
}

function receiptPrintSubmit(
  job: CentralPmsCashReceiptPrintStatus["jobs"][number] = receiptPrintJob(),
): CentralPmsCashReceiptPrintSubmit {
  const reprintMarker = job.classification === "Reprint" ? "REPRINTED: 2026-07-24 15:42" : null;
  return {
    job,
    safeMessage: "Submitted to printer.",
    printDocument: {
      terminalCashTenderId: job.terminalCashTenderId,
      fiscalDocumentId: job.posFiscalDocumentId,
      fiscalDocumentNumber: job.fiscalDocumentNumber,
      authoritativePayloadHash: job.authoritativePayloadHash,
      semanticRequestHash: job.semanticRequestHash,
      classification: job.classification,
      copySequence: job.copySequence,
      reprintedAt: job.classification === "Reprint" ? job.submittedToSpoolerAt : null,
      reprintMarker,
      paperProfile: receiptPreview({ complete: true }).paperProfile,
      lines: reprintMarker
        ? [reprintMarker, "SALES INVOICE", "Fiscal doc: SI-000001"]
        : ["SALES INVOICE", "Fiscal doc: SI-000001"],
    },
  };
}

function receiptPrintJob(
  overrides: Partial<CentralPmsCashReceiptPrintStatus["jobs"][number]> = {},
): CentralPmsCashReceiptPrintStatus["jobs"][number] {
  const now = new Date().toISOString();
  return {
    printJobId: "print-job-001",
    terminalCashTenderId: "tender-001",
    localReceiptRetrievalId: "receipt-command-001",
    fiscalIssuanceReferenceId: "fiscal-reference-001",
    posFiscalDocumentId: "pos-fiscal-document-001",
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
    windowsSpoolerJobId: "controlled-spooler-1",
    lastUpdatedAt: now,
    correlationId: "corr-print",
    ...overrides,
  };
}

function receiptPreview({
  voided = false,
  paperWidthMm = 57,
  paperWidthWarning = null,
  complete = false,
}: {
  voided?: boolean;
  paperWidthMm?: 57 | 58 | 80;
  paperWidthWarning?: string | null;
  complete?: boolean;
} = {}): CentralPmsCashReceiptPreview {
  const profile = {
    id: `receipt-paper-${paperWidthMm}` as "receipt-paper-57" | "receipt-paper-58" | "receipt-paper-80",
    paperWidthMm,
    printableWidthMm: paperWidthMm === 80 ? 70 : paperWidthMm === 58 ? 49 : 48,
    innerMarginMm: paperWidthMm === 80 ? 5 : 4,
    fontScale: paperWidthMm === 80 ? 1 : paperWidthMm === 58 ? 0.94 : 0.92,
    monetaryColumnBehavior: paperWidthMm === 80 ? "wide-right-aligned" : "compact-right-aligned",
    metadataDensity: paperWidthMm === 80 ? "standard" : "compact",
  };
  const command = receiptStatus(voided ? "Voided" : "Available").command!;
  return {
    enabled: true,
    command,
    paperProfile: profile,
    paperWidthWarning,
    preview: {
      terminalCashTenderId: command.terminalCashTenderId,
      localReceiptRetrievalId: command.localReceiptRetrievalId,
      fiscalIssuanceReferenceId: command.fiscalIssuanceReferenceId,
      posFiscalDocumentId: command.posFiscalDocumentId,
      fiscalDocumentNumber: "SI-000001",
      fiscalDocumentStatus: voided ? "VOIDED" : "RECORDED",
      receiptAvailabilityState: "AVAILABLE",
      presentationVersion: "digital-sales-invoice-presentation-json-v1",
      templateVersion: "digital-sales-invoice-json-v1",
      contentType: "application/json",
      authoritativePayloadHash: "sha256:receipt-payload",
      semanticRequestHash: command.semanticRequestHash,
      semanticRequestHashVersion: command.semanticRequestHashVersion,
      semanticRequestHashStatus: command.semanticRequestHashStatus,
      retrievedAt: new Date().toISOString(),
      retrievalCorrelationId: "corr-receipt",
      centralPmsCorrelationId: "corr-central-pms-receipt",
      voided,
      voidStatus: voided ? "voided" : null,
      voidReasonCode: voided ? "SUPERVISOR_VOID" : null,
      voidedAt: voided ? new Date().toISOString() : null,
      paperProfile: profile,
      hasPlaceholders: !complete,
      configurationCompleteness: complete ? "Complete" : "Incomplete",
      sections: [
        {
          title: "Sales Invoice Title",
          fields: [field("Title", "SALES INVOICE")],
          rows: [],
        },
        {
          title: "Registered business and statutory header",
          fields: complete
            ? [
                field("Registered business name", "GOVERNED REGISTERED BUSINESS NAME"),
                field("Registered business address", "GOVERNED REGISTERED BUSINESS ADDRESS"),
                field("TIN", "GOVERNED TIN", false, "tin"),
                field("S/N", "GOVERNED POS SERIAL NUMBER"),
                field("MIN", "GOVERNED MACHINE IDENTIFICATION NUMBER"),
              ]
            : [
                field("Registered business name", "[REGISTERED BUSINESS NAME]", true),
                field("Registered business address", "[REGISTERED BUSINESS ADDRESS]", true),
                field("TIN", "[TIN]", true, "tin"),
                field("S/N", "[POS SERIAL NUMBER]", true),
                field("MIN", "[MACHINE IDENTIFICATION NUMBER]", true),
              ],
          rows: [],
        },
        {
          title: "SITE AND TERMINAL INFORMATION",
          fields: complete
            ? [field("PARKING LOCATION", "GOVERNED PARKING LOCATION"), field("TERMINAL ID", "GOVERNED TERMINAL ID")]
            : [field("PARKING LOCATION", "[PARKING LOCATION]", true), field("TERMINAL ID", "[TERMINAL ID]", true)],
          rows: [],
        },
        {
          title: "SALES INVOICE",
          fields: [
            field("Sales Invoice No.", "SI-000001"),
            complete ? field("Issued Date", "GOVERNED ISSUED DATE", false, "issuedDate") : field("Issued Date", "[ISSUED DATE]", true, "issuedDate"),
          ],
          rows: [],
        },
        {
          title: "PARKING DETAILS",
          fields: complete
            ? [
                field("Plate Number", "GOVERNED PLATE NUMBER"),
                field("Entry Time", "GOVERNED ENTRY TIME"),
                field("Exit Time", "GOVERNED EXIT TIME"),
                field("Duration", "GOVERNED DURATION"),
              ]
            : [
                field("Plate Number", "[PLATE NUMBER]", true),
                field("Entry Time", "[ENTRY TIME]", true),
                field("Exit Time", "[EXIT TIME]", true),
                field("Duration", "[DURATION]", true),
              ],
          rows: [],
        },
        {
          title: "ITEMS",
          fields: [],
          rows: [
            {
              fields: [
                field("Description", "Parking fee - cash"),
                field("Qty", "1"),
                complete ? field("Unit price", "PHP 125.00") : field("Unit price", "[UNIT PRICE]", true),
                field("Amount", "PHP 125.00"),
              ],
            },
          ],
        },
        {
          title: "SUBTOTAL",
          fields: [complete ? field("Subtotal", "PHP 125.00") : field("Subtotal", "[SUBTOTAL]", true)],
          rows: [],
        },
        {
          title: "DISCOUNTS",
          fields: [],
          rows: [
            {
              fields: complete
                ? [field("Discount Reason", "None"), field("Discount Amount", "PHP 0.00")]
                : [field("Discount Reason", "[DISCOUNT REASON]", true), field("Discount Amount", "[DISCOUNT AMOUNT]", true)],
            },
          ],
        },
        {
          title: "VAT BREAKDOWN",
          fields: complete
            ? [
                field("VATable Sales", "PHP 125.00"),
                field("Output VAT", "PHP 0.00"),
                field("VAT Exempt", "PHP 0.00"),
                field("Zero Rated", "PHP 0.00"),
              ]
            : [
                field("VATable Sales", "[VATABLE SALES]", true),
                field("Output VAT", "PHP 0.00"),
                field("VAT Exempt", "[VAT EXEMPT SALES]", true),
                field("Zero Rated", "[ZERO-RATED SALES]", true),
              ],
          rows: [],
        },
        {
          title: "PAYMENT DETAILS",
          fields: [
            field("Payment method", "CASH"),
            complete ? field("Provider", "Not applicable") : field("Provider", "[PAYMENT PROVIDER]", true),
            field("Amount", "PHP 150.00"),
          ],
          rows: [],
        },
        {
          title: "TOTAL PAID AND CHANGE",
          fields: [field("Total Paid", "PHP 150.00"), field("Change", "PHP 25.00")],
          rows: [],
        },
        {
          title: "Sales Invoice legal statement",
          fields: [complete ? field("Statement", "THIS SERVES AS YOUR SALES INVOICE") : field("Statement", "[SALES INVOICE LEGAL STATEMENT]", true)],
          rows: [],
        },
        {
          title: "Customer-service footer",
          fields: [complete ? field("Footer", "THANK YOU FOR CHOOSING OUR SERVICE") : field("Footer", "[SALES INVOICE FOOTER]", true)],
          rows: [],
        },
        {
          title: "BIR ACCREDITATION AND PTU INFORMATION",
          fields: complete
            ? [
                field("BIR Accr. No.", "GOVERNED BIR ACCREDITATION NO.", false, "birAccreditationNumber"),
                field("Date Issued", "GOVERNED BIR ACCREDITATION DATE ISSUED", false, "birAccreditationIssuedDateDisplay"),
                field("Valid Until", "GOVERNED BIR ACCREDITATION VALID UNTIL", false, "birAccreditationValidUntilDisplay"),
                field("PTU No.", "GOVERNED PTU NO.", false, "ptuNumber"),
                field("Date Issued", "GOVERNED PTU DATE ISSUED", false, "ptuIssuedDateDisplay"),
              ]
            : [
                field("BIR Accr. No.", "[BIR ACCREDITATION NO.]", true, "birAccreditationNumber"),
                field("Date Issued", "[BIR ACCREDITATION DATE ISSUED]", true, "birAccreditationIssuedDateDisplay"),
                field("Valid Until", "[BIR ACCREDITATION VALID UNTIL]", true, "birAccreditationValidUntilDisplay"),
                field("PTU No.", "[PTU NO.]", true, "ptuNumber"),
                field("Date Issued", "[PTU DATE ISSUED]", true, "ptuIssuedDateDisplay"),
              ],
          rows: [],
        },
      ],
    },
  };
}

function field(label: string, value: string, isPlaceholder = false, key = label) {
  return { key, label, value, isPlaceholder };
}
