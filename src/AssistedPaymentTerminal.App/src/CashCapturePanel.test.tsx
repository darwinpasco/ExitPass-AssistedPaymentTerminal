import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { CashCapturePanel } from "./CashCapturePanel";
import type { ResolveVendorParkingResponse } from "./api/centralPmsTypes";
import type { AptConfig } from "./config";
import type {
  BridgeResult,
  CashCustodySessionSnapshot,
  CashTenderSnapshot,
  LocalJournalBridge,
  LocalJournalHealth,
  LocalTenderReadback,
  RecordCashReceivedPayload,
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

  it("displays local tender ID and local-only authority warning after CASH_RECEIVED", async () => {
    renderPanel({ config: enabledConfig(), bridge: new FakeBridge() });

    await recordCashReceived();

    expect(await screen.findByText("Cash received locally")).toBeInTheDocument();
    expect(screen.getByText("Local tender ID: tender-001")).toBeInTheDocument();
    expect(screen.getByText(/Canonical payment not submitted/)).toBeInTheDocument();
    expect(screen.getByText(/Fiscal issuance not started/)).toBeInTheDocument();
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
});

function renderPanel({
  config,
  tariffExpired = false,
  bridge,
}: {
  config: AptConfig;
  tariffExpired?: boolean;
  bridge: LocalJournalBridge;
}) {
  render(
    <CashCapturePanel
      config={config}
      context={buildTerminalContext(config)}
      session={activeSession()}
      tariffExpired={tariffExpired}
      bridge={bridge}
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

function enabledConfig(): AptConfig {
  return { ...mode1Config(), nonLiveCashCaptureEnabled: true };
}

function activeSession(): ResolveVendorParkingResponse {
  const now = Date.now();
  return {
    parkingSessionId: "parking-session-ui-001",
    tariffSnapshotId: "tariff-ui-001",
    siteGroupId: "site-group-001",
    siteId: "site-001",
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
    effectiveTariffSnapshotId: "tariff-ui-001",
    vendorSystemId: "VENDOR-PMS-DEV",
    correlationId: "corr-session",
  };
}

class FakeBridge implements LocalJournalBridge {
  private readonly duplicateOnStart: boolean;

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
      payload: tender({ ...payload, id: "tender-001", state: "TenderStarted", correlationId }),
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
    payload: {
      tender: null,
      events: [],
    },
  }));

  public constructor(options: { duplicateOnStart?: boolean } = {}) {
    this.duplicateOnStart = options.duplicateOnStart ?? false;
  }
}

function tender({
  id,
  state,
  correlationId,
  cashCustodySessionId = "cash-session-001",
  parkingSessionId = "parking-session-ui-001",
  tariffSnapshotId = "tariff-ui-001",
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
