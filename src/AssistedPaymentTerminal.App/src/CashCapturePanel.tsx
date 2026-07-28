import { useEffect, useMemo, useRef, useState } from "react";
import type { AptConfig } from "./config";
import { createCorrelationId } from "./correlation";
import type { PayableBasisResponse } from "./api/centralPmsTypes";
import type { TerminalContext } from "./terminalContext";
import {
  createWebViewLocalJournalBridge,
  type BridgeError,
  type CashCustodySessionSnapshot,
  type CashTenderSnapshot,
  type CentralPmsCashFiscalStatus,
  type CentralPmsCashReceiptPrintStatus,
  type CentralPmsCashReceiptPrintSubmit,
  type CentralPmsCashReceiptPreview,
  type CentralPmsCashReceiptStatus,
  type CentralPmsCashSubmissionStatus,
  type LocalJournalBridge,
  type LocalJournalHealth,
  type LocalTenderReadback,
  type SalesInvoicePrintHistory,
  type SalesInvoicePrintHistoryDetail,
} from "./localJournalBridge";

type PanelStatus =
  | { kind: "idle" }
  | { kind: "checking"; message: string }
  | { kind: "ready"; health: LocalJournalHealth; readback: LocalTenderReadback }
  | { kind: "success"; tender: CashTenderSnapshot; readback: LocalTenderReadback; correlationId: string }
  | { kind: "conflict"; existingTenderId?: string; existingState?: string; message: string; correlationId: string }
  | { kind: "error"; message: string };

type CentralPmsPanelStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashSubmissionStatus; correlationId: string }
  | { kind: "error"; message: string; correlationId: string };

type FiscalPanelStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashFiscalStatus; correlationId: string }
  | { kind: "error"; message: string; correlationId: string };

type ReceiptPanelStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashReceiptStatus; correlationId: string }
  | { kind: "error"; message: string; correlationId: string };

type ReceiptPreviewStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "ready"; preview: CentralPmsCashReceiptPreview; correlationId: string }
  | { kind: "blocked"; message: string; correlationId: string; code: string; detail?: BridgeError["detail"] };

type ReceiptPrintStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "unavailable"; message: string }
  | { kind: "ready"; status: CentralPmsCashReceiptPrintStatus; correlationId: string; lastSubmit?: CentralPmsCashReceiptPrintSubmit }
  | { kind: "error"; message: string; correlationId: string };

type ReceiptPrintHistoryStatus =
  | { kind: "idle" }
  | { kind: "loading"; message: string }
  | { kind: "ready"; history: SalesInvoicePrintHistory; correlationId: string; detail?: SalesInvoicePrintHistoryDetail }
  | { kind: "error"; message: string; correlationId: string };

const defaultBridge = createWebViewLocalJournalBridge();
const denominations = [
  { code: "PHP-1000", value: 1000 },
  { code: "PHP-500", value: 500 },
  { code: "PHP-100", value: 100 },
  { code: "PHP-50", value: 50 },
  { code: "PHP-20", value: 20 },
  { code: "PHP-10", value: 10 },
  { code: "PHP-5", value: 5 },
  { code: "PHP-1", value: 1 },
];

export function CashCapturePanel({
  config,
  context,
  session,
  tariffExpired,
  cashAcceptanceReady = !tariffExpired,
  cashAcceptanceBlockedMessage = "Central PMS has not marked this payable basis ready for cash acceptance.",
  onBeforeCashReceived,
  onLocalPrerequisiteFailure,
  bridge = defaultBridge,
  developmentFixtureLocalCashTenderId,
  autoAdvanceAfterCashReceived = true,
}: {
  config: AptConfig;
  context: TerminalContext;
  session: PayableBasisResponse;
  tariffExpired: boolean;
  cashAcceptanceReady?: boolean;
  cashAcceptanceBlockedMessage?: string;
  onBeforeCashReceived?: (session: PayableBasisResponse) => Promise<{ ok: true; basis: PayableBasisResponse } | { ok: false; message: string }>;
  onLocalPrerequisiteFailure?: (message: string | null) => void;
  bridge?: LocalJournalBridge;
  developmentFixtureLocalCashTenderId?: string;
  autoAdvanceAfterCashReceived?: boolean;
}) {
  const amountDue = session.authoritativeAmountMinorUnits / 100;
  const [amountTenderedText, setAmountTenderedText] = useState(amountDue.toFixed(2));
  const [cashierAttested, setCashierAttested] = useState(false);
  const [denominationCounts, setDenominationCounts] = useState<Record<string, number>>({});
  const [status, setStatus] = useState<PanelStatus>({ kind: "idle" });
  const [centralPmsStatus, setCentralPmsStatus] = useState<CentralPmsPanelStatus>({ kind: "idle" });
  const [fiscalStatus, setFiscalStatus] = useState<FiscalPanelStatus>({ kind: "idle" });
  const [receiptStatus, setReceiptStatus] = useState<ReceiptPanelStatus>({ kind: "idle" });
  const [receiptPreviewStatus, setReceiptPreviewStatus] = useState<ReceiptPreviewStatus>({ kind: "idle" });
  const [receiptPrintStatus, setReceiptPrintStatus] = useState<ReceiptPrintStatus>({ kind: "idle" });
  const [receiptPrintHistoryStatus, setReceiptPrintHistoryStatus] = useState<ReceiptPrintHistoryStatus>({ kind: "idle" });
  const [receiptPrintHistoryOpen, setReceiptPrintHistoryOpen] = useState(false);
  const [receiptPrintHistoryFilter, setReceiptPrintHistoryFilter] = useState<"All" | "Original" | "Reprint" | "Submitted" | "Failed" | "Requires confirmation">("All");
  const autoSubmissionKeys = useRef(new Set<string>());
  const autoFiscalKeys = useRef(new Set<string>());
  const autoReceiptKeys = useRef(new Set<string>());

  const amountTendered = Number(amountTenderedText);
  const changeDue = Number.isFinite(amountTendered) ? Math.max(0, amountTendered - amountDue) : 0;

  useEffect(() => {
    setAmountTenderedText(amountDue.toFixed(2));
    setCashierAttested(false);
    setDenominationCounts({});
    setCentralPmsStatus({ kind: "idle" });
    setFiscalStatus({ kind: "idle" });
    setReceiptStatus({ kind: "idle" });
    setReceiptPreviewStatus({ kind: "idle" });
    setReceiptPrintStatus({ kind: "idle" });
    setReceiptPrintHistoryStatus({ kind: "idle" });
    setReceiptPrintHistoryOpen(false);
    setReceiptPrintHistoryFilter("All");
  }, [amountDue, session.parkingSessionId]);

  useEffect(() => {
    if (!config.nonLiveCashCaptureEnabled || tariffExpired) {
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setStatus({ kind: "checking", message: "Checking local journal readiness..." });

    async function prepare() {
      const health = await bridge.health(correlationId);
      if (cancelled) return;

      if (!health.ok || !health.payload.enabled || !health.payload.healthy) {
        setStatus({ kind: "error", message: health.ok ? "Local journal bridge is disabled." : health.error.message });
        return;
      }

      const readback = await bridge.readTenderByParkingSession(createCorrelationId(), session.parkingSessionId);
      if (cancelled) return;

      setStatus({
        kind: "ready",
        health: health.payload,
        readback: readback.ok ? readback.payload : { tender: null, events: [] },
      });
    }

    void prepare();

    return () => {
      cancelled = true;
    };
  }, [bridge, cashAcceptanceReady, config.nonLiveCashCaptureEnabled, context, session.parkingSessionId, tariffExpired]);

  const existingTender =
    status.kind === "ready" ? status.readback.tender : status.kind === "success" ? status.readback.tender ?? status.tender : null;
  const centralPmsConfig = centralPmsSubmissionConfig(config);
  const fiscalConfig = centralPmsFiscalConfig(config);
  const receiptConfig = centralPmsReceiptConfig(config);
  const centralPmsCommand = centralPmsStatus.kind === "ready" ? centralPmsStatus.status.command : null;
  const canonicalPaymentConfirmed = centralPmsCommand?.status === "Confirmed";
  const fiscalCommand = fiscalStatus.kind === "ready" ? fiscalStatus.status.command : null;
  const fiscalRecorded = fiscalCommand?.status === "Recorded";
  const receiptCommand = receiptStatus.kind === "ready" ? receiptStatus.status.command : null;
  const receiptPreviewEligible = receiptCommand?.status === "Available" || receiptCommand?.status === "Voided";
  const receiptPrintEligible = receiptPreviewEligible && config.receiptPrintingEnabled;
  const transactionState = buildCashierTransactionState({
    localTender: existingTender,
    centralPmsStatus,
    fiscalStatus,
    receiptStatus,
    receiptPreviewEligible,
    exitAuthorizationContractAvailable: false,
  });

  useEffect(() => {
    if (!config.centralPmsCashSubmissionEnabled || !existingTender || existingTender.currentLocalState !== "CashReceived") {
      return;
    }

    if (!centralPmsConfig.valid) {
      setCentralPmsStatus({ kind: "unavailable", message: centralPmsConfig.message });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setCentralPmsStatus({ kind: "loading", message: "Checking Central PMS status..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashSubmissionStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setCentralPmsStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setCentralPmsStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [bridge, centralPmsConfig.message, centralPmsConfig.valid, config.centralPmsCashSubmissionEnabled, existingTender?.id, existingTender?.currentLocalState]);

  useEffect(() => {
    if (!existingTender || existingTender.currentLocalState !== "CashReceived" || !canonicalPaymentConfirmed) {
      setFiscalStatus({ kind: "idle" });
      return;
    }

    if (!config.centralPmsFiscalIssuanceEnabled) {
      setFiscalStatus({ kind: "idle" });
      return;
    }

    if (!fiscalConfig.valid) {
      setFiscalStatus({ kind: "unavailable", message: fiscalConfig.message });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setFiscalStatus({ kind: "loading", message: "Checking fiscal issuance status..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashFiscalStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setFiscalStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setFiscalStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [
    bridge,
    canonicalPaymentConfirmed,
    config.centralPmsFiscalIssuanceEnabled,
    existingTender?.id,
    existingTender?.currentLocalState,
    fiscalConfig.message,
    fiscalConfig.valid,
  ]);

  useEffect(() => {
    if (!existingTender || existingTender.currentLocalState !== "CashReceived" || !fiscalRecorded) {
      setReceiptStatus({ kind: "idle" });
      setReceiptPreviewStatus({ kind: "idle" });
      return;
    }

    if (!config.centralPmsReceiptRetrievalEnabled) {
      setReceiptStatus({ kind: "idle" });
      return;
    }

    if (!receiptConfig.valid) {
      setReceiptStatus({ kind: "unavailable", message: receiptConfig.message });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setReceiptStatus({ kind: "loading", message: "Checking receipt availability..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashReceiptStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setReceiptStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setReceiptStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [
    bridge,
    config.centralPmsReceiptRetrievalEnabled,
    existingTender?.id,
    existingTender?.currentLocalState,
    fiscalRecorded,
    receiptConfig.message,
    receiptConfig.valid,
  ]);

  useEffect(() => {
    if (!existingTender || !receiptPrintEligible) {
      setReceiptPrintStatus({ kind: "idle" });
      return;
    }

    if (!config.receiptPrintingEnabled) {
      setReceiptPrintStatus({ kind: "idle" });
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setReceiptPrintStatus({ kind: "loading", message: "Checking printer status..." });

    async function loadStatus() {
      const result = await bridge.getCentralPmsCashReceiptPrintStatus(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setReceiptPrintStatus({ kind: "ready", status: result.payload, correlationId });
      } else {
        setReceiptPrintStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadStatus();

    return () => {
      cancelled = true;
    };
  }, [bridge, config.receiptPrintingEnabled, existingTender?.id, receiptPrintEligible]);

  useEffect(() => {
    if (!existingTender || !receiptPrintEligible) {
      setReceiptPrintHistoryStatus({ kind: "idle" });
      setReceiptPrintHistoryOpen(false);
      return;
    }

    let cancelled = false;
    const correlationId = createCorrelationId();
    setReceiptPrintHistoryStatus({ kind: "loading", message: "Loading Sales Invoice print history..." });

    async function loadHistory() {
      const result = await bridge.getSalesInvoicePrintHistoryForTender(correlationId, existingTender!.id);
      if (cancelled) return;

      if (result.ok) {
        setReceiptPrintHistoryStatus({ kind: "ready", history: result.payload, correlationId });
      } else {
        setReceiptPrintHistoryStatus({ kind: "error", message: result.error.message, correlationId });
      }
    }

    void loadHistory();

    return () => {
      cancelled = true;
    };
  }, [bridge, existingTender?.id, receiptPrintEligible]);

  useEffect(() => {
    if (!autoAdvanceAfterCashReceived || !existingTender || existingTender.currentLocalState !== "CashReceived" || !centralPmsConfig.valid) {
      return;
    }

    if (centralPmsStatus.kind !== "ready") {
      return;
    }

    const command = centralPmsStatus.status.command;
    const eligibleStatuses = new Set([undefined, "Pending", "ReadbackRequired", "RetryPending"]);
    if (!eligibleStatuses.has(command?.status)) {
      return;
    }

    const key = `${existingTender.id}:payment:${command?.status ?? "none"}:${command?.attemptCount ?? 0}`;
    if (autoSubmissionKeys.current.has(key)) {
      return;
    }

    autoSubmissionKeys.current.add(key);
    void submitOrReadbackCentralPms();
  }, [autoAdvanceAfterCashReceived, centralPmsConfig.valid, centralPmsStatus, existingTender?.id, existingTender?.currentLocalState]);

  useEffect(() => {
    if (!autoAdvanceAfterCashReceived || !existingTender || existingTender.currentLocalState !== "CashReceived" || !canonicalPaymentConfirmed || !fiscalConfig.valid) {
      return;
    }

    if (fiscalStatus.kind !== "ready") {
      return;
    }

    const command = fiscalStatus.status.command;
    const eligibleStatuses = new Set([undefined, "Pending", "ReadbackRequired", "RetryPending", "Unknown"]);
    if (!eligibleStatuses.has(command?.status)) {
      return;
    }

    const key = `${existingTender.id}:fiscal:${command?.status ?? "none"}:${command?.attemptCount ?? 0}`;
    if (autoFiscalKeys.current.has(key)) {
      return;
    }

    autoFiscalKeys.current.add(key);
    void submitOrReadbackFiscal();
  }, [autoAdvanceAfterCashReceived, canonicalPaymentConfirmed, existingTender?.id, existingTender?.currentLocalState, fiscalConfig.valid, fiscalStatus]);

  useEffect(() => {
    if (!autoAdvanceAfterCashReceived || !existingTender || existingTender.currentLocalState !== "CashReceived" || !canonicalPaymentConfirmed || !fiscalRecorded || !receiptConfig.valid) {
      return;
    }

    if (receiptStatus.kind !== "ready") {
      return;
    }

    const command = receiptStatus.status.command;
    const eligibleStatuses = new Set([undefined, "Pending", "NotReady", "RetryPending", "Unavailable"]);
    if (!eligibleStatuses.has(command?.status) || command?.lastRetryable === false) {
      return;
    }

    const key = `${existingTender.id}:receipt:${command?.status ?? "none"}:${command?.attemptCount ?? 0}`;
    if (autoReceiptKeys.current.has(key)) {
      return;
    }

    autoReceiptKeys.current.add(key);
    void retrieveOrCheckReceipt();
  }, [
    autoAdvanceAfterCashReceived,
    canonicalPaymentConfirmed,
    existingTender?.id,
    existingTender?.currentLocalState,
    fiscalRecorded,
    receiptConfig.valid,
    receiptStatus,
  ]);

  const denominationPayload = useMemo(
    () =>
      denominations.map((denomination) => ({
        denominationCode: denomination.code,
        denominationValue: denomination.value,
        quantity: denominationCounts[denomination.code] ?? 0,
      })).filter((denomination) => denomination.quantity > 0),
    [denominationCounts],
  );

  if (!config.nonLiveCashCaptureEnabled) {
    return null;
  }

  if (tariffExpired) {
    return (
      <section className="cash-capture-panel unavailable" aria-label="Non-live cash capture unavailable">
        <p className="eyebrow">Non-live development simulation</p>
        <h2>Cash capture unavailable</h2>
        <p>Cash custody recording is blocked until the payable basis is current and non-expired.</p>
      </section>
    );
  }

  async function recordCashReceived() {
    if (!Number.isFinite(amountTendered) || amountTendered < amountDue) {
      setStatus({ kind: "error", message: "Amount tendered must be greater than or equal to amount due." });
      return;
    }

    if (!cashierAttested) {
      setStatus({ kind: "error", message: "Cashier attestation is required before CASH_RECEIVED." });
      return;
    }

    if (status.kind !== "ready") {
      setStatus({ kind: "error", message: "Local journal is not ready." });
      return;
    }

    const revalidation = onBeforeCashReceived ? await onBeforeCashReceived(session) : { ok: true as const, basis: session };
    if (!revalidation.ok) {
      onLocalPrerequisiteFailure?.(revalidation.message);
      setStatus({ kind: "error", message: revalidation.message });
      setCashierAttested(false);
      return;
    }

    onLocalPrerequisiteFailure?.(null);
    const authoritativeBasis = revalidation.basis;
    const correlationId = createCorrelationId();
    const sessionResult = await bridge.createOrGetDevelopmentSession(createCorrelationId(), {
      cashierId: context.cashierId,
      authenticatedCashierSessionReference: `dev-auth:${context.cashierId}:${context.shiftId}`,
      cashierShiftId: context.shiftId,
      terminalId: context.terminalId,
      siteId: context.siteId,
      siteGroupId: context.siteGroupId,
      posServerId: context.posServerId,
      openingCashAmount: 0,
    });

    if (!sessionResult.ok) {
      setStatus({ kind: "error", message: sessionResult.error.message });
      return;
    }

    const cashSession = sessionResult.payload;
    const tariffSnapshotId = authoritativeBasis.tariffSnapshotId;
    const started = await bridge.startTender(correlationId, {
      ...(developmentFixtureLocalCashTenderId ? { localCashTenderId: developmentFixtureLocalCashTenderId } : {}),
      cashCustodySessionId: cashSession.id,
      parkingSessionId: authoritativeBasis.parkingSessionId,
      tariffSnapshotId,
      currency: authoritativeBasis.currency,
      amountDue: authoritativeBasis.authoritativeAmountMinorUnits / 100,
      amountTendered,
      localIdempotencyIdentity: `local-cash:${authoritativeBasis.parkingSessionId}:${tariffSnapshotId}`,
    });

    if (!started.ok) {
      setConflict(started.error, correlationId);
      return;
    }

    const received = await bridge.recordCashReceived(correlationId, {
      localCashTenderId: started.payload.id,
      cashierAttested,
      statutoryTenderEvidence: buildStatutoryTenderEvidence(authoritativeBasis),
      denominations: denominationPayload,
    });

    if (!received.ok) {
      setStatus({ kind: "error", message: received.error.message });
      return;
    }

    const readback = await bridge.readTenderByParkingSession(createCorrelationId(), authoritativeBasis.parkingSessionId);
    setStatus({
      kind: "success",
      tender: received.payload,
      readback: readback.ok ? readback.payload : { tender: received.payload, events: [] },
      correlationId,
    });
  }

  async function submitOrReadbackCentralPms() {
    if (!existingTender) {
      setCentralPmsStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!centralPmsConfig.valid) {
      setCentralPmsStatus({ kind: "unavailable", message: centralPmsConfig.message });
      return;
    }

    const correlationId = createCorrelationId();
    setCentralPmsStatus({ kind: "loading", message: "Submitting or checking Central PMS..." });
    const result = await bridge.submitOrReadbackCentralPmsCashSubmission(correlationId, existingTender.id);
    if (result.ok) {
      setCentralPmsStatus({ kind: "ready", status: result.payload, correlationId });
    } else {
      setCentralPmsStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function submitOrReadbackFiscal() {
    if (!existingTender) {
      setFiscalStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!canonicalPaymentConfirmed) {
      setFiscalStatus({ kind: "error", message: "Canonical payment must be confirmed before fiscal issuance.", correlationId: "unavailable" });
      return;
    }

    if (!fiscalConfig.valid) {
      setFiscalStatus({ kind: "unavailable", message: fiscalConfig.message });
      return;
    }

    const correlationId = createCorrelationId();
    setFiscalStatus({ kind: "loading", message: "Submitting or checking fiscal issuance..." });
    const result = await bridge.submitOrReadbackCentralPmsCashFiscal(correlationId, existingTender.id);
    if (result.ok) {
      setFiscalStatus({ kind: "ready", status: result.payload, correlationId });
    } else {
      setFiscalStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function retrieveOrCheckReceipt() {
    if (!existingTender) {
      setReceiptStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!fiscalRecorded) {
      setReceiptStatus({ kind: "error", message: "Fiscal document must be recorded before receipt retrieval.", correlationId: "unavailable" });
      return;
    }

    if (!receiptConfig.valid) {
      setReceiptStatus({ kind: "unavailable", message: receiptConfig.message });
      return;
    }

    const correlationId = createCorrelationId();
    setReceiptStatus({ kind: "loading", message: "Retrieving or checking receipt availability..." });
    const result = await bridge.retrieveOrCheckCentralPmsCashReceipt(correlationId, existingTender.id);
    if (result.ok) {
      setReceiptStatus({ kind: "ready", status: result.payload, correlationId });
      setReceiptPreviewStatus({ kind: "idle" });
    } else {
      setReceiptStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function viewReceiptPreview() {
    if (!existingTender) {
      setReceiptPreviewStatus({
        kind: "blocked",
        message: "Local CASH_RECEIVED tender is not available.",
        code: "local_tender_unavailable",
        correlationId: "unavailable",
      });
      return;
    }

    if (!receiptPreviewEligible) {
      setReceiptPreviewStatus({
        kind: "blocked",
        message: "Receipt preview is available only after authoritative receipt presentation is available.",
        code: "receipt_preview_not_available",
        correlationId: "unavailable",
      });
      return;
    }

    const correlationId = createCorrelationId();
    setReceiptPreviewStatus({ kind: "loading", message: "Loading read-only receipt preview..." });
    const result = await bridge.getCentralPmsCashReceiptPreview(correlationId, existingTender.id);

    if (result.ok) {
      setReceiptPreviewStatus({ kind: "ready", preview: result.payload, correlationId });
    } else {
      setReceiptPreviewStatus({
        kind: "blocked",
        message: result.error.message,
        code: result.error.code,
        detail: result.error.detail,
        correlationId,
      });
    }
  }

  async function printReceipt() {
    if (!existingTender) {
      setReceiptPrintStatus({ kind: "error", message: "Local CASH_RECEIVED tender is not available.", correlationId: "unavailable" });
      return;
    }

    if (!receiptPrintEligible) {
      setReceiptPrintStatus({ kind: "error", message: "Sales Invoice printing requires an available authoritative receipt.", correlationId: "unavailable" });
      return;
    }

    const correlationId = createCorrelationId();
    setReceiptPrintStatus({ kind: "loading", message: "Preparing Sales Invoice for printer..." });
    const result = await bridge.submitCentralPmsCashReceiptPrint(correlationId, existingTender.id);

    if (result.ok) {
      const statusResult = await bridge.getCentralPmsCashReceiptPrintStatus(createCorrelationId(), existingTender.id);
      const refreshedStatus = statusResult.ok
        ? statusResult.payload
        : { enabled: true, configurationValid: true, configurationMessage: result.payload.safeMessage, command: receiptCommand, jobs: [] };
      const refreshedJobs = refreshedStatus.jobs.some((job) => job.printJobId === result.payload.job.printJobId)
        ? refreshedStatus.jobs
        : [...refreshedStatus.jobs, result.payload.job];
      setReceiptPrintStatus({
        kind: "ready",
        status: { ...refreshedStatus, jobs: refreshedJobs },
        correlationId,
        lastSubmit: result.payload,
      });
      const historyResult = await bridge.getSalesInvoicePrintHistoryForTender(createCorrelationId(), existingTender.id);
      if (historyResult.ok) {
        setReceiptPrintHistoryStatus({ kind: "ready", history: historyResult.payload, correlationId });
      }
    } else {
      setReceiptPrintStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function openPrintHistoryDetail(printJobId: string) {
    const currentHistory = receiptPrintHistoryStatus.kind === "ready" ? receiptPrintHistoryStatus.history : null;
    const correlationId = createCorrelationId();
    const result = await bridge.getSalesInvoicePrintHistoryDetail(correlationId, printJobId);

    if (result.ok) {
      setReceiptPrintHistoryStatus({
        kind: "ready",
        history: currentHistory ?? {
          scope: "terminalCashTenderId",
          summary: {
            hasHistory: true,
            originalStatus: result.payload.job.statusLabel,
            reprintCount: result.payload.job.classification === "Reprint" ? 1 : 0,
            latestCopySequence: result.payload.job.copySequence,
            latestStatus: result.payload.job.statusLabel,
            latestPrinterName: result.payload.job.configuredPrinterName,
            latestPaperWidthMm: result.payload.job.paperWidthMm,
            latestAttemptAt: result.payload.job.requestedAt,
            requiresConfirmation: result.payload.job.status === "UnknownAfterRestart",
            attentionRequired: result.payload.indicators.some((indicator) => indicator.severity === "attention"),
          },
          jobs: [result.payload.job],
          indicators: result.payload.indicators,
        },
        correlationId,
        detail: result.payload,
      });
    } else {
      setReceiptPrintHistoryStatus({ kind: "error", message: result.error.message, correlationId });
    }
  }

  async function reloadLocalTender() {
    const readback = await bridge.readTenderByParkingSession(createCorrelationId(), session.parkingSessionId);
    if (!readback.ok) {
      setStatus({ kind: "error", message: readback.error.message });
      return;
    }

    if (status.kind === "ready") {
      setStatus({ ...status, readback: readback.payload });
    } else {
      setStatus({ kind: "success", tender: readback.payload.tender!, readback: readback.payload, correlationId: "readback" });
    }
  }

  async function attemptDuplicateTender() {
    if (status.kind !== "success") {
      return;
    }

    const cashSessionId = status.readback.tender?.cashCustodySessionId;
    if (!cashSessionId) {
      return;
    }

    const correlationId = createCorrelationId();
    const duplicate = await bridge.startTender(correlationId, {
      cashCustodySessionId: cashSessionId,
      parkingSessionId: session.parkingSessionId,
      tariffSnapshotId: session.tariffSnapshotId,
      currency: session.currency,
      amountDue,
      amountTendered: amountDue,
      localIdempotencyIdentity: `local-cash-duplicate:${session.parkingSessionId}`,
    });

    if (!duplicate.ok) {
      setConflict(duplicate.error, correlationId);
    }
  }

  function setConflict(error: BridgeError, correlationId: string) {
    setStatus({
      kind: "conflict",
      existingTenderId: error.detail?.existingCashTenderId,
      existingState: error.detail?.existingCashTenderState,
      message: error.message,
      correlationId,
    });
  }

  return (
    <section className="cash-capture-panel" aria-label="Non-live cash custody capture">
      <div className="section-heading">
        <p className="eyebrow">Non-live development simulation</p>
        <h2>Local cash custody capture</h2>
      </div>

      <div className="authority-warning" role="status">
        {existingTender?.currentLocalState === "CashReceived" || status.kind === "success" ? (
          <>
            <strong>State at local cash capture:</strong> Cash received locally. At this checkpoint, canonical payment had not yet been submitted and fiscal issuance had not yet started. Exit authorization was unavailable.
          </>
        ) : (
          <>
            <strong>Cash has not yet been recorded at this terminal.</strong> Complete denomination entry and attest physical receipt before recording CASH_RECEIVED.
          </>
        )}
      </div>

      {status.kind === "checking" && <p className="support-line">{status.message}</p>}
      {status.kind === "error" && <p className="cash-error" role="alert">{status.message}</p>}
      {status.kind === "conflict" && (
        <div className="cash-error" role="alert">
          <strong>Duplicate local cash tender rejected.</strong>
          <p>{status.message}</p>
          <p>Existing local tender ID: {status.existingTenderId ?? "Unavailable"}</p>
          <p>Existing local state: {status.existingState ?? "Unavailable"}</p>
          <p>Correlation ID: {status.correlationId}</p>
        </div>
      )}

      {existingTender && status.kind !== "success" && (
        <div className="cash-readback">
          <h3>Existing local custody record</h3>
          <p>Local tender ID: {existingTender.id}</p>
          <p>Local state: {existingTender.currentLocalState}</p>
          <p>Correlation ID: {existingTender.correlationId}</p>
          {existingTender.statutoryDiscountDecisionCommandId && (
            <dl className="central-pms-details" data-testid="statutory-tender-evidence">
              <div><dt>Statutory decision</dt><dd>{existingTender.statutoryDiscountDecisionCommandId}</dd></div>
              <div><dt>Statutory application</dt><dd>{existingTender.statutoryDiscountPayableBasisApplicationCommandId ?? "Unavailable"}</dd></div>
              <div><dt>Applied tariff snapshot</dt><dd>{existingTender.statutoryAppliedTariffSnapshotId ?? existingTender.tariffSnapshotId}</dd></div>
              <div><dt>Final statutory amount</dt><dd>{formatMoney(existingTender.statutoryFinalAmountMinorUnits, existingTender.statutoryCurrency ?? existingTender.currency)}</dd></div>
              <div><dt>Revalidated at</dt><dd>{existingTender.statutoryImmediateRevalidatedAt ? formatDateTime(existingTender.statutoryImmediateRevalidatedAt) : "Unavailable"}</dd></div>
            </dl>
          )}
          <button className="secondary-action" type="button" onClick={attemptDuplicateTender}>
            Attempt duplicate cash tender
          </button>
        </div>
      )}

      {!existingTender && (
        <>
          <div className="cash-grid">
            <label>
              Amount due
              <input value={formatAmount(amountDue)} readOnly />
            </label>
            <label>
              Amount tendered
              <input
                type="number"
                min="0"
                step="0.01"
                value={amountTenderedText}
                onChange={(event) => setAmountTenderedText(event.target.value)}
              />
            </label>
            <label>
              Change due
              <input value={formatAmount(changeDue)} readOnly />
            </label>
          </div>

          <fieldset className="denomination-grid">
            <legend>Optional denomination inputs</legend>
            {denominations.map((denomination) => (
              <label key={denomination.code}>
                {denomination.code}
                <input
                  type="number"
                  min="0"
                  step="1"
                  value={denominationCounts[denomination.code] ?? 0}
                  onChange={(event) =>
                    setDenominationCounts((current) => ({
                      ...current,
                      [denomination.code]: Math.max(0, Math.floor(Number(event.target.value) || 0)),
                    }))
                  }
                />
              </label>
            ))}
          </fieldset>

          <label className="attestation-row">
            <input
              type="checkbox"
              checked={cashierAttested}
              onChange={(event) => setCashierAttested(event.target.checked)}
            />
            I attest: cash received at this terminal.
          </label>

          <button type="button" disabled={!cashAcceptanceReady || status.kind === "checking"} onClick={() => void recordCashReceived()}>
            Record Cash Received
          </button>
        </>
      )}

      {status.kind === "success" && (
        <div className="cash-success" role="status">
          <h3>Cash received locally</h3>
          <p>Local tender ID: {status.tender.id}</p>
          <p>Local state: {status.tender.currentLocalState}</p>
          <p>Correlation ID: {status.correlationId}</p>
          {status.tender.statutoryDiscountDecisionCommandId && (
            <dl className="central-pms-details" data-testid="statutory-tender-evidence">
              <div><dt>Statutory decision</dt><dd>{status.tender.statutoryDiscountDecisionCommandId}</dd></div>
              <div><dt>Statutory application</dt><dd>{status.tender.statutoryDiscountPayableBasisApplicationCommandId ?? "Unavailable"}</dd></div>
              <div><dt>Applied tariff snapshot</dt><dd>{status.tender.statutoryAppliedTariffSnapshotId ?? status.tender.tariffSnapshotId}</dd></div>
              <div><dt>Final statutory amount</dt><dd>{formatMoney(status.tender.statutoryFinalAmountMinorUnits, status.tender.statutoryCurrency ?? status.tender.currency)}</dd></div>
              <div><dt>Revalidated at</dt><dd>{status.tender.statutoryImmediateRevalidatedAt ? formatDateTime(status.tender.statutoryImmediateRevalidatedAt) : "Unavailable"}</dd></div>
            </dl>
          )}
          <p>Event history entries: {status.readback.events.length}</p>
        </div>
      )}

      {existingTender?.currentLocalState === "CashReceived" && (
        <CashierTransactionStatePanel state={transactionState} />
      )}

      {config.centralPmsCashSubmissionEnabled && existingTender?.currentLocalState === "CashReceived" && (
        <CentralPmsCanonicalPaymentPanel
          centralPmsStatus={centralPmsStatus}
          onSubmitOrReadback={() => void submitOrReadbackCentralPms()}
        />
      )}

      {existingTender?.currentLocalState === "CashReceived" && canonicalPaymentConfirmed && (
        <CentralPmsFiscalIssuancePanel
          enabled={config.centralPmsFiscalIssuanceEnabled}
          fiscalStatus={fiscalStatus}
          onSubmitOrReadback={() => void submitOrReadbackFiscal()}
        />
      )}

      {existingTender?.currentLocalState === "CashReceived" && canonicalPaymentConfirmed && fiscalRecorded && (
        <CentralPmsReceiptAvailabilityPanel
          enabled={config.centralPmsReceiptRetrievalEnabled}
          previewEnabled={config.receiptPreviewEnabled}
          receiptStatus={receiptStatus}
          onRetrieveOrCheck={() => void retrieveOrCheckReceipt()}
          onViewPreview={() => void viewReceiptPreview()}
        />
      )}

      <ReceiptPreviewSurface
        status={receiptPreviewStatus}
        configuredPaperWidthMm={config.receiptPaperWidthMm}
        paperWidthWarning={config.receiptPaperWidthWarning}
        onClose={() => setReceiptPreviewStatus({ kind: "idle" })}
      />

      <ReceiptPrintPanel
        enabled={config.receiptPrintingEnabled}
        configuredPrinterName={config.receiptPrinterName}
        configuredPaperWidthMm={config.receiptPaperWidthMm}
        receiptAvailable={receiptPreviewEligible}
        status={receiptPrintStatus}
        onPrint={() => void printReceipt()}
      />

      <SalesInvoicePrintHistoryPanel
        receiptAvailable={receiptPreviewEligible}
        status={receiptPrintHistoryStatus}
        open={receiptPrintHistoryOpen}
        filter={receiptPrintHistoryFilter}
        onOpen={() => setReceiptPrintHistoryOpen(true)}
        onClose={() => setReceiptPrintHistoryOpen(false)}
        onFilter={setReceiptPrintHistoryFilter}
        onDetail={(printJobId) => void openPrintHistoryDetail(printJobId)}
      />

      <button className="secondary-action" type="button" onClick={() => void reloadLocalTender()}>
        Reload local tender
      </button>
    </section>
  );
}

function formatAmount(value: number): string {
  return value.toFixed(2);
}

function formatMoney(minorUnits?: number | null, currency = "PHP"): string {
  if (minorUnits == null) {
    return "Unavailable";
  }

  return new Intl.NumberFormat("en-PH", { style: "currency", currency }).format(minorUnits / 100);
}

function buildStatutoryTenderEvidence(session: PayableBasisResponse) {
  const readiness = session.statutoryDiscountReadiness;
  if (!readiness?.applicable) {
    return null;
  }

  return {
    statutoryDiscountDecisionCommandId: readiness.statutoryDiscountDecisionCommandId ?? null,
    statutoryDiscountPayableBasisApplicationCommandId: readiness.statutoryDiscountPayableBasisApplicationCommandId ?? session.statutoryDiscountApplicationId ?? null,
    statutoryDiscountValidationId: readiness.statutoryDiscountValidationId ?? session.statutoryDiscountValidationId ?? null,
    originalTariffSnapshotId: readiness.originalTariffSnapshotId ?? session.originalTariffSnapshotId ?? null,
    appliedTariffSnapshotId: readiness.appliedTariffSnapshotId ?? session.appliedTariffSnapshotId ?? session.tariffSnapshotId,
    originalAmountMinorUnits: readiness.originalAmountMinorUnits ?? null,
    finalAmountMinorUnits: readiness.finalPayableAmountMinorUnits ?? session.authoritativeAmountMinorUnits,
    currency: readiness.currency ?? session.currency,
    amountAcknowledged: true,
    amountAcknowledgedAt: new Date().toISOString(),
    immediateRevalidationOutcome: session.revalidationOutcome ?? null,
    immediateRevalidatedAt: new Date().toISOString(),
    centralPmsCorrelationId: session.correlationId,
    readinessStatus: readiness.payableBasisReadinessStatus ?? null,
    readinessAction: readiness.payableBasisReadinessAction ?? null,
  };
}

function centralPmsSubmissionConfig(config: AptConfig): { valid: boolean; message: string } {
  if (!config.centralPmsCashSubmissionEnabled) {
    return { valid: false, message: "Central PMS cash submission is disabled." };
  }

  try {
    const url = new URL(config.centralPmsBaseUrl);
    if (!["http:", "https:"].includes(url.protocol) || url.hostname.endsWith(".example.invalid")) {
      return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for cash submission." };
    }

    return { valid: true, message: "Central PMS cash submission is available." };
  } catch {
    return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for cash submission." };
  }
}

function centralPmsFiscalConfig(config: AptConfig): { valid: boolean; message: string } {
  if (!config.centralPmsFiscalIssuanceEnabled) {
    return { valid: false, message: "Central PMS fiscal issuance is disabled." };
  }

  try {
    const url = new URL(config.centralPmsBaseUrl);
    if (!["http:", "https:"].includes(url.protocol) || url.hostname.endsWith(".example.invalid")) {
      return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for fiscal issuance." };
    }

    return { valid: true, message: "Central PMS fiscal issuance is available." };
  } catch {
    return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for fiscal issuance." };
  }
}

function centralPmsReceiptConfig(config: AptConfig): { valid: boolean; message: string } {
  if (!config.centralPmsReceiptRetrievalEnabled) {
    return { valid: false, message: "Central PMS receipt retrieval is disabled." };
  }

  try {
    const url = new URL(config.centralPmsBaseUrl);
    if (!["http:", "https:"].includes(url.protocol) || url.hostname.endsWith(".example.invalid")) {
      return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for receipt retrieval." };
    }

    return { valid: true, message: "Central PMS receipt retrieval is available." };
  } catch {
    return { valid: false, message: "CENTRAL_PMS_BASE_URL is not configured for receipt retrieval." };
  }
}

type CashierTransactionState = {
  custody: string;
  terminalCashSubmission: string;
  paymentFinality: string;
  fiscalIssuance: string;
  receiptPresentation: string;
  exitAuthorization: string;
  completion: "TRANSACTION_IN_PROGRESS" | "TRANSACTION_REQUIRES_RETRY" | "TRANSACTION_REQUIRES_SUPPORT" | "TRANSACTION_COMPLETE";
  headline: string;
  supportReference: string | null;
  latestUpdatedAt: string | null;
  terminalCashTenderId: string | null;
  paymentConfirmationId: string | null;
  fiscalDocumentNumber: string | null;
};

function buildCashierTransactionState({
  localTender,
  centralPmsStatus,
  fiscalStatus,
  receiptStatus,
  receiptPreviewEligible,
  exitAuthorizationContractAvailable,
}: {
  localTender: CashTenderSnapshot | null;
  centralPmsStatus: CentralPmsPanelStatus;
  fiscalStatus: FiscalPanelStatus;
  receiptStatus: ReceiptPanelStatus;
  receiptPreviewEligible: boolean;
  exitAuthorizationContractAvailable: boolean;
}): CashierTransactionState {
  const paymentCommand = centralPmsStatus.kind === "ready" ? centralPmsStatus.status.command : null;
  const fiscalCommand = fiscalStatus.kind === "ready" ? fiscalStatus.status.command : null;
  const receiptCommand = receiptStatus.kind === "ready" ? receiptStatus.status.command : null;
  const paymentTerminal = paymentCommand?.status === "Conflict" || paymentCommand?.status === "Rejected" || centralPmsStatus.kind === "error";
  const fiscalTerminal = fiscalCommand?.status === "Conflict" || fiscalCommand?.status === "Rejected" || fiscalStatus.kind === "error";
  const receiptTerminal =
    receiptCommand?.status === "Inconsistent"
    || receiptCommand?.status === "Rejected"
    || receiptCommand?.status === "Unsupported"
    || receiptCommand?.status === "Malformed"
    || receiptStatus.kind === "error";
  const retryRequired =
    paymentCommand?.status === "RetryPending"
    || fiscalCommand?.status === "RetryPending"
    || fiscalCommand?.status === "ReadbackRequired"
    || fiscalCommand?.status === "Unknown"
    || receiptCommand?.status === "RetryPending"
    || receiptCommand?.status === "Unavailable"
    || receiptCommand?.status === "NotReady";
  const paymentFinal = paymentCommand?.status === "Confirmed";
  const fiscalRecorded = fiscalCommand?.status === "Recorded";
  const receiptAvailable = receiptCommand?.status === "Available" || receiptCommand?.status === "Voided" || receiptPreviewEligible;
  const exitAuthorization = exitAuthorizationContractAvailable ? "EXIT_AUTHORIZATION_NOT_EVALUATED" : "EXIT_AUTHORIZATION_READBACK_CONTRACT_MISSING";
  const authorizationBlocked = receiptAvailable && !exitAuthorizationContractAvailable;
  // Completion requires durable cash custody plus authoritative payment, fiscal, receipt, and ExitAuthorization readback; no local ExitAuthorization inference is allowed.
  const complete = Boolean(localTender?.currentLocalState === "CashReceived" && paymentFinal && fiscalRecorded && receiptAvailable && exitAuthorizationContractAvailable);

  let completion: CashierTransactionState["completion"] = "TRANSACTION_IN_PROGRESS";
  if (paymentTerminal || fiscalTerminal || receiptTerminal || authorizationBlocked) {
    completion = "TRANSACTION_REQUIRES_SUPPORT";
  } else if (retryRequired) {
    completion = "TRANSACTION_REQUIRES_RETRY";
  } else if (complete) {
    completion = "TRANSACTION_COMPLETE";
  }

  return {
    custody: localTender?.currentLocalState === "CashReceived" ? "CASH_RECEIVED" : "CASH_NOT_RECEIVED",
    terminalCashSubmission: mapPaymentSubmissionState(paymentCommand, centralPmsStatus),
    paymentFinality: mapPaymentFinalityState(paymentCommand, centralPmsStatus),
    fiscalIssuance: mapFiscalState(fiscalCommand, fiscalStatus),
    receiptPresentation: mapReceiptState(receiptCommand, receiptStatus),
    exitAuthorization,
    completion,
    headline: completionLabel(completion),
    supportReference:
      receiptCommand?.lastCentralPmsCorrelationId
      ?? receiptCommand?.retrievalCorrelationId
      ?? fiscalCommand?.fiscalCorrelationId
      ?? paymentCommand?.originalCorrelationId
      ?? localTender?.correlationId
      ?? null,
    latestUpdatedAt: receiptCommand?.updatedAt ?? fiscalCommand?.updatedAt ?? paymentCommand?.updatedAt ?? localTender?.updatedAt ?? null,
    terminalCashTenderId: localTender?.id ?? paymentCommand?.terminalCashTenderId ?? fiscalCommand?.terminalCashTenderId ?? receiptCommand?.terminalCashTenderId ?? null,
    paymentConfirmationId: paymentCommand?.canonicalPaymentConfirmationId ?? receiptCommand?.canonicalPaymentConfirmationId ?? null,
    fiscalDocumentNumber: receiptCommand?.fiscalDocumentNumber ?? fiscalCommand?.fiscalDocumentNumber ?? null,
  };
}

function mapPaymentSubmissionState(command: CentralPmsCashSubmissionStatus["command"], status: CentralPmsPanelStatus): string {
  if (status.kind === "unavailable") return "TERMINAL_CASH_SUBMISSION_RETRYABLE";
  if (status.kind === "error") return "TERMINAL_CASH_SUBMISSION_RETRYABLE";
  if (!command) return "TERMINAL_CASH_NOT_SUBMITTED";
  if (command.status === "Confirmed") return "TERMINAL_CASH_SUBMISSION_ACCEPTED";
  if (command.status === "Conflict" || command.status === "Rejected") return "TERMINAL_CASH_SUBMISSION_FAILED";
  if (command.status === "RetryPending") return "TERMINAL_CASH_SUBMISSION_RETRYABLE";
  if (command.status === "ReadbackRequired") return "TERMINAL_CASH_SUBMISSION_ACCEPTED";
  if (command.status === "Submitting") return "TERMINAL_CASH_SUBMISSION_PENDING";
  return "TERMINAL_CASH_SUBMISSION_PENDING";
}

function mapPaymentFinalityState(command: CentralPmsCashSubmissionStatus["command"], status: CentralPmsPanelStatus): string {
  if (command?.status === "Confirmed") return "PAYMENT_FINAL";
  if (command?.status === "Conflict" || command?.status === "Rejected") return "PAYMENT_FINALITY_FAILED";
  if (command?.status === "RetryPending" || status.kind === "error" || status.kind === "unavailable") return "PAYMENT_FINALITY_RETRYABLE";
  if (command?.status === "ReadbackRequired" || command?.status === "Submitting") return "PAYMENT_FINALITY_PENDING";
  return "PAYMENT_FINALITY_PENDING";
}

function mapFiscalState(command: CentralPmsCashFiscalStatus["command"], status: FiscalPanelStatus): string {
  if (status.kind === "idle") return "FISCAL_NOT_STARTED";
  if (status.kind === "loading") return "FISCAL_REQUESTED";
  if (status.kind === "unavailable" || status.kind === "error") return "FISCAL_RETRYABLE";
  if (!command) return "FISCAL_NOT_STARTED";
  if (command.status === "Recorded") return "FISCAL_DOCUMENT_RECORDED";
  if (command.status === "Conflict" || command.status === "Rejected") return "FISCAL_TERMINAL_FAILURE";
  if (command.status === "RetryPending" || command.status === "ReadbackRequired") return "FISCAL_RETRYABLE";
  if (command.status === "Unknown") return "FISCAL_UNKNOWN";
  return command.status === "Pending" ? "FISCAL_PENDING" : "FISCAL_REQUESTED";
}

function mapReceiptState(command: CentralPmsCashReceiptStatus["command"], status: ReceiptPanelStatus): string {
  if (status.kind === "idle") return "RECEIPT_NOT_REQUESTED";
  if (status.kind === "loading") return "RECEIPT_PENDING";
  if (status.kind === "unavailable" || status.kind === "error") return "RECEIPT_TEMPORARILY_UNAVAILABLE";
  if (!command) return "RECEIPT_NOT_REQUESTED";
  if (command.status === "Available") return "RECEIPT_AVAILABLE";
  if (command.status === "Voided") return "RECEIPT_VOIDED";
  if (command.status === "NotReady") return "RECEIPT_PENDING";
  if (command.status === "RetryPending" || command.status === "Unavailable") return "RECEIPT_TEMPORARILY_UNAVAILABLE";
  if (command.status === "Unsupported") return "RECEIPT_UNSUPPORTED";
  if (command.status === "Malformed") return "RECEIPT_MALFORMED";
  if (command.status === "Rejected" || command.status === "Inconsistent") return "RECEIPT_TERMINAL_FAILURE";
  return "RECEIPT_PENDING";
}

function completionLabel(completion: CashierTransactionState["completion"]): string {
  switch (completion) {
    case "TRANSACTION_COMPLETE":
      return "Transaction complete";
    case "TRANSACTION_REQUIRES_RETRY":
      return "Transaction requires retry";
    case "TRANSACTION_REQUIRES_SUPPORT":
      return "Transaction requires support";
    default:
      return "Transaction in progress";
  }
}

function friendlyState(value: string): string {
  return value.replace(/_/g, " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}

function CashierTransactionStatePanel({ state }: { state: CashierTransactionState }) {
  return (
    <section
      className={`central-pms-panel transaction-state ${state.completion === "TRANSACTION_COMPLETE" ? "confirmed" : state.completion === "TRANSACTION_REQUIRES_SUPPORT" ? "blocked" : ""}`}
      aria-label="Cashier transaction state"
      role={state.completion === "TRANSACTION_REQUIRES_SUPPORT" ? "alert" : "status"}
      data-testid="cashier-transaction-state"
    >
      <div className="central-pms-status-row">
        <h3>Cashier transaction state</h3>
        <strong>{state.headline}</strong>
      </div>
      <p>Completion uses durable local CASH_RECEIVED evidence plus authoritative Central PMS payment, fiscal, and receipt readback. Receipt preview or printing does not complete the transaction.</p>
      <dl className="central-pms-details">
        <PreviewMeta label="Cash custody" value={friendlyState(state.custody)} testId="cash-custody-state" />
        <PreviewMeta label="Terminal-cash submission" value={friendlyState(state.terminalCashSubmission)} testId="terminal-cash-submission-state" />
        <PreviewMeta label="Payment finality" value={friendlyState(state.paymentFinality)} testId="payment-finality-state" />
        <PreviewMeta label="Fiscal issuance" value={friendlyState(state.fiscalIssuance)} testId="fiscal-issuance-state" />
        <PreviewMeta label="Receipt presentation" value={friendlyState(state.receiptPresentation)} testId="receipt-presentation-state" />
        <PreviewMeta label="Exit authorization" value={friendlyState(state.exitAuthorization)} testId="exit-authorization-state" />
        <PreviewMeta label="Cashier completion" value={friendlyState(state.completion)} testId="cashier-completion-state" />
        <PreviewMeta label="Terminal cash tender ID" value={state.terminalCashTenderId} />
        <PreviewMeta label="Payment confirmation ID" value={state.paymentConfirmationId} />
        <PreviewMeta label="Sales Invoice No." value={state.fiscalDocumentNumber} />
        <PreviewMeta label="Latest update" value={state.latestUpdatedAt ? formatDateTime(state.latestUpdatedAt) : null} />
        <PreviewMeta label="Support reference" value={state.supportReference} />
      </dl>
      {state.exitAuthorization === "EXIT_AUTHORIZATION_READBACK_CONTRACT_MISSING" && (
        <p>ExitAuthorization readback is not evaluated in this desktop slice because no APT-usable Central PMS readback contract is present. No authorization is inferred locally.</p>
      )}
      {state.completion === "TRANSACTION_COMPLETE" && (
        <p>The cashier may start a new transaction after local evidence is preserved. No gate action is created.</p>
      )}
    </section>
  );
}

function CentralPmsCanonicalPaymentPanel({
  centralPmsStatus,
  onSubmitOrReadback,
}: {
  centralPmsStatus: CentralPmsPanelStatus;
  onSubmitOrReadback: () => void;
}) {
  if (centralPmsStatus.kind === "unavailable") {
    return (
      <section className="central-pms-panel unavailable" aria-label="Central PMS canonical payment">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (centralPmsStatus.kind === "loading") {
    return (
      <section className="central-pms-panel" aria-label="Central PMS canonical payment">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (centralPmsStatus.kind === "error") {
    return (
      <section className="central-pms-panel blocked" aria-label="Central PMS canonical payment" role="alert">
        <h3>Central PMS canonical payment</h3>
        <p>{centralPmsStatus.message}</p>
        <p>Cash received locally. Canonical payment not yet confirmed. Fiscal issuance not started. Exit authorization unavailable.</p>
        <p>Correlation ID: {centralPmsStatus.correlationId}</p>
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Submit / Check Central PMS
        </button>
      </section>
    );
  }

  const command = centralPmsStatus.kind === "ready" ? centralPmsStatus.status.command : null;
  const status = command?.status ?? "Pending";
  const confirmed = status === "Confirmed";
  const conflict = status === "Conflict";
  const rejected = status === "Rejected";
  const readbackOnly = status === "ReadbackRequired";
  const retry =
    status === "Pending" || status === "RetryPending" || status === "Submitting" || !command;
  const replay = command?.resultClassification === "IDEMPOTENT_REPLAY";

  return (
    <section
      className={`central-pms-panel ${confirmed ? "confirmed" : conflict || rejected ? "blocked" : ""}`}
      aria-label="Central PMS canonical payment"
      role={conflict || rejected ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Central PMS canonical payment</h3>
        <strong>{confirmed ? "Canonical payment confirmed" : conflict ? "Conflict - support review required" : rejected ? "Rejected - reconciliation required" : readbackOnly ? "Payment finality pending" : retry ? "Canonical payment not yet confirmed" : command?.statusLabel}</strong>
      </div>

      <p>Local cash custody: cash received locally.</p>
      {confirmed ? (
        <>
          <p>{replay ? "Idempotent replay confirmed the existing command; no new charge was created." : "Central PMS accepted the persisted cash-payment command."}</p>
          <dl className="central-pms-details">
            <div>
              <dt>Payment-attempt ID</dt>
              <dd>{command?.canonicalPaymentAttemptId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Payment-confirmation ID</dt>
              <dd>{command?.canonicalPaymentConfirmationId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Result classification</dt>
              <dd>{command?.resultClassification ?? "CONFIRMED"}</dd>
            </div>
            <div>
              <dt>Confirmation timestamp</dt>
              <dd>{command?.confirmedAt ? formatDateTime(command.confirmedAt) : "Unavailable"}</dd>
            </div>
            <div>
              <dt>Correlation ID</dt>
              <dd>{command?.originalCorrelationId ?? "Unavailable"}</dd>
            </div>
          </dl>
        </>
      ) : conflict ? (
        <>
          <p>Central PMS reported a semantic conflict. Supervisor or support review is required.</p>
          <p>Existing local tender reference: {command?.terminalCashTenderId ?? "Unavailable"}</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "CONFLICT"}</p>
        </>
      ) : rejected ? (
        <>
          <p>Central PMS rejected the persisted command. Local CASH_RECEIVED evidence is retained.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "REJECTED"}</p>
          <p>Support or reconciliation handling is required.</p>
        </>
      ) : (
        <>
          <p>Status: {command?.statusLabel ?? "Pending"}</p>
          <p>{command?.lastSafeErrorCode ? `Safe error code: ${command.lastSafeErrorCode}` : readbackOnly ? "Use the accepted command to check payment finality. This does not resubmit the terminal-cash command." : "Use the persisted command to submit or check Central PMS."}</p>
        </>
      )}

      <p>Fiscal issuance not started. Exit authorization unavailable.</p>
      {!confirmed && !conflict && !rejected && (
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          {readbackOnly ? "Check Payment Status" : "Submit / Check Central PMS"}
        </button>
      )}
    </section>
  );
}

function CentralPmsFiscalIssuancePanel({
  enabled,
  fiscalStatus,
  onSubmitOrReadback,
}: {
  enabled: boolean;
  fiscalStatus: FiscalPanelStatus;
  onSubmitOrReadback: () => void;
}) {
  if (!enabled) {
    return (
      <section className="central-pms-panel fiscal unavailable" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>Central PMS fiscal issuance is disabled.</p>
        <p>Cash received locally. Canonical payment confirmed. Fiscal issuance not started. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "unavailable") {
    return (
      <section className="central-pms-panel fiscal unavailable" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment remains confirmed. Fiscal issuance not completed. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "loading") {
    return (
      <section className="central-pms-panel fiscal" aria-label="Central PMS fiscal issuance">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment confirmed. Fiscal issuance pending. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (fiscalStatus.kind === "error") {
    return (
      <section className="central-pms-panel fiscal blocked" aria-label="Central PMS fiscal issuance" role="alert">
        <h3>Fiscal issuance</h3>
        <p>{fiscalStatus.message}</p>
        <p>Canonical payment remains confirmed. Fiscal issuance incomplete. Supervisor or support review is required.</p>
        <p>Correlation ID: {fiscalStatus.correlationId}</p>
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Issue / Check Fiscal Document
        </button>
      </section>
    );
  }

  const command = fiscalStatus.kind === "ready" ? fiscalStatus.status.command : null;
  const recorded = command?.status === "Recorded";
  const conflict = command?.status === "Conflict";
  const rejected = command?.status === "Rejected";
  const uncertain =
    command?.status === "ReadbackRequired" || command?.status === "RetryPending" || command?.status === "Unknown";
  const pending = !command || command.status === "Pending" || command.status === "Submitting" || uncertain;
  const replay = command?.resultClassification === "IDEMPOTENT_REPLAY";

  return (
    <section
      className={`central-pms-panel fiscal ${recorded ? "confirmed" : conflict || rejected ? "blocked" : ""}`}
      aria-label="Central PMS fiscal issuance"
      role={conflict || rejected ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Fiscal issuance</h3>
        <strong>
          {recorded
            ? "Fiscal document recorded"
            : conflict
              ? "Fiscal conflict - support review required"
              : rejected
                ? "Fiscal rejected - reconciliation required"
                : uncertain
                  ? command?.statusLabel
                  : "Fiscal issuance pending"}
        </strong>
      </div>

      {recorded ? (
        <>
          <p>{replay ? "Idempotent replay restored the existing fiscal document; no duplicate document was created." : "Central PMS recorded the fiscal workflow result."}</p>
          <dl className="central-pms-details">
            <div>
              <dt>Fiscal-issuance reference</dt>
              <dd>{command?.fiscalIssuanceReferenceId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>POS fiscal-document ID</dt>
              <dd>{command?.posFiscalDocumentId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-document number</dt>
              <dd>{command?.fiscalDocumentNumber ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-number assigned</dt>
              <dd>{command?.fiscalNumberAssignedAt ? formatDateTime(command.fiscalNumberAssignedAt) : "Unavailable"}</dd>
            </div>
            <div>
              <dt>Result classification</dt>
              <dd>{command?.resultClassification ?? "RECORDED"}</dd>
            </div>
            <div>
              <dt>Fiscal state</dt>
              <dd>{command?.fiscalIssuanceState ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Correlation ID</dt>
              <dd>{command?.fiscalCorrelationId ?? "Unavailable"}</dd>
            </div>
          </dl>
        </>
      ) : conflict ? (
        <>
          <p>Central PMS reported a fiscal conflict. Supervisor or support review is required.</p>
          <p>Fiscal command reference: {command?.localFiscalCommandId ?? "Unavailable"}</p>
          <p>Terminal cash tender: {command?.terminalCashTenderId ?? "Unavailable"}</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "CONFLICT"}</p>
        </>
      ) : rejected ? (
        <>
          <p>Central PMS rejected fiscal issuance. Canonical payment remains confirmed; fiscal issuance was not completed.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "REJECTED"}</p>
          <p>Support or reconciliation handling is required.</p>
        </>
      ) : (
        <>
          <p>{pending ? "Fiscal issuance pending." : `Fiscal status: ${command?.statusLabel}`}</p>
          <p>{command?.fiscalIssuanceReferenceId ? `Fiscal reference: ${command.fiscalIssuanceReferenceId}` : "No fiscal document recorded yet."}</p>
          <p>{command?.lastSafeErrorCode ? `Safe error code: ${command.lastSafeErrorCode}` : "Use the persisted fiscal command to issue or check status."}</p>
        </>
      )}

      <p>Receipt not rendered or printed. Exit authorization unavailable.</p>
      {!recorded && !conflict && !rejected && (
        <button className="secondary-action" type="button" onClick={onSubmitOrReadback}>
          Issue / Check Fiscal Document
        </button>
      )}
    </section>
  );
}

function CentralPmsReceiptAvailabilityPanel({
  enabled,
  previewEnabled,
  receiptStatus,
  onRetrieveOrCheck,
  onViewPreview,
}: {
  enabled: boolean;
  previewEnabled: boolean;
  receiptStatus: ReceiptPanelStatus;
  onRetrieveOrCheck: () => void;
  onViewPreview: () => void;
}) {
  if (!enabled) {
    return (
      <section className="central-pms-panel receipt unavailable" aria-label="Central PMS receipt availability">
        <h3>Receipt availability</h3>
        <p>Central PMS receipt retrieval is disabled.</p>
        <p>Cash received locally. Canonical payment confirmed. Fiscal document recorded. Receipt not retrieved. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (receiptStatus.kind === "unavailable") {
    return (
      <section className="central-pms-panel receipt unavailable" aria-label="Central PMS receipt availability">
        <h3>Receipt availability</h3>
        <p>{receiptStatus.message}</p>
        <p>Fiscal document remains recorded. Receipt retrieval not completed. Receipt not rendered or printed. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (receiptStatus.kind === "loading") {
    return (
      <section className="central-pms-panel receipt" aria-label="Central PMS receipt availability">
        <h3>Receipt availability</h3>
        <p>{receiptStatus.message}</p>
        <p>Fiscal document recorded. Receipt presentation not yet available in the terminal. Exit authorization unavailable.</p>
      </section>
    );
  }

  if (receiptStatus.kind === "error") {
    return (
      <section className="central-pms-panel receipt blocked" aria-label="Central PMS receipt availability" role="alert">
        <h3>Receipt availability</h3>
        <p>{receiptStatus.message}</p>
        <p>Fiscal document remains recorded. Receipt retrieval incomplete. Supervisor or support review is required.</p>
        <p>Correlation ID: {receiptStatus.correlationId}</p>
        <button className="secondary-action" type="button" onClick={onRetrieveOrCheck}>
          Retrieve / Check Receipt
        </button>
      </section>
    );
  }

  const command = receiptStatus.kind === "ready" ? receiptStatus.status.command : null;
  const available = command?.status === "Available";
  const voided = command?.status === "Voided";
  const notReady = command?.status === "NotReady";
  const retry = command?.status === "RetryPending" || command?.status === "Unavailable" || command?.status === "Retrieving";
  const inconsistent = command?.status === "Inconsistent";
  const rejected = command?.status === "Rejected";
  const unsupported = command?.status === "Unsupported";
  const malformed = command?.status === "Malformed";
  const terminalFailure = inconsistent || rejected || unsupported || malformed;
  const pending = !command || command.status === "Pending";

  return (
    <section
      className={`central-pms-panel receipt ${available || voided ? "confirmed" : terminalFailure ? "blocked" : ""}`}
      aria-label="Central PMS receipt availability"
      role={terminalFailure ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Receipt availability</h3>
        <strong>
          {available
            ? "Receipt presentation available"
            : voided
              ? "Receipt presentation available - fiscal document voided"
              : inconsistent
                ? "Receipt inconsistency - support review required"
                : rejected
                  ? "Receipt rejected - reconciliation required"
                  : unsupported
                    ? "Sales Invoice format is not supported"
                    : malformed
                      ? "Sales Invoice response could not be read"
                  : notReady
                    ? "Receipt presentation not ready"
                    : retry
                      ? command?.statusLabel
                      : "Receipt not yet retrieved"}
        </strong>
      </div>

      {available || voided ? (
        <>
          <p>{voided ? "Authoritative POS Server presentation is available with void posture." : "Authoritative POS Server presentation metadata is available."}</p>
          <dl className="central-pms-details">
            <div>
              <dt>Canonical payment status</dt>
              <dd>{command?.canonicalPaymentStatus ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal issuance reference</dt>
              <dd>{command?.fiscalIssuanceReferenceId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>POS fiscal-document ID</dt>
              <dd>{command?.posFiscalDocumentId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-document number</dt>
              <dd>{command?.fiscalDocumentNumber ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Fiscal-document status</dt>
              <dd>{command?.fiscalDocumentStatus ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Receipt availability</dt>
              <dd>{command?.receiptAvailabilityState ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Presentation version</dt>
              <dd>{command?.presentationVersion ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Template version</dt>
              <dd>{command?.templateVersion ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Content type</dt>
              <dd>{command?.contentType ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Retrieved timestamp</dt>
              <dd>{command?.retrievedAt ? formatDateTime(command.retrievedAt) : "Unavailable"}</dd>
            </div>
            <div>
              <dt>Payload hash</dt>
              <dd>{command?.authoritativePayloadHash ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Semantic hash</dt>
              <dd>{command?.semanticRequestHash ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Semantic hash version</dt>
              <dd>{command?.semanticRequestHashVersion ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Semantic hash status</dt>
              <dd>{command?.semanticRequestHashStatus ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Correlation ID</dt>
              <dd>{command?.retrievalCorrelationId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Central PMS correlation ID</dt>
              <dd>{command?.lastCentralPmsCorrelationId ?? "Unavailable"}</dd>
            </div>
            <div>
              <dt>Last Central PMS update</dt>
              <dd>{command?.lastUpdatedFromCentralPms ? formatDateTime(command.lastUpdatedFromCentralPms) : "Unavailable"}</dd>
            </div>
            {voided && (
              <>
                <div>
                  <dt>Void status</dt>
                  <dd>{command?.voidStatus ?? "Unavailable"}</dd>
                </div>
                <div>
                  <dt>Void reason</dt>
                  <dd>{command?.voidReasonCode ?? "Unavailable"}</dd>
                </div>
                <div>
                  <dt>Voided timestamp</dt>
                  <dd>{command?.voidedAt ? formatDateTime(command.voidedAt) : "Unavailable"}</dd>
                </div>
              </>
            )}
          </dl>
        </>
      ) : inconsistent ? (
        <>
          <p>Central PMS reported conflicting terminal-cash, fiscal, or POS-document references. Supervisor or support review is required.</p>
          <p>Receipt command reference: {command?.localReceiptRetrievalId ?? "Unavailable"}</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "INCONSISTENT"}</p>
        </>
      ) : rejected ? (
        <>
          <p>Central PMS rejected receipt retrieval. Canonical payment and fiscal recording are preserved; receipt retrieval did not complete.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "REJECTED"}</p>
          <p>Support or reconciliation handling is required.</p>
        </>
      ) : unsupported ? (
        <>
          <p>Central PMS reported a Sales Invoice presentation format this terminal does not support. No local fiscal receipt was created.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "POS_SERVER_RECEIPT_PRESENTATION_UNSUPPORTED"}</p>
          <p>Support or application upgrade is required.</p>
        </>
      ) : malformed ? (
        <>
          <p>Central PMS returned a receipt presentation response that could not be safely read. No local fiscal receipt was created.</p>
          <p>Safe error code: {command?.lastSafeErrorCode ?? "POS_SERVER_RECEIPT_PRESENTATION_MALFORMED"}</p>
          <p>Support review is required.</p>
        </>
      ) : (
        <>
          <p>{notReady ? "Receipt presentation not ready." : pending ? "Receipt not yet retrieved." : `Receipt status: ${command?.statusLabel}`}</p>
          <p>{command?.lastSafeErrorCode ? `Safe error code: ${command.lastSafeErrorCode}` : "Use the persisted receipt command to retrieve or check availability."}</p>
          {command?.lastRetryable !== null && command?.lastRetryable !== undefined && (
            <p>{command.lastRetryable ? "Retry receipt retrieval when eligible." : "Automatic retry is stopped for this receipt state."}</p>
          )}
        </>
      )}

      <p>Receipt not rendered or printed. Exit authorization unavailable.</p>
      {previewEnabled && (available || voided) && (
        <button className="secondary-action" type="button" onClick={onViewPreview}>
          View Receipt Preview
        </button>
      )}
      {!available && !voided && !terminalFailure && (
        <button className="secondary-action" type="button" onClick={onRetrieveOrCheck}>
          Retrieve / Check Receipt
        </button>
      )}
    </section>
  );
}

function ReceiptPrintPanel({
  enabled,
  configuredPrinterName,
  configuredPaperWidthMm,
  receiptAvailable,
  status,
  onPrint,
}: {
  enabled: boolean;
  configuredPrinterName: string | null;
  configuredPaperWidthMm: 57 | 58 | 80;
  receiptAvailable: boolean;
  status: ReceiptPrintStatus;
  onPrint: () => void;
}) {
  if (!enabled && !receiptAvailable) {
    return null;
  }

  if (!enabled) {
    return (
      <section className="central-pms-panel receipt-print unavailable" aria-label="Sales Invoice printing">
        <h3>Sales Invoice printing</h3>
        <p>Thermal printing is disabled.</p>
        <p>Preview remains read-only. No print job was created.</p>
      </section>
    );
  }

  if (!receiptAvailable) {
    return (
      <section className="central-pms-panel receipt-print unavailable" aria-label="Sales Invoice printing">
        <h3>Sales Invoice printing</h3>
        <p>Ready only after the authoritative Sales Invoice presentation is available.</p>
        <p>No fallback receipt will be printed.</p>
      </section>
    );
  }

  const jobs = status.kind === "ready" ? status.status.jobs : [];
  const latestJob = jobs.at(-1);
  const originalAccepted = jobs.some((job) => job.status === "SubmittedToSpooler" || job.status === "Completed" || job.status === "UnknownAfterRestart");
  const pending = latestJob?.status === "Requested" || latestJob?.status === "Preparing" || latestJob?.status === "SubmissionPending";
  const retryable = latestJob?.retryable === true;
  const buttonText = originalAccepted ? "Reprint Sales Invoice" : "Print Sales Invoice";
  const canPrint =
    status.kind !== "loading"
    && !pending
    && status.kind !== "error"
    && Boolean(configuredPrinterName)
    && (latestJob?.status !== "UnknownAfterRestart" || false);

  return (
    <section
      className={`central-pms-panel receipt-print ${latestJob?.status === "SubmittedToSpooler" ? "confirmed" : latestJob?.status?.includes("Failed") || latestJob?.status === "PrinterUnavailable" ? "blocked" : ""}`}
      aria-label="Sales Invoice printing"
      role={latestJob?.status?.includes("Failed") || latestJob?.status === "PrinterUnavailable" ? "alert" : "status"}
    >
      <div className="central-pms-status-row">
        <h3>Sales Invoice printing</h3>
        <strong>{latestJob?.statusLabel ?? (status.kind === "loading" ? status.message : "Ready to print")}</strong>
      </div>

      <dl className="central-pms-details">
        <div>
          <dt>Configured printer</dt>
          <dd>{configuredPrinterName ?? "Not configured"}</dd>
        </div>
        <div>
          <dt>Paper width</dt>
          <dd>{configuredPaperWidthMm} mm</dd>
        </div>
        <div>
          <dt>Last print attempt</dt>
          <dd>{latestJob?.requestedAt ? formatDateTime(latestJob.requestedAt) : "None"}</dd>
        </div>
        <div>
          <dt>Print classification</dt>
          <dd>{latestJob?.classificationLabel ?? (originalAccepted ? "Reprint" : "Original")}</dd>
        </div>
        <div>
          <dt>Copy sequence</dt>
          <dd>{latestJob?.copySequence ? String(latestJob.copySequence) : "Not printed"}</dd>
        </div>
        <div>
          <dt>Support reference</dt>
          <dd>{latestJob?.correlationId ?? "Unavailable"}</dd>
        </div>
      </dl>

      {status.kind === "error" && <p className="cash-error">{status.message}</p>}
      {latestJob?.failureClassification && (
        <p>Safe printer failure classification: {latestJob.failureClassification}</p>
      )}
      {latestJob?.status === "UnknownAfterRestart" && (
        <p>Print result requires confirmation. The terminal will not silently resubmit this job after restart.</p>
      )}
      {retryable && <p>Retry is available after confirming the printer is ready.</p>}
      {status.kind === "ready" && status.lastSubmit && (
        <p>{status.lastSubmit.safeMessage}</p>
      )}
      {status.kind === "ready" && status.lastSubmit && (
        <article className="receipt-print-output" aria-label="Prepared print output">
          {status.lastSubmit.printDocument.lines.map((line, index) => (
            <p key={`${line}-${index}`}>{line}</p>
          ))}
        </article>
      )}
      <p>Printing uses the stored authoritative Sales Invoice presentation. It does not retrieve another receipt or change payment, fiscal, ExitAuthorization, HikCentral, gate, or cash-drawer state.</p>

      <button className="secondary-action" type="button" disabled={!canPrint} onClick={onPrint}>
        {retryable ? "Retry Sales Invoice Print" : buttonText}
      </button>
    </section>
  );
}

function SalesInvoicePrintHistoryPanel({
  receiptAvailable,
  status,
  open,
  filter,
  onOpen,
  onClose,
  onFilter,
  onDetail,
}: {
  receiptAvailable: boolean;
  status: ReceiptPrintHistoryStatus;
  open: boolean;
  filter: "All" | "Original" | "Reprint" | "Submitted" | "Failed" | "Requires confirmation";
  onOpen: () => void;
  onClose: () => void;
  onFilter: (filter: "All" | "Original" | "Reprint" | "Submitted" | "Failed" | "Requires confirmation") => void;
  onDetail: (printJobId: string) => void;
}) {
  if (!receiptAvailable && status.kind === "idle") {
    return null;
  }

  const history = status.kind === "ready" ? status.history : null;
  const summary = history?.summary;
  const jobs = history?.jobs ?? [];
  const filteredJobs = jobs.filter((job) => {
    if (filter === "All") return true;
    if (filter === "Original" || filter === "Reprint") return job.classification === filter;
    if (filter === "Requires confirmation") return job.status === "UnknownAfterRestart";
    if (filter === "Submitted") return job.status === "SubmittedToSpooler" || job.status === "Completed";
    return job.status === "PrinterUnavailable" || job.status === "PreparationFailed" || job.status === "SpoolerSubmissionFailed";
  });
  const latestAttempt = summary?.latestAttemptAt ? formatDateTime(summary.latestAttemptAt) : "None";

  return (
    <section className="central-pms-panel receipt-print-history" aria-label="Sales Invoice Print History">
      <div className="central-pms-status-row">
        <h3>Sales Invoice Print History</h3>
        <strong>{status.kind === "loading" ? status.message : summary?.latestStatus ?? "No print attempts recorded"}</strong>
      </div>

      {status.kind === "error" && <p className="cash-error">{status.message}</p>}

      <dl className="central-pms-details">
        <div>
          <dt>Original status</dt>
          <dd>{summary?.originalStatus ?? "No print attempts recorded"}</dd>
        </div>
        <div>
          <dt>Reprint count</dt>
          <dd>{summary ? String(summary.reprintCount) : "0"}</dd>
        </div>
        <div>
          <dt>Latest copy sequence</dt>
          <dd>{summary?.latestCopySequence ? String(summary.latestCopySequence) : "None"}</dd>
        </div>
        <div>
          <dt>Printer</dt>
          <dd>{summary?.latestPrinterName ?? "No printer evidence"}</dd>
        </div>
        <div>
          <dt>Paper width</dt>
          <dd>{summary?.latestPaperWidthMm ? `${summary.latestPaperWidthMm} mm` : "No width evidence"}</dd>
        </div>
        <div>
          <dt>Latest attempt</dt>
          <dd>{latestAttempt}</dd>
        </div>
      </dl>

      {summary?.requiresConfirmation && <p>Print result requires confirmation. This read-only history view will not resolve or resubmit it.</p>}
      {history?.indicators.some((indicator) => indicator.severity === "attention") && (
        <div className="status-notice warning" role="status">
          <strong>Local reconciliation attention</strong>
          {history.indicators.filter((indicator) => indicator.severity === "attention").map((indicator) => (
            <p key={indicator.code}>{indicator.label}: {indicator.message}</p>
          ))}
        </div>
      )}
      {history?.jobs.length === 0 && <p>No print attempts recorded.</p>}
      <p>Local history shows printer-submission evidence only. It does not overclaim physical paper output.</p>

      {!open ? (
        <button className="secondary-action" type="button" onClick={onOpen}>
          Open Print History
        </button>
      ) : (
        <div className="print-history-expanded">
          <div className="receipt-history-toolbar" role="group" aria-label="Print history filters">
            {(["All", "Original", "Reprint", "Submitted", "Failed", "Requires confirmation"] as const).map((value) => (
              <button
                key={value}
                type="button"
                className={filter === value ? "secondary-action selected" : "secondary-action"}
                aria-pressed={filter === value}
                onClick={() => onFilter(value)}
              >
                {value}
              </button>
            ))}
            <button className="secondary-action" type="button" onClick={onClose}>
              Close Print History
            </button>
          </div>

          <div className="receipt-history-list" aria-label="Print attempts">
            {filteredJobs.length === 0 ? (
              <p>No print attempts match this filter.</p>
            ) : (
              filteredJobs.map((job) => (
                <button
                  key={job.printJobId}
                  type="button"
                  className="receipt-history-row"
                  onClick={() => onDetail(job.printJobId)}
                >
                  <span>{job.classificationLabel}</span>
                  <span>Copy sequence {job.copySequence}</span>
                  <span>{job.statusLabel}</span>
                  <span>{job.configuredPrinterName}</span>
                  <span>{job.paperWidthMm} mm</span>
                  <span>{formatDateTime(job.requestedAt)}</span>
                  <span>{job.status === "UnknownAfterRestart" ? "Attention" : "Support reference available"}</span>
                </button>
              ))
            )}
          </div>

          {status.kind === "ready" && status.detail && (
            <article className="receipt-history-detail" aria-label="Print attempt detail">
              <h4>Print Attempt Detail</h4>
              <p>{status.detail.statusExplanation}</p>
              <dl className="central-pms-details">
                <PreviewMeta label="Support reference" value={status.detail.job.correlationId} />
                <PreviewMeta label="Print job ID" value={status.detail.job.printJobId} />
                <PreviewMeta label="Terminal cash tender ID" value={status.detail.job.terminalCashTenderId} />
                <PreviewMeta label="Fiscal document ID" value={status.detail.job.posFiscalDocumentId} />
                <PreviewMeta label="Sales Invoice No." value={status.detail.job.fiscalDocumentNumber} />
                <PreviewMeta label="Classification" value={status.detail.job.classificationLabel} />
                <PreviewMeta label="Copy sequence" value={String(status.detail.job.copySequence)} />
                <PreviewMeta label="Printer" value={status.detail.job.configuredPrinterName} />
                <PreviewMeta label="Paper width" value={`${status.detail.job.paperWidthMm} mm`} />
                <PreviewMeta label="Presentation version" value={status.detail.job.presentationVersion} />
                <PreviewMeta label="Template version" value={status.detail.job.templateVersion} />
                <PreviewMeta label="Payload hash evidence" value={status.detail.shortAuthoritativePayloadHash} />
                <PreviewMeta label="Semantic hash evidence" value={status.detail.shortSemanticRequestHash} />
                <PreviewMeta label="Windows spooler job ID" value={status.detail.job.windowsSpoolerJobId} />
                <PreviewMeta label="Requested at" value={formatDateTime(status.detail.job.requestedAt)} />
                <PreviewMeta label="Submission started at" value={status.detail.job.submissionStartedAt ? formatDateTime(status.detail.job.submissionStartedAt) : null} />
                <PreviewMeta label="Submitted to printer at" value={status.detail.job.submittedToSpoolerAt ? formatDateTime(status.detail.job.submittedToSpoolerAt) : null} />
                <PreviewMeta label="Completed at" value={status.detail.job.completedAt ? formatDateTime(status.detail.job.completedAt) : null} />
                <PreviewMeta label="Failed at" value={status.detail.job.failedAt ? formatDateTime(status.detail.job.failedAt) : null} />
                <PreviewMeta label="Failure classification" value={status.detail.job.failureClassification} />
                <PreviewMeta label="Retryable" value={status.detail.job.retryable ? "Yes" : "No"} />
              </dl>
              {status.detail.indicators.map((indicator) => (
                <p key={indicator.code}>{indicator.label}: {indicator.message}</p>
              ))}
            </article>
          )}
        </div>
      )}
    </section>
  );
}

function ReceiptPreviewSurface({
  status,
  configuredPaperWidthMm,
  paperWidthWarning,
  onClose,
}: {
  status: ReceiptPreviewStatus;
  configuredPaperWidthMm: 57 | 58 | 80;
  paperWidthWarning: string | null;
  onClose: () => void;
}) {
  if (status.kind === "idle") {
    return null;
  }

  const rawPreview = status.kind === "ready" ? status.preview.preview : null;
  const placeholderBlocked = rawPreview?.hasPlaceholders === true;
  const preview = placeholderBlocked ? null : rawPreview;
  const blockedDetail = status.kind === "blocked" ? status.detail : undefined;
  const profile = status.kind === "ready" ? status.preview.paperProfile : blockedDetail?.paperProfile;
  const command = status.kind === "ready" ? status.preview.command : blockedDetail?.command;
  const width = profile?.paperWidthMm ?? configuredPaperWidthMm;
  const warning = status.kind === "ready" ? status.preview.paperWidthWarning : blockedDetail?.paperWidthWarning ?? paperWidthWarning;
  const blockedCode = placeholderBlocked ? "receipt_preview_incomplete_authoritative_payload" : status.kind === "blocked" ? status.code : "";

  return (
    <section className="receipt-preview-overlay" aria-label="Receipt preview">
      <div className="receipt-preview-shell" role={status.kind === "blocked" || placeholderBlocked ? "alert" : "dialog"} aria-modal="false">
        <div className="receipt-preview-header">
          <div>
            <p className="eyebrow">Receipt preview</p>
            <h3>Read-only authoritative presentation</h3>
          </div>
          <button className="secondary-action" type="button" onClick={onClose}>
            Close preview
          </button>
        </div>

        <div className="receipt-preview-primary-meta">
          <span>Sales Invoice No. {preview?.fiscalDocumentNumber ?? command?.fiscalDocumentNumber ?? "Unavailable"}</span>
          <span>Status: {preview?.fiscalDocumentStatus ?? command?.fiscalDocumentStatus ?? "Unavailable"}</span>
          <span>Paper width: {width} mm</span>
          {preview && <span>Configuration completeness: {preview.configurationCompleteness}</span>}
          <span>Not printed</span>
          <span>Exit authorization unavailable</span>
        </div>
        {warning && <p className="receipt-preview-warning">{warning}</p>}

        {status.kind === "loading" && <p>{status.message}</p>}

        {(status.kind === "blocked" || placeholderBlocked) && (
          <div className="receipt-preview-blocked">
            <strong>{blockedTitle(blockedCode)}</strong>
            <p>{placeholderBlocked ? "Receipt presentation is missing required authoritative display fields. No local placeholders were rendered." : status.kind === "blocked" ? status.message : ""}</p>
            <p>Support review or application upgrade is required. No receipt body was rendered.</p>
            {command && (
              <dl className="receipt-preview-metadata">
                <PreviewMeta label="Sales Invoice No." value={command.fiscalDocumentNumber} />
                <PreviewMeta label="Fiscal-document status" value={command.fiscalDocumentStatus} />
                <PreviewMeta label="Receipt availability" value={command.receiptAvailabilityState} />
                <PreviewMeta label="Presentation version" value={command.presentationVersion} />
                <PreviewMeta label="Template version" value={command.templateVersion} />
                <PreviewMeta label="Content type" value={command.contentType} />
                <PreviewMeta label="Payload hash" value={command.authoritativePayloadHash} />
                <PreviewMeta label="Semantic hash" value={command.semanticRequestHash} />
                <PreviewMeta label="Semantic hash version" value={command.semanticRequestHashVersion} />
                <PreviewMeta label="Semantic hash status" value={command.semanticRequestHashStatus} />
                <PreviewMeta label="Correlation ID" value={command.retrievalCorrelationId} />
                <PreviewMeta label="Central PMS correlation ID" value={command.lastCentralPmsCorrelationId} />
              </dl>
            )}
          </div>
        )}

        {preview && (
          <>
            {preview.voided && (
              <div className="receipt-preview-voided">
                <strong>VOIDED FISCAL DOCUMENT</strong>
                <p>Void reason: {preview.voidReasonCode ?? "Unavailable"}</p>
                <p>Voided timestamp: {preview.voidedAt ? formatDateTime(preview.voidedAt) : "Unavailable"}</p>
                <p>Not valid as an active receipt. Not printed. Exit authorization unavailable.</p>
              </div>
            )}

            <details className="receipt-preview-technical">
              <summary>Receipt technical details</summary>
              <dl className="receipt-preview-metadata">
                <PreviewMeta label="Receipt availability" value={preview.receiptAvailabilityState} />
                <PreviewMeta label="Configuration completeness" value={preview.configurationCompleteness} />
                <PreviewMeta label="Presentation version" value={preview.presentationVersion} />
                <PreviewMeta label="Template version" value={preview.templateVersion} />
                <PreviewMeta label="Content type" value={preview.contentType} />
                <PreviewMeta label="Payload hash" value={preview.authoritativePayloadHash} />
                <PreviewMeta label="Semantic hash" value={preview.semanticRequestHash} />
                <PreviewMeta label="Semantic hash version" value={preview.semanticRequestHashVersion} />
                <PreviewMeta label="Semantic hash status" value={preview.semanticRequestHashStatus} />
                <PreviewMeta label="Retrieval timestamp" value={preview.retrievedAt ? formatDateTime(preview.retrievedAt) : "Unavailable"} />
                <PreviewMeta label="Correlation ID" value={preview.retrievalCorrelationId} />
                <PreviewMeta label="Central PMS correlation ID" value={preview.centralPmsCorrelationId} />
                {preview.voided && <PreviewMeta label="Void status" value={preview.voidStatus} />}
              </dl>
            </details>

            <article className={`receipt-paper ${preview.paperProfile.id}`} aria-label="Read-only receipt body">
              {preview.sections.map((section) => <ReceiptPaperSection key={section.title} section={section} />)}
            </article>
          </>
        )}
      </div>
    </section>
  );
}

type ReceiptPreviewPaperField = {
  key?: string;
  label: string;
  value: string;
  isPlaceholder?: boolean;
};

function ReceiptPaperSection({
  section,
}: {
  section: {
    title: string;
    fields: ReceiptPreviewPaperField[];
    rows: Array<{ fields: ReceiptPreviewPaperField[] }>;
  };
}) {
  if (section.title === "Sales Invoice Title") {
    return (
      <section className="receipt-paper-title">
        <h4>{section.fields[0]?.value ?? "SALES INVOICE"}</h4>
      </section>
    );
  }

  if (section.title === "Registered business and statutory header") {
    return (
      <section className="receipt-paper-header">
        {section.fields.slice(0, 2).map((field, index) => (
          <p key={`${field.key ?? field.value}-${index}`} className={`${index === 0 ? "receipt-paper-merchant" : ""} ${field.isPlaceholder ? "receipt-placeholder" : ""}`.trim()}>
            {field.value}
          </p>
        ))}
        <ReceiptPreviewFields fields={section.fields.slice(2)} />
      </section>
    );
  }

  if (section.title === "Customer-service footer") {
    return (
      <section className="receipt-paper-footer">
        {section.fields.map((field, index) => (
          <p key={`${field.key ?? field.value}-${index}`} className={field.isPlaceholder ? "receipt-placeholder" : undefined}>
            {field.value}
          </p>
        ))}
      </section>
    );
  }

  if (section.title === "ITEMS") {
    return (
      <section className="receipt-paper-section receipt-paper-lines">
        <h4>ITEMS</h4>
        {section.rows.map((row, index) => (
          <ReceiptLineItem fields={row.fields} key={`${section.title}-${index}`} />
        ))}
      </section>
    );
  }

  if (section.title === "SUBTOTAL" || section.title === "TOTAL PAID AND CHANGE") {
    return (
      <section className="receipt-paper-section receipt-paper-totals">
        <h4>{section.title}</h4>
        {section.fields.length > 0 && <ReceiptPreviewFields fields={section.fields} />}
        {section.rows.map((row, index) => <ReceiptPreviewFields fields={row.fields} key={`${section.title}-${index}`} />)}
      </section>
    );
  }

  if (section.title === "PAYMENT DETAILS") {
    return (
      <section className="receipt-paper-section receipt-paper-payment">
        <h4>PAYMENT DETAILS</h4>
        {section.rows.map((row, index) => (
          <ReceiptPreviewFields fields={row.fields} key={`${section.title}-${index}`} />
        ))}
        {section.fields.length > 0 && <ReceiptPreviewFields fields={section.fields} />}
      </section>
    );
  }

  return (
    <section className="receipt-paper-section">
      <h4>{section.title}</h4>
      {section.fields.length > 0 && <ReceiptPreviewFields fields={section.fields} />}
      {section.rows.map((row, index) => (
        <div className="receipt-paper-row" key={`${section.title}-${index}`}>
          <ReceiptPreviewFields fields={row.fields} />
        </div>
      ))}
    </section>
  );
}

function ReceiptLineItem({ fields }: { fields: ReceiptPreviewPaperField[] }) {
  const description = fields.find((field) => field.key === "description" || field.label === "Description");
  const amount = fields.find((field) => field.key === "amount" || field.label === "Amount");
  const supportingFields = fields.filter((field) => field !== description && field !== amount);

  return (
    <div className="receipt-line-item">
      <div className="receipt-line-main">
        <span className={description?.isPlaceholder ? "receipt-placeholder" : undefined}>{description?.value ?? "Line item"}</span>
        {amount && <strong className={amount.isPlaceholder ? "receipt-placeholder" : undefined}>{amount.value}</strong>}
      </div>
      {supportingFields.length > 0 && <ReceiptPreviewFields fields={supportingFields} />}
    </div>
  );
}

function ReceiptPreviewFields({ fields }: { fields: ReceiptPreviewPaperField[] }) {
  return (
    <dl className="receipt-paper-fields">
      {fields.map((field, index) => (
        <div key={`${field.key ?? field.label}-${index}`} className={field.isPlaceholder ? "receipt-placeholder-row" : undefined}>
          <dt>{field.label}</dt>
          <dd className={field.isPlaceholder ? "receipt-placeholder" : undefined}>{field.value}</dd>
        </div>
      ))}
    </dl>
  );
}

function PreviewMeta({ label, value, testId }: { label: string; value: string | null | undefined; testId?: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd data-testid={testId}>{value || "Unavailable"}</dd>
    </div>
  );
}

function blockedTitle(code: string): string {
  if (code === "receipt_preview_integrity_failed") {
    return "Receipt payload integrity check failed";
  }

  if (code === "receipt_preview_decode_failed") {
    return "Receipt presentation could not be safely decoded";
  }

  if (code === "receipt_preview_unsupported_version") {
    return "Unsupported receipt presentation version";
  }

  if (code === "receipt_preview_incomplete_authoritative_payload") {
    return "Receipt presentation is incomplete";
  }

  return "Receipt preview unavailable";
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat("en-PH", {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}
