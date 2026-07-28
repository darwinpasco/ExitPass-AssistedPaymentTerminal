import { useMemo, useState, type ReactNode } from "react";
import type { AptConfig } from "./config";
import { MockCentralPmsClient } from "./api/mockCentralPms";
import type { CentralPmsClient, PayableBasisReferenceType } from "./api/centralPmsTypes";
import type {
  BridgeResult,
  CashCustodySessionSnapshot,
  CentralPmsCashSubmissionStatus,
  CashTenderSnapshot,
  CreateDevelopmentSessionPayload,
  LocalJournalBridge,
  LocalJournalHealth,
  LocalTenderReadback,
  PayableBasisStatePayload,
  PayableBasisStateSnapshot,
  RecordCashReceivedPayload,
  StartTenderPayload,
} from "./localJournalBridge";

export type PayableBasisVisualSmokeScenarioId =
  | "ticket-ready"
  | "plate-ready"
  | "fiscal-readiness-blocked"
  | "session-already-paid"
  | "vendor-pms-unavailable"
  | "revalidation-passed-unchanged"
  | "revalidation-amount-changed"
  | "restart-recovery-before-cash";

export type PayableBasisVisualSmokeScenario = {
  id: PayableBasisVisualSmokeScenarioId;
  label: string;
  referenceType: PayableBasisReferenceType;
  referenceValue: string;
  expectedPosture: string;
};

export type PayableBasisVisualSmokeRuntimeScenario = PayableBasisVisualSmokeScenario & {
  config: AptConfig;
  client: CentralPmsClient;
};

export type PayableBasisVisualSmokeRenderArgs = {
  scenario: PayableBasisVisualSmokeRuntimeScenario;
  bridge: LocalJournalBridge;
  restorePayableBasisOnMount: boolean;
  renderKey: string;
};

export const payableBasisVisualSmokeScenarios: PayableBasisVisualSmokeScenario[] = [
  {
    id: "ticket-ready",
    label: "Ticket ready for cash",
    referenceType: "ticket",
    referenceValue: "APT-ACTIVE-1001",
    expectedPosture: "Ready basis from ticket lookup; Continue to Cash revalidates before local cash custody.",
  },
  {
    id: "plate-ready",
    label: "Plate ready for cash",
    referenceType: "plate",
    referenceValue: "PLATE-READY-1002",
    expectedPosture: "Ready basis from plate lookup; no ticket is required.",
  },
  {
    id: "fiscal-readiness-blocked",
    label: "Fiscal readiness blocked",
    referenceType: "ticket",
    referenceValue: "APT-FISCAL-BLOCKED",
    expectedPosture: "Sales Invoice or fiscal readiness blocks CASH_RECEIVED.",
  },
  {
    id: "session-already-paid",
    label: "Session already paid",
    referenceType: "ticket",
    referenceValue: "APT-ALREADY-PAID",
    expectedPosture: "Central PMS reports a terminal already-paid blocker.",
  },
  {
    id: "vendor-pms-unavailable",
    label: "Vendor PMS temporarily unavailable",
    referenceType: "ticket",
    referenceValue: "APT-UNAVAILABLE-503",
    expectedPosture: "Temporary Vendor PMS failure stays retryable and pre-cash.",
  },
  {
    id: "revalidation-passed-unchanged",
    label: "Revalidation passed unchanged",
    referenceType: "ticket",
    referenceValue: "APT-ACTIVE-1001",
    expectedPosture: "Continue to Cash runs revalidation and opens the local cash workflow only after PASSED_UNCHANGED.",
  },
  {
    id: "revalidation-amount-changed",
    label: "Revalidation amount changed",
    referenceType: "ticket",
    referenceValue: "APT-AMOUNT-CHANGED",
    expectedPosture: "Continue to Cash returns AMOUNT_CHANGED, shows old/new amounts, and remains pre-cash.",
  },
  {
    id: "restart-recovery-before-cash",
    label: "Restart recovery before cash acceptance",
    referenceType: "ticket",
    referenceValue: "APT-ACTIVE-1001",
    expectedPosture: "Resolve and persist the basis, simulate restart, and restore it before CASH_RECEIVED.",
  },
];

export function shouldUsePayableBasisVisualSmoke(
  search: string,
  isDevelopment = (import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV === true,
): boolean {
  return isDevelopment && new URLSearchParams(search).get("payableBasisVisualSmoke") === "1";
}

export function PayableBasisVisualSmokeShell({
  config,
  renderTerminalShell,
  bridge = createPayableBasisVisualSmokeBridge(),
}: {
  config: AptConfig;
  renderTerminalShell: (args: PayableBasisVisualSmokeRenderArgs) => ReactNode;
  bridge?: PayableBasisVisualSmokeBridge;
}) {
  const [selectedId, setSelectedId] = useState<PayableBasisVisualSmokeScenarioId>("ticket-ready");
  const [renderVersion, setRenderVersion] = useState(0);
  const [restartMode, setRestartMode] = useState(false);
  const selected = payableBasisVisualSmokeScenarios.find((scenario) => scenario.id === selectedId) ?? payableBasisVisualSmokeScenarios[0];
  const smokeConfig = useMemo<AptConfig>(
    () => ({
      ...config,
      nonLiveCashCaptureEnabled: true,
      centralPmsConnectionMode: "mock",
      centralPmsCashSubmissionEnabled: false,
      centralPmsFiscalIssuanceEnabled: false,
      centralPmsReceiptRetrievalEnabled: false,
      receiptPreviewEnabled: false,
      receiptPrintingEnabled: false,
    }),
    [config],
  );
  const runtimeScenario = useMemo<PayableBasisVisualSmokeRuntimeScenario>(
    () => ({
      ...selected,
      config: smokeConfig,
      client: new MockCentralPmsClient(smokeConfig),
    }),
    [selected, smokeConfig],
  );
  const restoredRuntimeScenario = restartMode
    ? { ...runtimeScenario, referenceValue: "" }
    : runtimeScenario;

  function selectScenario(scenario: PayableBasisVisualSmokeScenario) {
    bridge.reset();
    setSelectedId(scenario.id);
    setRestartMode(false);
    setRenderVersion((value) => value + 1);
  }

  return (
    <main
      className="terminal-shell receipt-visual-smoke-shell"
      data-testid="apt-payable-basis-visual-smoke-shell"
      data-surface="payable-basis-visual-smoke"
      data-app-ready="true"
    >
      <header className="brand-header">
        <div>
          <p className="eyebrow">Development fixture</p>
          <h1>Payable Basis Visual Smoke</h1>
        </div>
        <span className="status-badge warning">Development-only</span>
      </header>

      <section className="visual-smoke-selector" aria-label="Payable basis visual smoke scenarios">
        {payableBasisVisualSmokeScenarios.map((scenario) => (
          <button
            key={scenario.id}
            type="button"
            className={scenario.id === selectedId ? "secondary-action selected" : "secondary-action"}
            aria-pressed={scenario.id === selectedId}
            onClick={() => selectScenario(scenario)}
          >
            {scenario.label}
          </button>
        ))}
      </section>

      <section className="status-notice info" role="status" aria-label="Selected payable basis visual smoke scenario">
        <h2>{selected.label}</h2>
        <p>{selected.expectedPosture}</p>
        <p>Pre-cash fixture state: no local cash-custody record, no CashReceived state, no terminal-cash payment submission, no payment finality, no fiscal issuance, no receipt retrieval, and no printing state.</p>
        <p className="support-line">Controlled reference: {selected.referenceType} {selected.referenceValue}</p>
        {selected.id === "restart-recovery-before-cash" && (
          <button
            type="button"
            className="secondary-action"
            onClick={() => {
              setRestartMode(true);
              setRenderVersion((value) => value + 1);
            }}
          >
            Simulate restart
          </button>
        )}
      </section>

      {renderTerminalShell({
        scenario: restoredRuntimeScenario,
        bridge,
        restorePayableBasisOnMount: restartMode,
        renderKey: `${selectedId}:${renderVersion}:${restartMode ? "restored" : "fresh"}`,
      })}
    </main>
  );
}

export type PayableBasisVisualSmokeBridge = LocalJournalBridge & {
  reset(): void;
  seedTender(snapshot: CashTenderSnapshot | null): void;
  seedCentralPmsCashSubmissionStatus(status: CentralPmsCashSubmissionStatus | null): void;
};

export function createPayableBasisVisualSmokeBridge(storageKey = "exitpass-apt-payable-basis-visual-smoke"): PayableBasisVisualSmokeBridge {
  const nowIso = () => new Date().toISOString();
  let payableBasis = readPayableBasisState(storageKey);
  let custodySession: CashCustodySessionSnapshot | null = null;
  let tender: CashTenderSnapshot | null = null;
  let centralPmsCashSubmissionStatus: CentralPmsCashSubmissionStatus | null = null;

  function persist(snapshot: PayableBasisStateSnapshot | null) {
    payableBasis = snapshot;
    writePayableBasisState(storageKey, snapshot);
  }

  const unavailable = <T,>(command: string, correlationId: string): Promise<BridgeResult<T>> => Promise.resolve({
    ok: false,
    command,
    correlationId,
    error: {
      code: "VISUAL_SMOKE_NO_SIDE_EFFECT",
      message: "Payable-basis visual smoke does not execute payment, fiscal, receipt, print, gate, or cash-drawer commands.",
    },
  });

  return {
    reset() {
      custodySession = null;
      tender = null;
      centralPmsCashSubmissionStatus = null;
      persist(null);
    },
    seedTender(snapshot) {
      tender = snapshot;
    },
    seedCentralPmsCashSubmissionStatus(status) {
      centralPmsCashSubmissionStatus = status;
    },
    async health(correlationId): Promise<BridgeResult<LocalJournalHealth>> {
      return {
        ok: true,
        command: "localJournal.health",
        correlationId,
        payload: {
          healthy: true,
          enabled: true,
          databasePath: "controlled-payable-basis-visual-smoke.db",
          cashDrawerEnabled: false,
          authorityWarning: "Development-only payable-basis visual smoke fixture. No live Central PMS, printer, HikCentral, payment, fiscal, receipt, gate, or cash-drawer command is executed.",
        },
      };
    },
    async savePayableBasisState(correlationId, payload: PayableBasisStatePayload): Promise<BridgeResult<PayableBasisStateSnapshot>> {
      const timestamp = nowIso();
      const snapshot: PayableBasisStateSnapshot = {
        ...payload,
        id: `payable-basis-visual-smoke:${payload.terminalId}:${payload.parkingSessionId}`,
        resolvedAt: payableBasis?.resolvedAt ?? timestamp,
        lastRevalidatedAt: payload.revalidationOutcome ? timestamp : payableBasis?.lastRevalidatedAt ?? null,
        updatedAt: timestamp,
      };
      persist(snapshot);
      return { ok: true, command: "payableBasisState.save", correlationId, payload: snapshot };
    },
    async getLatestPayableBasisState(correlationId, terminalId, siteId): Promise<BridgeResult<PayableBasisStateSnapshot | null>> {
      const payload = payableBasis?.terminalId === terminalId && payableBasis.siteId === siteId ? payableBasis : null;
      return { ok: true, command: "payableBasisState.getLatest", correlationId, payload };
    },
    async createOrGetDevelopmentSession(correlationId, payload: CreateDevelopmentSessionPayload): Promise<BridgeResult<CashCustodySessionSnapshot>> {
      custodySession ??= {
        id: "payable-basis-visual-smoke-custody-session",
        cashierId: payload.cashierId,
        authenticatedCashierSessionReference: payload.authenticatedCashierSessionReference,
        cashierShiftId: payload.cashierShiftId,
        terminalId: payload.terminalId,
        siteId: payload.siteId,
        siteGroupId: payload.siteGroupId,
        posServerId: payload.posServerId,
        openingCashAmount: payload.openingCashAmount,
        openedAt: nowIso(),
        status: "Open",
      };
      return { ok: true, command: "localJournal.createOrGetDevelopmentSession", correlationId, payload: custodySession };
    },
    async startTender(correlationId, payload: StartTenderPayload): Promise<BridgeResult<CashTenderSnapshot>> {
      const timestamp = nowIso();
      tender = {
        id: payload.localCashTenderId ?? "payable-basis-visual-smoke-tender",
        cashCustodySessionId: payload.cashCustodySessionId,
        parkingSessionId: payload.parkingSessionId,
        tariffSnapshotId: payload.tariffSnapshotId,
        currency: payload.currency,
        amountDue: payload.amountDue,
        amountTendered: payload.amountTendered,
        changeDue: Math.max(0, payload.amountTendered - payload.amountDue),
        correlationId,
        localIdempotencyIdentity: payload.localIdempotencyIdentity,
        currentLocalState: "TenderStarted",
        createdAt: timestamp,
        updatedAt: timestamp,
      };
      return { ok: true, command: "localJournal.startTender", correlationId, payload: tender };
    },
    async recordCashReceived(correlationId, payload: RecordCashReceivedPayload): Promise<BridgeResult<CashTenderSnapshot>> {
      if (!tender) {
        return unavailable("localJournal.recordCashReceived", correlationId);
      }
      const evidence = payload.statutoryTenderEvidence;
      tender = {
        ...tender,
        currentLocalState: "CashReceived",
        statutoryDiscountDecisionCommandId: evidence?.statutoryDiscountDecisionCommandId ?? null,
        statutoryDiscountPayableBasisApplicationCommandId: evidence?.statutoryDiscountPayableBasisApplicationCommandId ?? null,
        statutoryDiscountValidationId: evidence?.statutoryDiscountValidationId ?? null,
        statutoryOriginalTariffSnapshotId: evidence?.originalTariffSnapshotId ?? null,
        statutoryAppliedTariffSnapshotId: evidence?.appliedTariffSnapshotId ?? null,
        statutoryOriginalAmountMinorUnits: evidence?.originalAmountMinorUnits ?? null,
        statutoryFinalAmountMinorUnits: evidence?.finalAmountMinorUnits ?? null,
        statutoryCurrency: evidence?.currency ?? null,
        statutoryAmountAcknowledged: evidence?.amountAcknowledged ?? null,
        statutoryAmountAcknowledgedAt: evidence?.amountAcknowledgedAt ?? null,
        statutoryImmediateRevalidationOutcome: evidence?.immediateRevalidationOutcome ?? null,
        statutoryImmediateRevalidatedAt: evidence?.immediateRevalidatedAt ?? null,
        statutoryCorrelationId: evidence?.centralPmsCorrelationId ?? null,
        statutoryReadinessStatus: evidence?.readinessStatus ?? null,
        statutoryReadinessAction: evidence?.readinessAction ?? null,
        updatedAt: nowIso(),
      };
      return { ok: true, command: "localJournal.recordCashReceived", correlationId, payload: tender };
    },
    async readTenderByParkingSession(correlationId, parkingSessionId): Promise<BridgeResult<LocalTenderReadback>> {
      return {
        ok: true,
        command: "localJournal.readTenderByParkingSession",
        correlationId,
        payload: { tender: tender?.parkingSessionId === parkingSessionId ? tender : null, events: [] },
      };
    },
    getCentralPmsCashSubmissionStatus: (correlationId) => centralPmsCashSubmissionStatus
      ? Promise.resolve({ ok: true, command: "centralPmsCashSubmission.getStatus", correlationId, payload: centralPmsCashSubmissionStatus })
      : unavailable("centralPmsCashSubmission.getStatus", correlationId),
    submitOrReadbackCentralPmsCashSubmission: (correlationId) => centralPmsCashSubmissionStatus
      ? Promise.resolve({ ok: true, command: "centralPmsCashSubmission.submitOrReadback", correlationId, payload: centralPmsCashSubmissionStatus })
      : unavailable("centralPmsCashSubmission.submitOrReadback", correlationId),
    getCentralPmsCashFiscalStatus: (correlationId) => unavailable("centralPmsCashFiscal.getStatus", correlationId),
    submitOrReadbackCentralPmsCashFiscal: (correlationId) => unavailable("centralPmsCashFiscal.submitOrReadback", correlationId),
    getCentralPmsCashReceiptStatus: (correlationId) => unavailable("centralPmsCashReceipt.getStatus", correlationId),
    retrieveOrCheckCentralPmsCashReceipt: (correlationId) => unavailable("centralPmsCashReceipt.retrieveOrCheck", correlationId),
    getCentralPmsCashReceiptPreview: (correlationId) => unavailable("centralPmsCashReceipt.getPreview", correlationId),
    getCentralPmsCashReceiptPrintStatus: (correlationId) => unavailable("centralPmsCashReceiptPrint.getStatus", correlationId),
    submitCentralPmsCashReceiptPrint: (correlationId) => unavailable("centralPmsCashReceiptPrint.submit", correlationId),
    getSalesInvoicePrintHistoryForTender: (correlationId) => unavailable("salesInvoicePrintHistory.getForTender", correlationId),
    getSalesInvoicePrintHistoryForFiscalDocument: (correlationId) => unavailable("salesInvoicePrintHistory.getForFiscalDocument", correlationId),
    getRecentSalesInvoicePrintHistory: (correlationId) => unavailable("salesInvoicePrintHistory.getRecent", correlationId),
    getSalesInvoicePrintHistoryDetail: (correlationId) => unavailable("salesInvoicePrintHistory.getDetail", correlationId),
  };
}

function readPayableBasisState(storageKey: string): PayableBasisStateSnapshot | null {
  try {
    const stored = window.sessionStorage.getItem(storageKey);
    return stored ? JSON.parse(stored) as PayableBasisStateSnapshot : null;
  } catch {
    return null;
  }
}

function writePayableBasisState(storageKey: string, snapshot: PayableBasisStateSnapshot | null) {
  try {
    if (snapshot) {
      window.sessionStorage.setItem(storageKey, JSON.stringify(snapshot));
    } else {
      window.sessionStorage.removeItem(storageKey);
    }
  } catch {
  }
}
