# ExitPass APT Statutory Cash Acceptance Readiness Authorization Audit

## 1. Authorization Decision

Decision: READY_WITH_BOUNDED_FISCAL_LINKAGE_GAP

Desktop implementation may not yet enable statutory Continue to Cash or statutory CASH_RECEIVED. The merged desktop and APT payable-basis facade can enforce statutory readiness, amount acknowledgement, immediate revalidation, and local duplicate protection, but Central PMS terminal-cash fiscal issuance currently constructs POS Server fiscal payloads without statutory discount references or discount privilege details. Enabling cash before that linkage is explicit would allow a final statutory cash payment to reach fiscal issuance without the canonical statutory facts required for Sales Invoice fiscalization.

This decision does not authorize controlled UAT, cash controlled UAT, or production rollout.

## 2. Scope and Authority Boundaries

Audit repository: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Read-only evidence repositories:

- `D:\SourceCodes\ExitPass-APT`
- `D:\SourceCodes\ExitPass-Discounts`
- `D:\SourceCodes\ExitPass-PoSServer`
- `D:\SourceCodes\exitpassdb_v1.2`

Authority boundaries remain intact in the inspected implementation:

- Operator Console approves or rejects statutory entitlement.
- Central PMS owns the statutory decision, applied payable basis, applied tariff snapshot, final statutory amount, VAT facts, payment finality, and fiscal orchestration.
- POS Server owns Sales Invoice fiscalization, numbering, persistence, and rendering.
- APT owns only cashier interaction, local cash custody, denomination entry, durable local recovery state, and local support evidence.
- APT does not calculate discounts or VAT, infer APPLIED state, call HikCentral, call WebPay routes, issue ExitAuthorization, or control gates.

Evidence:

- `src/AssistedPaymentTerminal.App/src/api/centralPmsClient.ts` calls `/v1/statutory-discounts/decisions`, `/v1/terminal-cash-payments/payable-basis/resolve`, and `/v1/terminal-cash-payments/payable-basis/revalidate`.
- `src/AssistedPaymentTerminal.App/src/App.tsx` blocks active statutory workflows from the cash boundary with `cashBoundaryReady = centralReady && !statutoryWorkflowActive`.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/AptPayableBasisReadinessService.cs` consumes `IStatutoryDiscountDecisionFacadeService.GetAsync` and maps statutory readiness.
- `src/ExitPass.PosServer.Runtime/FiscalDocuments/FiscalDocumentCreationService.cs` rejects unapproved statutory discount references and accepts reference-only discount privilege details.

## 3. Current Merged Statutory Baseline

The desktop statutory workflow is merged and remains intentionally pre-cash:

- `StatutoryDiscountPanel.tsx` submits safe facts and displays review/application states.
- `centralPmsTypes.ts` models `StatutoryDiscountDecisionSubmitRequest`, `StatutoryDiscountDecisionResponse`, `StatutoryDiscountReadiness`, and `StatutoryDiscountWorkflowState`.
- `localJournalBridge.ts` and `TerminalCashPayableBasisState.cs` persist `statutoryDiscountStateJson` with safe workflow recovery facts.
- `App.tsx` restores statutory state, detects applied amount or snapshot change, reuses the existing amount-change acknowledgement flow, and keeps statutory cash disabled.
- `scripts/Invoke-AptStatutoryDiscountOrchestrationUiProof.ps1` proves the pre-cash statutory workflow and no CASH_RECEIVED posture.

The statutory-aware APT payable-basis facade is merged:

- `contracts/central-pms/apt-session-payable-basis-readiness.v1.json` defines optional `statutoryDiscountDecisionCommandId`, `statutoryDiscountReadiness`, applied-basis rule, and statutory blocker codes.
- `AptPayableBasisDtos.cs` includes `StatutoryDiscountDecisionCommandId` on resolve and revalidate requests and includes `AptStatutoryDiscountReadinessDto` on responses.
- `AptPayableBasisReadinessService.cs` blocks pending, rejected, retryable, terminal, inconsistent, and incomplete statutory states; when APPLIED facts are complete, it returns the applied tariff snapshot and final amount.
- `AptPayableBasisReadinessStatutoryFacadeTests.cs` proves complete APPLIED facts return the applied snapshot/final amount and incomplete facts remain blocked.

## 4. Final Cash-Enable Rule

Current desktop rule:

- `App.tsx` computes `centralReady = Boolean(displayedBasis?.readyForCashAcceptance) && !tariffExpired && lookupState.status !== "amount_changed"`.
- `App.tsx` computes `cashBoundaryReady = centralReady && !statutoryWorkflowActive`.
- `handleContinueToCash` exits before revalidation unless `cashBoundaryReady` and local prerequisites are true.
- `PreCashBoundaryPanel` receives `centralReady={cashBoundaryReady}`.
- `CashCapturePanel` is only mounted after `cashEntryRequested` and receives `cashAcceptanceReady={cashBoundaryReady && localPrerequisitesReady}`.

Result: statutory Continue to Cash is deliberately disabled even when Central PMS returns `readyForCashAcceptance=true` for an APPLIED statutory basis.

Smallest desktop change after the backend fiscal-linkage prerequisite: replace the blanket `!statutoryWorkflowActive` blocker with a statutory-specific gate that requires:

- statutory state is APPLIED;
- `payableBasisReady=true`;
- statutory-aware facade response has `readyForCashAcceptance=true`;
- no `amount_changed` state is pending;
- amount/snapshot acknowledgement has been persisted;
- immediate revalidation returns `PASSED_UNCHANGED` for the applied snapshot and final amount;
- local prerequisites pass.

Classification: READY_WITH_BOUNDED_EXTENSION after fiscal linkage is complete.

## 5. Immediate Revalidation Analysis

Current desktop revalidation is structurally aligned:

- `centralPmsClient.ts` sends `parkingSessionId`, `tariffSnapshotId`, expected amount, expected currency, Site/Site Group, terminal context, and `statutoryDiscountDecisionCommandId` from `displayedBasis.statutoryDiscountReadiness`.
- `App.tsx` calls `client.revalidatePayableBasis` inside `preCashRevalidate` before opening the cash panel and again after statutory amount acknowledgement.
- `App.tsx` treats `PASSED_UNCHANGED` with `readyForCashAcceptance=true` as pass, treats `AMOUNT_CHANGED` as a blocked acknowledgement state, and otherwise uses Central PMS blockers.

Central PMS revalidation is statutory-aware:

- `AptPayableBasisRevalidateRequest` includes `StatutoryDiscountDecisionCommandId`.
- `AptPayableBasisReadinessService.cs` returns `STATUTORY_DISCOUNT_BLOCKED` when statutory readiness is applicable but not ready.
- `AptPayableBasisReadinessService.cs` returns `AMOUNT_CHANGED` when current authoritative amount or tariff snapshot differs from the displayed basis.
- `AptPayableBasisReadinessStatutoryFacadeTests.cs` covers APPLIED complete, missing snapshot, missing final amount, missing currency, mismatched parking session, and amount-changed outcomes.

Result: immediate revalidation can enforce statutory readiness without local statutory inference.

Classification: READY_UNCHANGED.

## 6. Applied-Basis Custody Evidence

Current durable evidence available before CASH_RECEIVED:

- `TerminalCashPayableBasisState.cs` persists `TariffSnapshotId`, `AuthoritativeAmountMinorUnits`, `Currency`, `ReadyForCashAcceptance`, `RevalidationOutcome`, `CashierAcknowledgementRequired`, `AmountChanged`, `PriorDisplayedAmountMinorUnits`, `CentralPmsCorrelationId`, and `StatutoryDiscountStateJson`.
- `centralPmsTypes.ts` statutory workflow state carries decision command ID, application command ID, original/applied tariff snapshots, original/final amounts, VAT-exclusive amount, VAT amount, VAT treatment, statutory-discount amount, currency, readiness status/action, retryability, recovery posture, correlation, and `amountAcknowledged`.
- `App.tsx` reconstructs restored payable-basis statutory readiness from `statutoryDiscountStateJson`.

Current local tender evidence at CASH_RECEIVED:

- `CashCapturePanel.tsx` starts a tender with `authoritativeBasis.tariffSnapshotId`, `authoritativeBasis.authoritativeAmountMinorUnits / 100`, and currency after revalidation passes.
- `CashJournalService.cs` persists `CashTender.TariffSnapshotId`, `CashTender.AmountDue`, currency, tender ID, and CASH_RECEIVED event/denominations.
- `StartTenderPayload` and `RecordCashReceivedPayload` do not include explicit statutory decision/application IDs.

Result: local custody can be bound to the applied snapshot and final amount. For support/reconciliation, a bounded desktop implementation should persist or link the statutory decision/application identity at the cash-tender boundary rather than relying only on the latest payable-basis JSON.

Classification: DESKTOP_PERSISTENCE_CHANGE_REQUIRED.

## 7. Terminal-Cash Submission Analysis

Current desktop terminal-cash submission behavior:

- `TerminalCashPaymentPayloadFactory.cs` creates `TerminalCashPaymentRequest` from the tender and CASH_RECEIVED event.
- The request includes `TerminalCashTenderId`, `ParkingSessionId`, `TariffSnapshotId`, currency, amount due, amount tendered, change due, custody session, cashier, terminal, Site, Site Group, POS Server, cash timestamp, denominations, and local event reference.
- It does not include statutory decision command ID, statutory application command ID, VAT facts, discount amount, or validation reference.
- `TerminalCashPaymentSubmissionService.cs` uses a durable outbox, reuses the same tender identity/idempotency key, reads back after attempts, and does not duplicate submissions unless Central PMS readback returns not found.

Current Central PMS terminal-cash contract:

- `TerminalCashPaymentDtos.cs` has the same generic fields and no statutory identity fields.
- `TerminalCashPaymentService.cs` validates generic command fields and submits payment attempt data with tariff snapshot and amount.
- Vendor parking/payment-attempt tests prove Central PMS can persist applied statutory tariff snapshots in the payment attempt path when the effective applied snapshot is used.

Result: terminal-cash can carry the applied snapshot and final amount, but it cannot explicitly carry statutory decision/application references through the terminal-cash command. This is not the highest blocker by itself if Central PMS treats applied tariff snapshot as the payable-basis authority, but it should be resolved together with fiscal linkage so payment/fiscal evidence can be reconciled to the canonical statutory decision/application.

Classification: CENTRAL_PMS_CONTRACT_CHANGE_REQUIRED for explicit statutory identity, or READY_WITH_BOUNDED_EXTENSION if Central PMS formally documents applied snapshot as sufficient terminal-cash authority. This audit treats the fiscal linkage gap below as the gating prerequisite.

## 8. Fiscal Linkage Analysis

Current Central PMS terminal-cash fiscal issuance is not statutory-linked:

- `TerminalCashFiscalIssuanceService.cs` builds `CreateFiscalIssuanceReferenceRequest` with `TariffSnapshotId` and `PayableBasisRef` from the payment attempt.
- The POS Server fiscal payload in the same service sets `DiscountReferences: Array.Empty<CentralPmsFiscalDiscountReferenceContext>()`.
- It sets `DiscountPrivilegeDetails: Array.Empty<CentralPmsFiscalDiscountPrivilegeDetailContext>()`.
- POS Server `FiscalDocumentCreationService.cs` has explicit statutory-discount support: it validates `DiscountReferences`, rejects unapproved statutory treatment, validates `DiscountPrivilegeDetails`, rejects sensitive evidence, and persists/render reads discount privilege sections.
- POS Server persistence and presentation code includes `pos.fiscal_discount_privilege_details`, `discount_references`, and Digital Sales Invoice `discounts` sections.

Result: statutory payment cash acceptance cannot be authorized until Central PMS terminal-cash fiscal issuance maps canonical statutory application/validation facts into the POS Server fiscal request. APT must not send calculated VAT/discount facts directly to POS Server; Central PMS must own that linkage.

Classification: FISCAL_LINKAGE_CHANGE_REQUIRED.

## 9. Restart and Freshness Requirements

Current restart evidence:

- `App.tsx` restores the latest payable basis through `localJournalBridge.getLatestPayableBasisState` and reconstructs statutory readiness from `statutoryDiscountStateJson`.
- `initialLookupState` re-enters `amount_changed` when an APPLIED statutory state is restored without amount acknowledgement and the amount or applied snapshot differs.
- `CashJournalServiceTests.cs` includes payable-basis restart recovery without creating a cash tender.
- `CashCapturePanel.tsx` calls `onBeforeCashReceived` immediately inside `recordCashReceived`, so a future statutory cash-enabled path should always revalidate again after restart before recording CASH_RECEIVED.

Result: a previously successful revalidation should not be reused across application/process restart. The implementation that eventually enables statutory cash should require fresh immediate revalidation in the `recordCashReceived` path.

Classification: READY_WITH_BOUNDED_EXTENSION.

## 10. Duplicate and Idempotency Protection

Current protections:

- `StatutoryDiscountPanel.tsx` stores decision/application command IDs and idempotency keys; readback actions call GET only.
- Canonical DB has unique statutory decision business identity `(parking_session_id, entitlement_type)` and unique application command per decision.
- `CashJournalService.cs` rejects unresolved duplicate tenders per parking session.
- `CommitCashReceivedAsync` rejects transitions unless the tender is still `TenderStarted` and creates at most one payment outbox command per tender.
- `CashJournalDbContext.cs` has unique indexes for payment, fiscal, and receipt outbox commands by `TerminalCashTenderId`.
- `TerminalCashPaymentSubmissionService.cs` reuses the same outbox command/idempotency key and readback path.

Result: duplicate protection is sufficient for a bounded cash-enable implementation, provided it does not repeat statutory POST/application intent and continues to use the existing tender/outbox identity.

Classification: READY_UNCHANGED.

## 11. Failure-State Blocking Matrix

| Condition | Current evidence | Cash posture | Classification |
| --- | --- | --- | --- |
| Awaiting review | `AptPayableBasisReadinessService.cs` maps AWAITING_REVIEW to statutory blocker; `StatutoryDiscountPanel.tsx` displays Awaiting Operator Review | Blocked | READY_UNCHANGED |
| Decision rejected | `MapStatutoryBlockingReason` returns rejected blocker; panel disables application | Blocked | READY_UNCHANGED |
| Application not requested | Facade returns `DECISION_APPROVED_APPLICATION_NOT_REQUESTED`; panel exposes Submit Application | Blocked | READY_UNCHANGED |
| Application processing | Facade returns `APPLICATION_PROCESSING`; panel exposes GET/check status only | Blocked | READY_UNCHANGED |
| Required facts unavailable | Facade blocks incomplete APPLIED facts without zero substitution | Blocked | READY_UNCHANGED |
| Amount acknowledgement pending | `lookupState.status === "amount_changed"` prevents `centralReady`; `AmountChangedNotice` requires acknowledgement | Blocked | READY_UNCHANGED |
| Revalidation AMOUNT_CHANGED | `preCashRevalidate` enters amount_changed and blocks cash | Blocked | READY_UNCHANGED |
| Central PMS unavailable | client maps failure to safe error; `handleContinueToCash` blocks | Blocked | READY_UNCHANGED |
| Terminal cash unavailable | facade blocker in `terminalCashAvailability`; ready false | Blocked | READY_UNCHANGED |
| Fiscal readiness unavailable | facade fiscal readiness false | Blocked | READY_UNCHANGED |
| Local shift/custody prerequisites fail | `localPrerequisitesReady` can only restrict | Blocked | READY_UNCHANGED |
| APPLIED + ready + acknowledged + PASSED_UNCHANGED | Currently still blocked by `!statutoryWorkflowActive` | Blocked until implementation | READY_WITH_BOUNDED_EXTENSION after fiscal linkage |

## 12. Privacy and Audit Evidence

Current privacy posture is aligned:

- `StatutoryDiscountDecisionSubmitRequest` accepts masked ID reference, document type, issuing authority, expiry date, safe evidence references, requester attestation, optional safe notes, and idempotency/correlation.
- `StatutoryDiscountWorkflowState` persists safe references and canonical command IDs, not raw ID images or full statutory IDs.
- `centralPmsClient.ts` sends authenticated Central PMS requests and does not add HikCentral or WebPay route usage.
- `StatutoryDiscountPanel.tsx` displays safe references and does not display reviewer identity or notes.
- POS Server fiscal creation rejects sensitive evidence markers in discount references/details.

Result: support/reconciliation evidence can use statutory decision/application IDs, applied snapshot, final amount, and correlation references without exposing sensitive evidence.

Classification: READY_UNCHANGED with a bounded desktop persistence extension for cash-tender statutory links.

## 13. Non-Statutory Regression Posture

The eventual cash-enable change can preserve non-statutory behavior:

- Non-statutory `statutoryWorkflowActive` is false and the current cash path remains governed by `readyForCashAcceptance`, tariff freshness, local prerequisites, denomination entry, attestation, and `onBeforeCashReceived`.
- `StatutoryDiscountReadiness` is not applicable when no statutory decision command ID is supplied.
- Terminal-cash submission, fiscal readback, receipt retrieval, receipt display, printing, and print history are already keyed by tender/fiscal/receipt state and need no desktop statutory-specific change if Central PMS fiscal linkage is fixed.

Classification: READY_UNCHANGED.

## 14. Findings

### APT-STAT-CASH-001

Severity: High

Condition: Central PMS terminal-cash fiscal issuance currently sends empty POS Server discount references and discount privilege details even when the payment attempt is governed by an applied statutory tariff snapshot.

Current evidence:

- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/TerminalCashFiscalIssuanceService.cs` sets `DiscountReferences: Array.Empty<CentralPmsFiscalDiscountReferenceContext>()` and `DiscountPrivilegeDetails: Array.Empty<CentralPmsFiscalDiscountPrivilegeDetailContext>()`.
- `src/ExitPass.PosServer.Runtime/FiscalDocuments/FiscalDocumentCreationService.cs` supports and validates approved statutory discount references and discount privilege details.

Impact: CASH_RECEIVED could lead to fiscal issuance without statutory discount evidence in the Sales Invoice path.

Owner: `D:\SourceCodes\ExitPass-APT`.

Bounded correction: extend Central PMS terminal-cash fiscal issuance to resolve canonical statutory application/validation facts from the applied tariff snapshot or explicit statutory payment context, then send approved discount references and safe discount privilege details to POS Server.

Blocks implementation: Yes.

Blocks controlled UAT: Yes.

Blocks production rollout: Yes.

### APT-STAT-CASH-002

Severity: Medium

Condition: APT local cash tender and terminal-cash command persist applied tariff snapshot and final amount but do not explicitly persist statutory decision/application IDs at the tender boundary.

Current evidence:

- `CashTender` and `StartCashTenderRequest` include `ParkingSessionId`, `TariffSnapshotId`, `Currency`, `AmountDue`, and idempotency identity only.
- `TerminalCashPayableBasisState` stores `StatutoryDiscountStateJson`, but `TerminalCashPaymentRequest` has no statutory command ID fields.

Impact: support can correlate through latest payable-basis state and applied snapshot, but durable custody evidence would be stronger with direct statutory decision/application references linked to the tender.

Owner: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Bounded correction: in the later desktop cash-enable slice, link safe statutory decision/application IDs and acknowledgement/revalidation evidence to the cash-tender/custody boundary without storing raw evidence.

Blocks implementation: No, after Central PMS fiscal linkage is complete.

Blocks controlled UAT: Yes until implemented with the cash-enable slice.

Blocks production rollout: Yes until implemented with the cash-enable slice.

### APT-STAT-CASH-003

Severity: Medium

Condition: Statutory cash is still intentionally blocked by a blanket desktop condition rather than a final statutory cash-enable rule.

Current evidence:

- `App.tsx` computes `cashBoundaryReady = centralReady && !statutoryWorkflowActive`.
- `StatutoryDiscountPanel.tsx` displays `Statutory CASH_RECEIVED is not enabled in this slice.`

Impact: even a fully APPLIED, acknowledged, revalidated statutory basis cannot open the cash workflow until a bounded desktop implementation changes the rule.

Owner: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Bounded correction: after the Central PMS fiscal linkage prerequisite, implement statutory cash enablement only for APPLIED, acknowledged, immediately revalidated `PASSED_UNCHANGED`, Central PMS-ready, locally-ready transactions.

Blocks implementation: No for the backend prerequisite; yes for desktop cash enablement until changed.

Blocks controlled UAT: Yes.

Blocks production rollout: Yes.

### APT-STAT-CASH-004

Severity: Informational

Condition: Restart safety should require a fresh immediate revalidation before statutory CASH_RECEIVED even if a prior revalidation succeeded before restart.

Current evidence:

- `App.tsx` restores payable-basis evidence and amount-change state.
- `CashCapturePanel.tsx` invokes `onBeforeCashReceived` inside `recordCashReceived`.

Impact: no blocker if the later implementation keeps revalidation inside the irreversible command path.

Owner: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Bounded correction: document and test that restart never reuses old statutory revalidation as cash authorization.

Blocks implementation: No.

Blocks controlled UAT: No after tests pass.

Blocks production rollout: No after tests pass.

## 15. Required Next Task

Next bounded task: Extend Central PMS terminal-cash fiscal issuance to preserve canonical statutory discount linkage into POS Server fiscal issuance.

Owning repository: `D:\SourceCodes\ExitPass-APT`.

Required scope:

- Use the applied tariff snapshot or explicit statutory payment context to resolve the canonical statutory decision/application/validation facts.
- Ensure the terminal-cash payment/fiscal path can prove the applied statutory basis that governed cash custody.
- Populate POS Server fiscal discount references and discount privilege details from Central PMS authority.
- Do not require APT to calculate VAT, discount, or final amount.
- Preserve non-statutory terminal-cash behavior.
- Add focused Central PMS integration/contract tests and a proof that statutory terminal cash fiscal issuance includes approved statutory references and remains side-effect safe.

Why no other task should precede it: enabling the desktop cash boundary first would allow local CASH_RECEIVED and terminal-cash submission before the authoritative fiscal linkage for statutory Sales Invoice issuance is proven.

## 16. Deferred Authorization

Deferred until after the next task and a follow-on desktop implementation:

- statutory Continue to Cash;
- statutory CASH_RECEIVED;
- statutory controlled UAT;
- statutory cash controlled UAT;
- production rollout;
- WebPay changes;
- POS Server changes beyond consuming already supported reference fields;
- receipt rendering changes;
- printing changes;
- ExitAuthorization;
- gate integration.

## 17. Evidence Inventory

Desktop repository evidence:

- `src/AssistedPaymentTerminal.App/src/App.tsx` - statutory cash blocker, resolve/revalidate handling, amount-change acknowledgement, restart reconstruction, cash panel wiring.
- `src/AssistedPaymentTerminal.App/src/StatutoryDiscountPanel.tsx` - safe statutory UI and POST/GET/application workflow.
- `src/AssistedPaymentTerminal.App/src/api/centralPmsClient.ts` - APT payable-basis resolve/revalidate and statutory POST/GET routes.
- `src/AssistedPaymentTerminal.App/src/api/centralPmsTypes.ts` - statutory DTOs, readiness dimension, workflow state, revalidation outcomes.
- `src/AssistedPaymentTerminal.App/src/localJournalBridge.ts` - payable-basis and statutory JSON bridge fields.
- `src/AssistedPaymentTerminal.App/src/CashCapturePanel.tsx` - pre-CASH_RECEIVED revalidation, tender creation, attestation, CASH_RECEIVED command.
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPayableBasisState.cs` - durable payable-basis state and statutory JSON.
- `src/AssistedPaymentTerminal.LocalOperations/CashJournalService.cs` - tender creation, duplicate protection, CASH_RECEIVED transition, payment outbox creation.
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPaymentPayloadFactory.cs` - terminal-cash payload construction.
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPaymentContracts.cs` - local terminal-cash DTOs.
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPaymentSubmissionService.cs` - outbox submission/readback idempotency.
- `scripts/Invoke-AptStatutoryDiscountOrchestrationUiProof.ps1` - statutory orchestration proof.
- `scripts/Invoke-AptPayableBasisReadinessUiProof.ps1` - payable-basis proof.
- `scripts/Invoke-AptCashierTransactionCompletionUiProof.ps1` - transaction-completion proof.

Central PMS evidence:

- `contracts/central-pms/apt-session-payable-basis-readiness.v1.json` - statutory-aware APT payable-basis contract.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/TerminalCashPayments/AptPayableBasisDtos.cs` - statutory-aware resolve/revalidate DTOs.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/AptPayableBasisReadinessService.cs` - canonical statutory readback and applied-basis mapping.
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Application/AptPayableBasisReadinessStatutoryFacadeTests.cs` - statutory facade tests.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/TerminalCashPayments/TerminalCashPaymentDtos.cs` - terminal-cash payment DTO without statutory identity fields.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/TerminalCashPaymentService.cs` - terminal-cash payment validation and creation.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/TerminalCashFiscalIssuanceService.cs` - fiscal issuance payload construction with empty discount references/details.
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Api/VendorParkingResolutionApiIntegrationTests.cs` - applied statutory tariff snapshot behavior in vendor parking/payment-attempt paths.

Statutory backend and database evidence:

- `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/StatutoryDiscounts/StatutoryDiscountDecisionDtos.cs` - shared statutory POST/GET DTOs.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/StatutoryDiscounts/StatutoryDiscountDecisionFacadeService.cs` - shared statutory decision readback.
- `src/Services/CentralPms/src/ExitPass.CentralPms.Api/Endpoints/StatutoryDiscountDecisionEndpoints.cs` - shared statutory POST/GET routes.
- `D:\SourceCodes\exitpassdb_v1.2\build\generated\exitpass-full-object.generated.sql` - canonical statutory decision/application tables, applied snapshot and final amount constraints, idempotency and business identity indexes.

POS Server evidence:

- `src/ExitPass.PosServer.Runtime/FiscalDocuments/FiscalDocumentCreationService.cs` - approved discount reference and safe privilege-detail validation.
- `src/ExitPass.PosServer.Persistence.Postgres/FiscalDocuments/PostgresFiscalDocumentSql.cs` - persistence of discount references and privilege details.
- `src/ExitPass.PosServer.Runtime/FiscalDocuments/DigitalSalesInvoicePresentationAdapter.cs` - Digital Sales Invoice discount presentation section.
- `tests/ExitPass.PosServer.Api.Tests/DigitalSalesInvoiceEndpointTests.cs` - discounts section contract coverage.
- `tests/ExitPass.PosServer.Api.IntegrationTests/FiscalDocumentApiPostgresSmokeTests.cs` - persisted discount privilege smoke coverage.
