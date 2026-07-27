# ExitPass APT Statutory Discount Current Implementation Impact Analysis

## 1. Impact decision

Decision: READY_WITH_BOUNDED_CENTRAL_PMS_FACADE_GAP.

Desktop statutory implementation must not start with cash enablement until the APT-facing Central PMS payable-basis resolve/revalidate facade returns statutory-aware readiness and applied payable-basis facts. The statutory backend has the shared decision/readback and canonical application model, and the desktop has a strong non-statutory payable-basis, CASH_RECEIVED, receipt, printing, print-history, and completion baseline. The blocking gap is the APT facade: its current contract does not expose statutory decision/application readiness, applied tariff snapshot, VAT facts, discount facts, final statutory payable amount, or statutory recovery action.

First desktop implementation result: do not begin the cash-enabling desktop statutory slice yet. After the Central PMS facade is statutory-aware, desktop can extend the existing payable-basis/CASH_RECEIVED flow.

Statutory CASH_RECEIVED authorization result: NOT AUTHORIZED. The current generic CASH_RECEIVED workflow remains valid for non-statutory transactions only. An active statutory workflow must block CASH_RECEIVED until Central PMS reports statutory payable-basis readiness, all existing readiness gates pass, local prerequisites pass, and immediate pre-cash revalidation passes.

WebPay parity posture: available as channel-reference behavior, not as a route or storage implementation to copy. APT must use the shared statutory routes and the APT payable-basis facade, not WebPay routes.

## 2. Scope and repository authority

Artifact repository: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Read-only references inspected:

- `D:\SourceCodes\ExitPass-Discounts` for statutory backend routes, DTOs, services, tests, and proof evidence.
- `D:\SourceCodes\ExitPass-APT` for the current APT-facing payable-basis readiness facade.
- `D:\SourceCodes\exitpassdb_v1.2` for canonical generated database evidence.
- `D:\SourceCodes\ExitPass` for available WebPay statutory reference behavior.

Authority rules: APT submits safe entitlement facts and owns cashier UI/local custody only. Operator Console performs human entitlement review. Central PMS owns canonical statutory decision, application, applied tariff snapshot, final amount, VAT/discount facts, readiness, correlation, and idempotency recovery. POS Server fiscalizes finalized facts. APT does not approve entitlement, calculate discounts or VAT, derive the final payable amount, call HikCentral, issue ExitAuthorization, or control gates.

Retired database repository `D:\SourceCodes\ExitPass_DBv1.2` was not used. Canonical DB evidence is only from `D:\SourceCodes\exitpassdb_v1.2\build\generated\exitpass-full-object.generated.sql`.

## 3. Current statutory backend baseline

Implemented shared routes:

- `POST /v1/statutory-discounts/decisions` in `src/Services/CentralPms/src/ExitPass.CentralPms.Api/Endpoints/StatutoryDiscountDecisionEndpoints.cs`.
- `GET /v1/statutory-discounts/decisions/{statutoryDiscountDecisionCommandId}` in the same endpoint file.
- The request includes `ApplyPayableBasis` and `OriginalTariffSnapshotId` through `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/StatutoryDiscounts/StatutoryDiscountDecisionDtos.cs`.

The shared response DTO returns command ID, request reference, parking session, Site, Site Group, entitlement type, decision status/result, statutory validation ID, original and applied tariff snapshot IDs, application command ID/status/classification, gross/net amounts, VAT-exclusive basis, VAT amount, VAT treatment, currency, retryability, recovery classification/action, safe error code, `PayableBasisReady`, `PayableBasisReadinessStatus`, `PayableBasisReadinessAction`, and correlation.

Backend tests prove the review-mediated model:

- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Api/StatutoryDiscountServiceChannelPostApprovalApplicationIntentApiIntegrationTests.cs` covers `WEBPAY` and `ASSISTED_PAYMENT_TERMINAL` source channels, `AWAITING_REVIEW` / `NOT_DECIDED`, `applyPayableBasis=true`, APPLIED application, one application command, one applied tariff snapshot, Site/Site Group, VAT-exclusive amount, VAT amount, discount amount, final amount, `PHP`, `VAT_EXCLUSIVE`, `PayableBasisReady=true`, replay idempotency, and readback parity.
- `src/Services/CentralPms/tests/ExitPass.CentralPms.ContractTests/Public/StatutoryDiscountDecisionContractTests.cs` proves the public response contains Site Group, VAT fields, payable-basis readiness fields, APPLIED status, retryability, recovery fields, and safe error code.

Canonical database evidence in `build/generated/exitpass-full-object.generated.sql`:

- `discounts.statutory_discount_decision_commands` stores canonical decisions. Comments state business identity is `statutory-discount-decision:{parkingSessionId}:{entitlementType}`, source channel is not business identity, and `AWAITING_REVIEW` / `NOT_DECIDED` are explicit pending-review states.
- `discounts.statutory_discount_payable_basis_application_commands` stores canonical application commands. Comments state identity is `statutory-discount-payable-basis-application:{statutoryDiscountDecisionCommandId}` and application is distinct from decision, payment, fiscal issuance, ExitAuthorization, and gate action. Columns/constraints include applied tariff snapshot, approved discount, approved VAT-exclusive amount, approved VAT amount, approved final payable amount, currency, source channel constrained to `OPERATOR_CONSOLE`, `WEBPAY`, `ASSISTED_PAYMENT_TERMINAL`, APPLIED state, retry/recovery classifications, and idempotency indexes.
- `operator_console.statutory_discount_service_channel_reviews` is a safe review linkage/read model. Comments prohibit raw images, Base64 evidence, raw bytes, and full statutory ID values.
- `discounts.apply_statutory_discount_payable_basis` creates one statutory-adjusted active tariff snapshot and does not create payment, provider, gate, coupon, reconciliation, or AUB records.

## 4. Current merged APT baseline

Current desktop evidence:

- `src/AssistedPaymentTerminal.App/src/App.tsx`: ticket/plate choice, resolve, authoritative payable-basis display, `readyForCashAcceptance`, local prerequisite restriction, pre-cash revalidation, amount-changed acknowledgement, and handoff to `CashCapturePanel`.
- `src/AssistedPaymentTerminal.App/src/api/centralPmsClient.ts`: calls only `POST /v1/terminal-cash-payments/payable-basis/resolve` and `POST /v1/terminal-cash-payments/payable-basis/revalidate` for payable-basis.
- `src/AssistedPaymentTerminal.App/src/api/centralPmsTypes.ts`: ticket/plate payable-basis types, readiness dimensions, revalidation outcomes, blocker codes, and optional statutory-looking placeholders.
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPayableBasisState.cs`, `CashJournalRequests.cs`, `CashJournalSnapshots.cs`, `CashJournalService.cs`: durable non-statutory payable-basis state with amount, currency, tariff snapshot, readiness dimensions, blocking reasons, revalidation outcome, amount-changed flag, and prior displayed amount.
- `src/AssistedPaymentTerminal.App/src/CashCapturePanel.tsx`: pre-cash wording, attestation, explicit `Record Cash Received`, and post-CASH_RECEIVED payment/fiscal/receipt/print/completion separation.
- `src/AssistedPaymentTerminal.Desktop/LocalJournalBridgeHandler.cs`: bridge commands for payable-basis state, CASH_RECEIVED, terminal-cash submission/readback, fiscal readback, receipt, preview, printing, and print history.
- `src/AssistedPaymentTerminal.LocalOperations/README.md`: CASH_RECEIVED is terminal-local only; receipt printing uses already retrieved authoritative presentation; no ExitAuthorization mutation, HikCentral, gate, or cash-drawer behavior.
- `scripts/Invoke-AptPayableBasisReadinessUiProof.ps1` and `scripts/Invoke-AptCashierTransactionCompletionUiProof.ps1`: prove APT routes, Central PMS-owned readiness, local prerequisites as restrictions only, pre-CASH_RECEIVED revalidation, state separation, and no direct HikCentral/WebPay/gate/cash-drawer behavior.

No current statutory desktop workflow exists. There is no desktop typed client for the shared statutory POST/GET routes, no bridge command for statutory decision/application, and no durable statutory decision/application recovery entity.

## 5. End-to-end insertion point

Statutory workflow enters after original parking-session/payable-basis resolution and before `Continue to Cash` can open or commit cash custody.

Expected sequence: resolve session and original basis; cashier submits statutory pending-review facts; Central PMS returns `AWAITING_REVIEW` / `NOT_DECIDED`; APT persists the decision command ID and reads back; Operator Console approves or rejects; approved APT submits `applyPayableBasis=true`; APT observes `APPLICATION_PROCESSING` or `APPLIED`; Central PMS returns applied tariff snapshot, final statutory amount, VAT/discount facts, and readiness; APT updates the displayed basis after explicit review when amount changed; APT runs the normal payable-basis readiness and immediate pre-CASH_RECEIVED revalidation path; CASH_RECEIVED remains blocked until statutory and non-statutory gates pass.

## 6. Component impact matrix

| Component | Current file | Current behavior | Classification | Required change | Blocks first slice | Blocks statutory CASH_RECEIVED |
| --- | --- | --- | --- | --- | --- | --- |
| App workflow orchestration | `src/AssistedPaymentTerminal.App/src/App.tsx` | Resolves, displays, revalidates, then opens cash panel. | EXTEND_EXISTING_COMPONENT | Insert statutory decision/readback/application before cash. | Yes, after facade extension | Yes |
| Session-resolution UI | `App.tsx` | Ticket/plate only. | EXTEND_EXISTING_COMPONENT | Add statutory request entry from resolved session. | Yes | Yes |
| Central PMS client | `centralPmsClient.ts` | Calls APT payable-basis resolve/revalidate only. | EXTEND_EXISTING_COMPONENT | Add shared statutory POST/GET client; no WebPay route. | Yes | Yes |
| Central PMS types | `centralPmsTypes.ts` | Payable-basis types; optional statutory placeholders only. | EXTEND_EXISTING_COMPONENT | Add shared statutory DTOs and facade statutory fields after backend extension. | Yes | Yes |
| Payable-basis display | `App.tsx` | Displays one authoritative amount/snapshot. | EXTEND_EXISTING_COMPONENT | Show original/applied basis and statutory facts from Central PMS. | Yes | Yes |
| readyForCashAcceptance handling | `App.tsx`; APT facade contract | Desktop trusts `readyForCashAcceptance`; facade lacks statutory dimension. | BOUNDED_CENTRAL_PMS_FACADE_EXTENSION_REQUIRED | Add statutory dimension/blockers to facade. | Yes | Yes |
| Readiness blockers | `App.tsx`; APT facade contract | Non-statutory blockers only. | BOUNDED_CENTRAL_PMS_FACADE_EXTENSION_REQUIRED | Add awaiting-review, rejected, application-processing, required-facts, and applied-basis blockers. | Yes | Yes |
| Immediate revalidation | `App.tsx`; APT readiness service | Revalidates tariff/amount/cash/fiscal; not statutory applied basis. | BOUNDED_CENTRAL_PMS_FACADE_EXTENSION_REQUIRED | Revalidate applied snapshot and final statutory amount. | Yes | Yes |
| Durable pre-cash state | `TerminalCashPayableBasisState.cs` | Persists non-statutory basis. | EXTEND_EXISTING_COMPONENT | Link statutory decision/application and applied basis evidence. | Yes | Yes |
| SQLite schema | `CashJournalDbContext.cs`, `CashJournalService.cs` | Has payable-basis table; no statutory table. | EXTEND_EXISTING_COMPONENT | Add additive state for statutory recovery. | Yes | Yes |
| Local journal bridge | `localJournalBridge.ts`, `LocalJournalBridgeHandler.cs` | No statutory bridge commands. | EXTEND_EXISTING_COMPONENT | Add statutory save/read snapshots and requests. | Yes | Yes |
| Restart recovery | `App.tsx`, `CashJournalService.cs` | Restores non-statutory basis. | EXTEND_EXISTING_COMPONENT | Restore pending/retry/applied statutory state. | Yes | Yes |
| CashCapturePanel | `CashCapturePanel.tsx` | Records cash only after revalidation and attestation. | EXTEND_EXISTING_COMPONENT | Consume statutory-blocked readiness; no approval logic. | No | Yes |
| Denomination entry | `CashCapturePanel.tsx` | Local entry after cash panel opens. | NO_CHANGE | Keep blocked until statutory readiness; reuse amount-change reset/review. | No | Yes |
| CASH_RECEIVED | `CashCapturePanel.tsx`, `CashJournalService.cs` | Irreversible local custody. | EXTEND_EXISTING_COMPONENT | Add active-statutory blockers before boundary; non-statutory unchanged. | No | Yes |
| Terminal-cash submission | `CashJournalService.cs`, `TerminalCashPaymentSubmissionService.cs` | Uses existing tender/payable basis after cash. | EXTEND_EXISTING_COMPONENT | Ensure applied snapshot/final amount are submitted once statutory cash is later authorized. | No | Yes |
| Payment-finality readback | `CentralPmsTerminalCashPaymentClient.cs`, `CashCapturePanel.tsx` | Reads canonical payment state. | NO_CHANGE | No statutory-specific change proven after applied basis is used. | No | No |
| Fiscal readback | `CentralPmsTerminalCashFiscalClient.cs`, `CashCapturePanel.tsx` | Reads fiscal state. | NO_CHANGE | POS Server/Central PMS own fiscalized statutory facts. | No | No |
| Receipt retrieval | `TerminalCashReceiptRetrievalService.cs` | Retrieves authoritative presentation. | NO_CHANGE | Continue using POS Server presentation. | No | No |
| Receipt display | `CashCapturePanel.tsx`, `ReceiptPreviewSupport.cs` | Displays governed authoritative presentation. | NO_CHANGE | No local statutory rendering/calculation. | No | No |
| Printing | `TerminalCashReceiptPrintJobService.cs`, `ReceiptPrintSupport.cs` | Prints stored authoritative presentation. | NO_CHANGE | No statutory-specific printing logic beyond payload. | No | No |
| Print history | `LocalJournalBridgeHandler.cs`, `CashCapturePanel.tsx` | Read-only print evidence. | NO_CHANGE | No statutory-specific change currently required. | No | No |
| Transaction-completion state machine | `CashCapturePanel.tsx` | Payment/fiscal/receipt completion without ExitAuthorization inference. | NO_CHANGE | Reuse after applied payable basis is paid/fiscalized. | No | No |
| E2E | `tests/AssistedPaymentTerminal.EndToEndTests/run-e2e.mjs` | Non-statutory active/expired readiness. | EXTEND_EXISTING_COMPONENT | Add statutory pending/approved/rejected/applied/recovery tests later. | Yes for desktop slice | Yes |
| Visual smoke | `PayableBasisVisualSmoke.tsx`, `TransactionCompletionVisualSmoke.tsx` | Non-statutory scenarios. | EXTEND_EXISTING_COMPONENT | Add statutory pre-cash scenarios after facade support. | Yes for desktop slice | Yes |
| Proof scripts | payable-basis and completion proof scripts | Current authority-boundary proof only. | EXTEND_EXISTING_COMPONENT | Add statutory no-local-calculation and cash-blocking proofs. | Yes for desktop slice | Yes |
| Privacy handling | `LocalOperations/README.md`; DB review comments | Avoids secrets; backend prohibits raw evidence. | EXTEND_EXISTING_COMPONENT | Store only masked/reference statutory facts. | Yes | Yes |
| Site and terminal authorization | `App.tsx`; APT facade contract | Uses terminal/Site context and facade policy. | BOUNDED_CENTRAL_PMS_FACADE_EXTENSION_REQUIRED | Scope statutory readiness to same Site/terminal. | Yes | Yes |
| Correlation | `App.tsx`, `centralPmsClient.ts`, statutory DTOs | Correlation is already passed/persisted for payable basis. | EXTEND_EXISTING_COMPONENT | Persist decision/application correlations. | Yes | Yes |
| Idempotency recovery | backend statutory tests; payment outbox | Payment idempotency exists; no statutory idempotency state. | ADD_NEW_COMPONENT | Durable decision/application idempotency refs and recovery actions. | Yes | Yes |

## 7. Statutory state mapping

| State | UI | SQLite | Restart | Cashier action | CASH_RECEIVED | Poll/retry |
| --- | --- | --- | --- | --- | --- | --- |
| Decision NOT_SUBMITTED | Needed | Optional draft | Yes if started | Submit safe facts | Disabled when active statutory | No |
| Decision SUBMITTING | Needed | Request/idempotency pending | Yes | Wait | Disabled | No duplicate submit |
| Decision AWAITING_REVIEW | Needed | Decision ID/status/action | Yes | Wait/check status | Disabled | Poll readback |
| Decision COMPLETED_APPROVED | Needed | Decision result/validation ID | Yes | Submit application intent | Disabled until APPLIED | One application intent |
| Decision COMPLETED_REJECTED | Needed | Result/safe code | Yes | Stop statutory path | Disabled for statutory cash | Terminal |
| Decision RETRYABLE_FAILURE | Needed | retry/action/safe code | Yes | Retry original idempotency key | Disabled | Bounded retry |
| Decision TERMINAL_FAILURE | Needed | terminal safe code | Yes | Support | Disabled | Do not retry |
| Decision REQUIRED_FACTS_UNAVAILABLE | Needed | missing fact code | Yes | Correct facts | Disabled | No blind retry |
| Application NOT_REQUESTED | Needed | Decision ID | Yes | Submit after approval | Disabled | No poll |
| Application SUBMITTING_APPLICATION_INTENT | Needed | app idempotency/recovery refs | Yes | Wait | Disabled | No duplicate submit |
| Application APPLICATION_PROCESSING | Needed | Application ID/status | Yes | Wait/check | Disabled | Poll readback |
| Application APPLIED | Needed | Application ID, applied snapshot, final amount, VAT/discount | Yes | Review changed amount if applicable | Only after facade ready and revalidation | Readback only |
| Application RETRYABLE_FAILURE | Needed | retry/action/safe code | Yes | Retry original idempotency key | Disabled | Bounded retry |
| Application TERMINAL_FAILURE | Needed | terminal code | Yes | Support | Disabled | Do not retry |
| Application REQUIRED_FACTS_UNAVAILABLE | Needed | missing facts | Yes | Correct facts | Disabled | No blind retry |
| payableBasisReady=false | Needed | readiness status/action | Yes | Follow Central PMS action | Disabled | Depends on action |
| payableBasisReady=true | Needed | applied basis evidence | Yes | Continue to revalidation | Not sufficient alone | Revalidate before cash |
| AWAITING_REVIEW | Needed | Decision ID | Yes | Wait/check | Disabled | Poll |
| DECISION_APPROVED_APPLICATION_NOT_REQUESTED | Needed | Decision ID | Yes | Submit application | Disabled | One intent |
| APPLICATION_PROCESSING | Needed | App ID/status | Yes | Wait/check | Disabled | Poll |
| DECISION_REJECTED | Needed | Decision result | Yes | Stop statutory path | Disabled | Terminal |
| REQUIRED_FACTS_UNAVAILABLE | Needed | Safe missing fact code | Yes | Correct request | Disabled | No blind retry |
| POLL_READBACK | Needed | next-read evidence | Yes | Check status | Disabled until ready | Bounded poll |
| SUBMIT_APPLICATION_INTENT | Needed | decision/application ids | Yes | Submit application | Disabled | One intent |
| WAIT_THEN_RETRY_ORIGINAL_IDEMPOTENCY_KEY | Needed | idempotency/retry time | Yes | Retry same command | Disabled | Bounded retry |
| DO_NOT_RETRY | Needed | terminal code | Yes | Support | Disabled | None |

No statutory state permits CASH_RECEIVED by itself. CASH_RECEIVED requires Central PMS statutory readiness, normal Central PMS readiness, local prerequisites, and immediate revalidation.

## 8. Payable-basis impact

When statutory application reaches APPLIED, APT must treat the Central PMS applied tariff snapshot and final payable amount as the current authoritative payable basis.

Impacts:

- `originalTariffSnapshotId` remains historical/support evidence.
- `appliedTariffSnapshotId` becomes the expected tariff snapshot for terminal-cash submission and revalidation.
- Displayed amount changes only from Central PMS final statutory amount.
- Expected revalidation amount is the final statutory amount returned by Central PMS.
- Tariff validity must come from the applied snapshot/readiness facade.
- Currency must come from Central PMS.
- VAT-exclusive amount, VAT amount, VAT treatment, and statutory discount amount are display/persistence evidence only.
- Correlation must preserve decision/application/readiness support references.

APT must not calculate the discount, calculate VAT, derive the final amount, continue using the original snapshot after APPLIED, infer APPLIED from POST acceptance, silently overwrite the amount, or create duplicate application commands.

The existing AMOUNT_CHANGED review model in `App.tsx` should be reused for applied statutory basis changes: show prior and new amounts, require acknowledgement, do not silently overwrite, and require revalidation before CASH_RECEIVED.

## 9. readyForCashAcceptance and revalidation impact

Facade statutory-awareness classification: BOUNDED_CENTRAL_PMS_FACADE_EXTENSION_REQUIRED.

Evidence:

- `D:\SourceCodes\ExitPass-APT\contracts\central-pms\apt-session-payable-basis-readiness.v1.json` lists resolve/revalidate fields for parking session, tariff snapshot, amount, currency, readiness dimensions, `readyForCashAcceptance`, blocking reason codes, retryability, and correlation. It does not list statutory decision/application readiness, applied tariff snapshot, VAT fields, statutory discount amount, final statutory amount, or statutory readiness action.
- `D:\SourceCodes\ExitPass-APT\src\Services\CentralPms\src\ExitPass.CentralPms.Contracts\TerminalCashPayments\AptPayableBasisDtos.cs` defines APT payable-basis request/response without statutory fields.
- `D:\SourceCodes\ExitPass-APT\src\Services\CentralPms\src\ExitPass.CentralPms.Application\TerminalCashPayments\AptPayableBasisReadinessService.cs` evaluates parking session, tariff, terminal-cash eligibility, Sales Invoice readiness, and fiscal readiness. Targeted search for statutory/discount/VAT/applied/readiness-action terms returned no implementation evidence.

Required Central PMS facade extension:

- Recognize active statutory decision/application state for the parking session.
- Return statutory readiness and blockers for awaiting review, rejected, application not requested, application processing, retryable failure, terminal failure, required facts unavailable, and `payableBasisReady=false`.
- Return applied tariff snapshot, final amount, currency, VAT-exclusive amount, VAT amount, VAT treatment, statutory discount amount, decision/application IDs, readiness status/action, retryability, safe error, and correlation.
- Revalidate the applied tariff snapshot and final amount before CASH_RECEIVED.
- Keep `readyForCashAcceptance=false` until statutory and existing terminal-cash/fiscal readiness checks pass.

## 10. Local durable-state and restart impact

Desktop should extend the local journal with statutory recovery state linked to the existing payable-basis state and parking session. A separate statutory state table is likely cleaner than overloading `terminal_cash_payable_basis_states`, but the implementation slice should verify repository conventions before choosing. Either approach must be additive and idempotent.

Required safe recovery fields: parking-session reference, lookup reference, Site ID, Site Group ID, terminal ID, `statutoryDiscountDecisionCommandId`, `statutoryDiscountPayableBasisApplicationCommandId`, entitlement type, request reference, original idempotency reference, correlation IDs, decision status/result, application status/result classification, retryability, recovery classification, recovery action, safe error code, original tariff snapshot ID, applied tariff snapshot ID, original amount, VAT-exclusive amount, VAT amount, VAT treatment, statutory discount amount, final payable amount, currency, `payableBasisReady`, readiness status/action, and last readback timestamp.

Do not store full statutory ID, raw ID image, Base64 evidence, reviewer identity, reviewer notes, Operator Console device or shift identity, credentials, authorization headers, HikCentral details, raw exceptions, stack traces, or raw SQL.

Restart must resume without duplicate decision/application submission, without crossing CASH_RECEIVED, without terminal-cash submission, and without inferring approval or APPLIED status.

## 11. CASH_RECEIVED impact

The current generic CASH_RECEIVED workflow remains unchanged for non-statutory transactions.

For an active statutory workflow, CASH_RECEIVED must remain blocked when any condition applies:

- Decision is `AWAITING_REVIEW`.
- Decision is `REJECTED`.
- Application is not requested after approval.
- Application is processing.
- Application failed retryably or terminally.
- `payableBasisReady=false`.
- Applied tariff snapshot is missing.
- Final payable amount is missing.
- Currency is missing.
- Site/Site Group facts are unavailable.
- Central PMS is unavailable.
- Fiscal readiness is unavailable.
- Immediate pre-cash revalidation has not passed.

The current rule that local prerequisites can only restrict Central PMS readiness remains valid. It should consume a statutory dimension from Central PMS; APT must not derive statutory readiness locally.

## 12. Post-cash impact

After statutory application is APPLIED and statutory cash is eventually authorized:

- Terminal-cash submission must use the applied snapshot and final statutory amount already persisted as the authoritative basis.
- APT must not calculate or alter statutory fiscal content.
- Payment finality readback remains unchanged.
- Fiscal readback remains unchanged.
- Receipt display remains unchanged because it displays the POS Server-owned Sales Invoice presentation.
- Thermal printing remains unchanged because it prints the stored authoritative presentation and does not mutate facts or hash.
- Print history remains unchanged because it is local print evidence only.
- The existing ExitAuthorization readback gap remains separate; APT must not infer exit authorization from statutory, payment, fiscal, receipt, or print states.

## 13. Privacy and security impact

APT may handle only safe statutory facts: entitlement type, masked statutory ID reference, document type, issuing authority, expiry date, safe evidence references, requester attestation, safe notes when allowed, idempotency reference, and correlation/support references.

APT must not log, persist, or display raw statutory ID values, raw ID images, OCR output, Base64 evidence, reviewer identity, reviewer notes, Operator Console device or shift identity, bearer tokens, API keys, authorization headers, connection strings, HikCentral details, raw server bodies, stack traces, or raw SQL.

Fixtures, visual smoke, and proof scripts must use masked/reference-only evidence and must not place sensitive statutory data in query strings, logs, screenshots, or local storage.

## 14. WebPay alignment impact

Classification:

- Shared backend behavior: SAME_SHARED_BEHAVIOR.
- WebPay browser UI/proxy behavior: WEBPAY_BROWSER_SPECIFIC_NOT_APPLICABLE.
- APT local implementation: APT_DESKTOP_SPECIFIC_EXTENSION.
- Current APT payable-basis facade: ALIGNMENT_GAP.

Evidence:

- `D:\SourceCodes\ExitPass\src\Services\WebPayUi\e2e\webpay-authoritative-sales-invoice.spec.ts` verifies WebPay statutory pending-review browser smoke: statutory request, POST/GET decision behavior, payment intent blocked while pending, no unsafe VAT/final/applied-snapshot fields in browser request, rejection/retryable/terminal states, applied status UI, and no payment/fiscal duplicates.
- `D:\SourceCodes\ExitPass\docs\v1.3\webpay\reviews\ExitPass_WebPay_Statutory_Discount_Integration_Impact_Analysis_v1.0.md` documents payment initiation blocked until `payableBasisReady=true`, applied snapshot, final amount, and currency are present.

APT must align semantically, but it must use shared statutory routes and the APT payable-basis facade, not WebPay browser routes or browser storage.

## 15. Conflicts and findings

### Finding APT-STAT-001

- Severity: High
- Condition: The APT-facing payable-basis resolve/revalidate facade does not expose statutory readiness, applied tariff snapshot, final statutory amount, VAT facts, discount facts, or statutory recovery action.
- Current APT evidence: `contracts/central-pms/apt-session-payable-basis-readiness.v1.json`, `AptPayableBasisDtos.cs`, and `AptPayableBasisReadinessService.cs` in `D:\SourceCodes\ExitPass-APT` include non-statutory readiness dimensions only.
- Statutory backend evidence: `StatutoryDiscountDecisionDtos.cs` and `StatutoryDiscountServiceChannelPostApprovalApplicationIntentApiIntegrationTests.cs` expose and test those statutory fields.
- Impact: Desktop cannot safely enable statutory CASH_RECEIVED or revalidate an applied statutory basis without local inference.
- Owner: `D:\SourceCodes\ExitPass-APT`.
- Bounded correction: Extend the APT payable-basis readiness and revalidation facade to consume canonical statutory readiness and applied basis facts.
- Blocks first slice: Yes.
- Blocks statutory CASH_RECEIVED authorization: Yes.

### Finding APT-STAT-002

- Severity: High
- Condition: Desktop has no typed shared statutory decision/readback client and no durable decision/application recovery state.
- Current APT evidence: `centralPmsClient.ts` calls only APT payable-basis resolve/revalidate; `localJournalBridge.ts` and `LocalJournalBridgeHandler.cs` expose no statutory bridge commands; `TerminalCashPayableBasisState.cs` has no statutory decision/application fields.
- Statutory backend evidence: shared statutory POST/GET routes and DTOs exist.
- Impact: After the facade gap is fixed, desktop still needs a bounded statutory orchestration slice before statutory cash can be considered.
- Owner: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.
- Bounded correction: Add review-mediated statutory orchestration and recovery state in APT after Central PMS facade extension.
- Blocks first slice: No; Central PMS facade extension should happen first.
- Blocks statutory CASH_RECEIVED authorization: Yes.

### Finding APT-STAT-003

- Severity: High
- Condition: Existing cash controls trust `readyForCashAcceptance` and local prerequisites, but no statutory dimension can currently flow into that final boolean from the APT facade.
- Current APT evidence: `App.tsx` computes central readiness from `displayedBasis.readyForCashAcceptance`; `CashCapturePanel.tsx` relies on pre-cash revalidation before `Record Cash Received`.
- Statutory backend evidence: statutory readback exposes `PayableBasisReady` and readiness action separate from generic payment readiness.
- Impact: Desktop must not infer statutory readiness from decision readback alone.
- Owner: `D:\SourceCodes\ExitPass-APT` first, then desktop consumer.
- Bounded correction: Central PMS includes statutory blockers in `readyForCashAcceptance`; desktop displays them and remains blocked.
- Blocks first slice: Yes.
- Blocks statutory CASH_RECEIVED authorization: Yes.

### Finding APT-STAT-004

- Severity: Medium
- Condition: Current desktop visual smoke, proofs, and E2E cover non-statutory payable-basis and post-cash flows only.
- Current APT evidence: `PayableBasisVisualSmoke.tsx`, `TransactionCompletionVisualSmoke.tsx`, `Invoke-AptPayableBasisReadinessUiProof.ps1`, and `run-e2e.mjs` use non-statutory fixtures.
- Statutory backend evidence: WebPay and Central PMS statutory tests cover pending review, approved, applied, retryable, and terminal states.
- Impact: Future desktop implementation must add statutory scenarios without raw evidence.
- Owner: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.
- Bounded correction: Extend development-only visual smoke/proofs after the Central PMS facade is statutory-aware.
- Blocks first slice: No.
- Blocks statutory CASH_RECEIVED authorization: Yes for desktop sign-off.

### Finding APT-STAT-005

- Severity: Informational
- Condition: Existing receipt display, thermal printing, print history, payment readback, fiscal readback, and transaction-completion surfaces appear reusable after statutory application because they consume authoritative Central PMS/POS Server state rather than calculating receipt facts locally.
- Current APT evidence: `CashCapturePanel.tsx`, `TerminalCashReceiptRetrievalService.cs`, `TerminalCashReceiptPrintJobService.cs`, `LocalOperations/README.md`, and print-history bridge code.
- Statutory backend evidence: database function comments state statutory application does not create payment/fiscal/gate side effects; POS Server remains Sales Invoice authority.
- Impact: No statutory-specific post-cash redesign is indicated by current evidence.
- Owner: No immediate change.
- Bounded correction: Regression tests only in later desktop statutory slice.
- Blocks first slice: No.
- Blocks statutory CASH_RECEIVED authorization: No.

## 16. Required next task

Repository: `D:\SourceCodes\ExitPass-APT`.

Task: Extend the APT payable-basis readiness and revalidation facade to consume canonical statutory readiness, applied tariff snapshot, final amount, VAT, and discount facts.

Bounded scope:

- Reuse shared statutory decision/application readback.
- Add statutory readiness dimension and safe blocker codes to APT payable-basis resolve/revalidate.
- Return applied tariff snapshot ID, final statutory payable amount, currency, VAT-exclusive amount, VAT amount, VAT treatment, statutory discount amount, decision/application IDs, readiness status/action, retryability, safe error, and correlation where applicable.
- Ensure `readyForCashAcceptance=false` for active statutory workflows until `payableBasisReady=true`, applied basis facts exist, existing terminal-cash/fiscal readiness passes, and revalidation validates the applied basis.
- Preserve no payment, fiscal, receipt, ExitAuthorization, HikCentral, or gate side effects.

Why no other task should precede it: desktop implementation cannot safely enforce statutory CASH_RECEIVED restrictions or immediate revalidation without Central PMS returning statutory-aware readiness. Building desktop first would force local inference, violating the authority model.

## 17. Deferred work

Deferred: APT desktop statutory workflow, statutory CASH_RECEIVED authorization, APT controlled UAT, APT cash controlled UAT, production rollout, degraded/offline tariff policy, discount policy changes, cash submission redesign, fiscal issuance changes, receipt changes, printing changes, print-history changes, ExitAuthorization, gate integration, full multi-service UAT, OCR/raw document upload, and any runtime/API/DTO/SQLite/test changes in this documentation-only slice.

## 18. Evidence inventory

APT desktop evidence:

- `src/AssistedPaymentTerminal.App/src/App.tsx`
- `src/AssistedPaymentTerminal.App/src/CashCapturePanel.tsx`
- `src/AssistedPaymentTerminal.App/src/api/centralPmsClient.ts`
- `src/AssistedPaymentTerminal.App/src/api/centralPmsTypes.ts`
- `src/AssistedPaymentTerminal.App/src/api/mockCentralPms.ts`
- `src/AssistedPaymentTerminal.App/src/localJournalBridge.ts`
- `src/AssistedPaymentTerminal.App/src/PayableBasisVisualSmoke.tsx`
- `src/AssistedPaymentTerminal.App/src/TransactionCompletionVisualSmoke.tsx`
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPayableBasisState.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CashJournalDbContext.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CashJournalRequests.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CashJournalSnapshots.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CashJournalService.cs`
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashPaymentSubmissionService.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CentralPmsTerminalCashPaymentClient.cs`
- `src/AssistedPaymentTerminal.LocalOperations/CentralPmsTerminalCashFiscalClient.cs`
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashReceiptRetrievalService.cs`
- `src/AssistedPaymentTerminal.LocalOperations/TerminalCashReceiptPrintJobService.cs`
- `src/AssistedPaymentTerminal.LocalOperations/README.md`
- `src/AssistedPaymentTerminal.Desktop/LocalJournalBridgeHandler.cs`
- `tests/AssistedPaymentTerminal.EndToEndTests/run-e2e.mjs`
- `scripts/Invoke-AptPayableBasisReadinessUiProof.ps1`
- `scripts/Invoke-AptCashierTransactionCompletionUiProof.ps1`

Statutory backend evidence:

- `src/Services/CentralPms/src/ExitPass.CentralPms.Api/Endpoints/StatutoryDiscountDecisionEndpoints.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/StatutoryDiscounts/StatutoryDiscountDecisionDtos.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/StatutoryDiscounts/StatutoryDiscountDecisionFacadeService.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/StatutoryDiscounts/StatutoryDiscountDecisionFacadeModels.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/StatutoryDiscounts/StatutoryDiscountServiceChannelReviewModels.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Infrastructure/StatutoryDiscounts/PostgresStatutoryDiscountDecisionFacadeRepository.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Infrastructure/StatutoryDiscounts/PostgresStatutoryDiscountServiceChannelReviewRepository.cs`
- `src/Services/CentralPms/tests/ExitPass.CentralPms.ContractTests/Public/StatutoryDiscountDecisionContractTests.cs`
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Api/StatutoryDiscountDecisionApiAccessPolicyIntegrationTests.cs`
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Api/StatutoryDiscountServiceChannelPostApprovalApplicationIntentApiIntegrationTests.cs`

APT-driven Central PMS facade evidence:

- `contracts/central-pms/apt-session-payable-basis-readiness.v1.json`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Api/Endpoints/AptPayableBasisEndpoints.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Contracts/TerminalCashPayments/AptPayableBasisDtos.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/AptPayableBasisReadinessService.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Application/TerminalCashPayments/AptPayableBasisReadinessModels.cs`
- `src/Services/CentralPms/src/ExitPass.CentralPms.Infrastructure/TerminalCashPayments/TerminalCashPayableBasisEligibilityReader.cs`
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Api/AptPayableBasisReadinessApiIntegrationTests.cs`
- `src/Services/CentralPms/tests/ExitPass.CentralPms.IntegrationTests/Contracts/AptPayableBasisReadinessContractTests.cs`
- `scripts/Invoke-CentralPmsAptPayableBasisReadinessProof.ps1`

Canonical database evidence:

- `build/generated/exitpass-full-object.generated.sql`

WebPay reference evidence:

- `src/Services/WebPayUi/e2e/webpay-authoritative-sales-invoice.spec.ts`
- `src/Services/PaymentOrchestrator/src/ExitPass.PaymentOrchestrator.Contracts/WebPay/WebPayStatutoryDiscountDtos.cs`
- `src/Services/PaymentOrchestrator/src/ExitPass.PaymentOrchestrator.Application/Abstractions/Integrations/CentralPmsStatutoryDiscountDecision.cs`
- `docs/v1.3/webpay/reviews/ExitPass_WebPay_Statutory_Discount_Integration_Impact_Analysis_v1.0.md`
