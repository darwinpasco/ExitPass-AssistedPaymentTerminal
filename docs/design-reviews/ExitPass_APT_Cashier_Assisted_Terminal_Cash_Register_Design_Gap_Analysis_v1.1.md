# ExitPass APT Cashier-Assisted Terminal Cash-Register Design Gap Analysis v1.1

Date: 2026-07-15

Primary repository: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`

Read-only reference repository: `D:\SourceCodes\ExitPass`

Target branch: `docs/rewrite-cashier-assisted-terminal-gap-analysis`

Scope: Design terminology, scope, and product-classification correction only. This v1.1 document supersedes v1.0 and narrows the completed cash-register gap analysis to the Windows Cashier-Assisted Terminal. It does not perform another repository audit, reopen the completed gap analysis, define production code, create database migrations, change the terminal shell, analyze Continuity Terminal behavior, push, or create a pull request.

Version-change statement: v1.1 removes the obsolete numbered-mode designations, reframes the analysis around the Cashier-Assisted Terminal, treats the Continuity Terminal as a separate future BCP/degraded-operation terminal, and does not materially reopen or repeat the completed gap analysis.

## 1. Executive decision

The Cashier-Assisted Terminal is the Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations. It is the focus of this analysis. The current Assisted Payment Terminal BRD/system design and current APT repository correctly preserve the major ExitPass authority boundaries, but they stop at generic "payment-capable terminal" behavior and the existing implementation stops before payment collection. They do not define the cash-register responsibilities now required for physical cash tender: local durable custody evidence, drawer/session accounting, cash tender state, cashier attestation, restart recovery, cash-specific idempotency, receipt print journaling, and reconciliation ownership.

The design should be revised before cash tender implementation. The revision should not reopen the approved Windows, React/TypeScript/Vite, .NET/WPF/WebView2, or separate-repository decisions. The revision should explicitly classify the Cashier-Assisted Terminal as:

> The ExitPass Cashier-Assisted Terminal is a Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations. It records terminal-local physical cash and device facts in a durable local operational database, while Central PMS retains canonical payment authority and POS Server retains fiscal authority.

The Continuity Terminal is a separate future terminal for approved BCP and degraded-operation scenarios. It is outside this cash-register design gap analysis and must not influence Cashier-Assisted Terminal requirements unless a shared backend contract is explicitly approved.

Do not assume the Cashier-Assisted Terminal and Continuity Terminal must use the same operating system, deployment package, hardware profile, local database posture, or device integrations. Shared backend authority boundaries and reusable business contracts may exist, but shared application implementation is not a requirement established by this analysis.

The Cashier-Assisted Terminal remains separate from Operator Console. All previously approved cash, drawer, shift, refund, fiscal, receipt, authority, and Operator Console decisions remain unchanged by this terminology and scope correction.

Final readiness classification: `ready_for_design_revision`.

This means the design gap register and decisions are now concrete enough to update the design documents. It does not mean the Cashier-Assisted Terminal is ready for cash tender implementation.

## 2. Sources inspected

When sources conflict, the governing order for this review is: fixed product decisions in the task, current APT repository implementation facts, authoritative ExitPass v1.3 BRD/System Design authority rules, companion APT/POS/Operator/Continuity designs, API/runtime contracts, then supporting runbooks and implementation notes.

| Source | Purpose | Version/status | Authority posture | Cash tender relevance | Local DB relevance | Shift/drawer relevance | Central PMS finality relevance | POS Server fiscal relevance | Contradictions or missing detail |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `README.md` | Current APT product boundary and run/test instructions | Current repo, shell-slice README | Authoritative for current repo baseline | Says payment collection is disabled | Says no local DB/device service | Shows static cashier/shift config only | Preserves Central PMS authority | Preserves POS Server authority | Does not classify Cashier-Assisted Terminal as cash register or POS client; no cash lifecycle |
| `contracts/central-pms/vendor-parking-resolve.contract.json` | Pinned Central PMS resolve snapshot | Current inspected snapshot | Supporting contract snapshot | Provides session/payable basis only | None | None | No cash/payment finality path | None | No cash-payment endpoint |
| `src/AssistedPaymentTerminal.App/src/App.tsx`, `terminalContext.ts`, `config.ts`, `api/*` | Current Cashier-Assisted Terminal UI and Central PMS adapter | Current implementation baseline | Authoritative for actual APT behavior | Payment UI intentionally blocked | No local persistence | Static cashier/shift context | Resolve/recalculate only | None | Uses mock recalculation and static local config; no auth, custody, journal, devices |
| `src/AssistedPaymentTerminal.Desktop/README.md`, WPF source | Thin WebView2 host design | Current implementation baseline | Authoritative for shell behavior | No cash drawer/printer/device service | No local DB | No shift enforcement | None | None | Shell is not responsible for cashier/device logic yet |
| Terminal-shell validation evidence under `docs/evidence/` | Validation evidence for shell slice | Current evidence | Supporting | Confirms no payment/fiscal/devices | None | Bound-context screenshots only | None | None | Useful baseline, not cash evidence |
| `git log --oneline --stat` | Cashier-Assisted Terminal shell history | Current branch history | Supporting implementation history | Shows shell and WebView fix only | None | None | None | None | No cash-register commits exist |
| `docs/v1.3/ExitPass_BRD_v1.3.md` | Core v1.3 business authority baseline | v1.3, 2026-07-01, approved baseline | Authoritative business baseline | Names cashier-assisted terminal but not cash custody | Defers DB detail | Mentions cashier/shift accountability | Central PMS owns payment-linked state/finality | Fiscal before ExitAuthorization | Not cash-register-specific |
| `docs/v1.3/ExitPass_System_Design_v1.3.md` | Core v1.3 architecture baseline | v1.3 | Authoritative architecture baseline | APT is payment-capable, not finality authority | Defers terminal implementation/local storage | Mentions device/shift context | Central PMS owns finality | POS Server owns fiscal | Terminal final implementation architecture deferred |
| `docs/v1.3/assisted-payment-terminal/ExitPass_Assisted_Payment_Terminal_BRD_v1.0.md` | APT companion BRD | v1.0, approved for v1.3 baseline | Authoritative APT business baseline, now incomplete | Mentions payment collection, not physical cash register custody | No local durable DB design | Mentions login/shift/session but not drawer | Explicitly not finality authority | Site POS Server fiscal routing | Says APT shall not "operate as a separate POS system per terminal"; needs clarification so POS client/cash register is not mistaken for fiscal authority |
| `docs/v1.3/assisted-payment-terminal/ExitPass_Assisted_Payment_Terminal_System_Design_v1.0.md` | APT companion system design | v1.0 | Authoritative APT design baseline, now outdated for platform choices | Cash support is open question `APT-SD-OQ-016` | `APT-SD-OQ-023` leaves DB changes open | Shift auth is conceptual only | Preserves finality boundary | Fiscal status display only | Android-first posture superseded by fixed task decisions; no cash state/journal |
| `docs/v1.3/pos-invoicing/ExitPass_POS_Invoicing_BRD_v1.0.md` | Fiscal business baseline | v1.0 approved | Authoritative fiscal business baseline | Applies to Cashier-Assisted Terminal | No terminal local DB | Mentions channel/terminal context | Central PMS remains payment finality | POS Server owns Sales Invoice/fiscal records | Does not define cash tender or receipt-print mechanics for APT |
| `docs/v1.3/pos-server/ExitPass_POS_Server_System_Design_v1.0.md` | POS Server design | v1.0 | Authoritative for POS Server posture | Cashier-assisted channel included | POS Server DB only | X/Z scope open | Consumes payment finality | Owns fiscal numbering, records, recovery | Terminal print/reprint and fiscal adjustment APIs remain open |
| `docs/v1.3/pos-server-api/ExitPass_POS_Server_API_Contract_v1.0.md` | POS Server current API contract | Aligned to current POS Server runtime | Authoritative for current POS Server API | No terminal cash API | None | Channel terminal id in fiscal hash | Central PMS intended trusted caller | `POST/GET /v1/fiscal-documents` implemented | Explicitly says terminals must not call directly unless later boundary permits |
| `docs/v1.3/pos-server-api/ExitPass_Central_PMS_to_POS_Server_Fiscal_Issuance_Integration_Contract_v1.0.md` | Central PMS to POS Server fiscal integration | v1.0 | Authoritative integration contract | Not cash-specific | None | Channel/terminal context only | Central PMS initiates fiscal after finality | Readback/idempotency described | Printable SI, reprints, void/refund/cancel/return fiscal adjustments are not implemented contracts |
| `docs/v1.3/operator-console/ExitPass_Operator_Console_BRD_v1.1.md` | Operator Console boundary | v1.1 approved | Authoritative for Operator Console | Operator Console must not collect payment | None | Device/shift governance only | Read-only payment context | Read-only fiscal context / governance | No APT cash supervision model |
| `docs/v1.3/operator-console/ExitPass_Operator_Console_System_Design_SDD_v1.0.md` and contracts | Operator Console design and fiscal visibility contracts | v1.0 | Supporting/authoritative for OC design | Non-payment surface | None | Shift/device visibility | No finality mutation | Fiscal status viewer/action logs | Lacks cash drawer/session/shortage/overage views |
| `docs/v1.3/continuity/*` | Boundary awareness for Continuity Terminal/BCP | v1.0 approved | Authoritative only for continuity boundary | Warns against unmanaged offline payment | Open | Continuity terminal validation only | Payment uncertainty not finality | Fiscal exception not exit authorization | Do not use Continuity Terminal to overcomplicate Cashier-Assisted Terminal |
| Central PMS code under `src/Services/CentralPms` | Actual API/runtime facts | Current source | Authoritative implementation reality | No public cash payment endpoint | Central DB only | Operator Console shift/device objects exist separately | Payment attempt/create, confirmation, finality, exit auth are Central PMS-owned | Fiscal status/void/readback references exist | Existing payment-attempt API is provider-oriented and returns provider handoff |
| Payment Orchestrator code under `src/Services/PaymentOrchestrator` | Provider execution and outcomes | Current source | Authoritative implementation reality | No cash provider rail | None | None | Reports provider outcomes, not finality | None | Cash should not be shoehorned into provider outcome model |
| Gate Integration source references | Gate command/consumption boundary | Current source | Supporting implementation reality | None | None | None | Consumes Central PMS exit auth | None | APT must not command gates |

## 3. Current Cashier-Assisted Terminal Product Posture

Current APT implementation is a visible Cashier-Assisted Terminal shell, not a cash register implementation. It supports:

- Windows fixed-station app posture through WPF/WebView2.
- React/TypeScript/Vite UI.
- `CASHIER_ASSISTED_TERMINAL` profile enforcement.
- Static terminal/site/POS/cashier/shift display.
- Central PMS session resolve by ticket reference.
- Payable/tariff expiry display and mock recalculation.
- Explicit refusal of payment collection, fiscal issuance, receipt numbering, device integration, and Continuity Terminal behavior.

Current unsafe assumptions for cash-register posture:

- Cashier identity and shift are config values, not authenticated/validated operational state.
- POS Server id is display context, not a verified binding.
- Payment collection can be a follow-on UI slice without a local durable journal. That is unsafe for physical cash.
- Existing Central PMS payment-attempt API can represent cash. It cannot; it requires a provider code and handoff.
- Fiscal success plus print success can be treated as a single backend status. That is incomplete; fiscal truth and local print attempts are separate facts.

## 4. Recommended Cashier-Assisted Terminal Cash-Register Definition

The APT BRD and System Design should use the following terms explicitly:

- Cashier POS Client
- Computerized Cash Register
- Cashier Payment Workstation
- Cash Register Terminal connected to the ExitPass POS Server

Preferred product definition:

> The ExitPass Cashier-Assisted Terminal is a Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations. It records terminal-local physical cash and device facts in a durable local operational database, while Central PMS retains canonical payment authority and POS Server retains fiscal authority.

Do not rename the product. The clarification is a functional classification, not a product rename. The phrase "not a separate POS system per terminal" should be revised to "not an independent fiscal POS authority per terminal" so it does not contradict the approved cash-register classification.

## 5. Authority and source-of-truth matrix

| Fact | Origin | Durable owner | Final authority | Replicas/cache | Mutation authority | Reconciliation obligation |
| --- | --- | --- | --- | --- | --- | --- |
| Raw parking session | Vendor PMS/HCP | Vendor PMS/HCP | Vendor PMS/HCP | Central PMS projection, APT display | Vendor PMS/HCP | Projection freshness and vendor acknowledgment |
| Tariff and tariff expiry | Vendor PMS/HCP live calculation | Vendor PMS/HCP; Central PMS TariffSnapshot for accepted basis | Vendor PMS/HCP for tariff truth; Central PMS for accepted payable basis | APT cached display | Vendor PMS/HCP, Central PMS snapshot creation | Tariff snapshot to payment/fiscal reconciliation |
| Payable basis | Central PMS from vendor response/approved discount basis | Central PMS | Central PMS | APT display/local reference snapshot | Central PMS | Link to payment and fiscal document |
| Statutory discount validation | Cashier captures input; Central PMS/discount workflow validates | Central PMS | Central PMS/discount workflow | APT safe display only | Central PMS/approved reviewer | Evidence/review reconciliation |
| Cash tender started | APT UI/cashier | APT local DB | APT local DB for local event | Central PMS/OC summaries | APT | Local journal to backend payment attempt |
| Physical cash received | Cashier attestation at terminal | APT local DB | APT local DB for physical fact | Central PMS cash payment record references | APT with cashier identity | Must reconcile to Central PMS confirmation and drawer |
| Amount tendered | Cashier entry/denomination count | APT local DB | APT local DB for tender fact; Central PMS for canonical payment amount after accepted | Central PMS cash command | APT before final local receive; correction requires reversal/refund event | Drawer and payment reconciliation |
| Denomination breakdown | Cashier/optional device entry | APT local DB | APT local DB | Central PMS/OC optional summary | APT | Drawer count variance |
| Change due | APT local calculation from payable and amount tendered | APT local DB | APT local DB for local tender computation | UI/backend summary | APT deterministic calculation | Drawer balance |
| Change given | Cashier attestation | APT local DB | APT local DB | Central PMS/OC summary | APT | Drawer balance/dispute evidence |
| Drawer opened | Local device service | APT local DB | APT local DB | OC health/event view | Device service records result | Drawer/session audit |
| Drawer-open failure | Local device service | APT local DB | APT local DB | OC alert | Device service | Incident and tender cancellation |
| Cash refund physically paid | Cashier/supervisor action | APT local DB | APT local DB for physical payout; Central PMS/POS Server for business/fiscal authorization | Central PMS/OC | APT only after approved command | Refund/void reconciliation |
| Payment attempt | APT/Central PMS cash command | Central PMS | Central PMS | APT local reference | Central PMS | Local tender to payment state |
| Canonical payment confirmation | Central PMS after accepted cash command | Central PMS | Central PMS | APT local reference | Central PMS | Cash received to payment confirmation |
| Provider outcome | Payment Orchestrator/provider | Payment Orchestrator | Payment Orchestrator for provider outcome | Central PMS | Payment Orchestrator | Not applicable to physical cash unless cash rail is explicitly modeled outside provider outcomes |
| Fiscal issuance request | Central PMS | Central PMS/POS Server | Central PMS for request/reference; POS Server for document | APT status | Central PMS to POS Server | Payment confirmation to fiscal reference |
| Fiscal document | POS Server | POS Server | POS Server | Central PMS reference, APT receipt payload/status | POS Server | Fiscal document to payment and print |
| Fiscal number | POS Server | POS Server | POS Server | Central PMS safe reference, APT display/print | POS Server | Sequence/counter audit |
| Receipt-print attempt | APT/local device service | APT local DB | APT local DB | OC diagnostics | APT/device service | Print attempt to fiscal document |
| Receipt-print result | Printer/device service | APT local DB | APT local DB | OC diagnostics | Device service | Failed print/reprint review |
| Reprint | APT request plus POS/print rules | APT local DB for attempt; POS Server for reprint permission/payload if required | POS Server for fiscal reprint rules; APT for physical print attempt | OC | APT only after approved status | Reprint audit |
| Controlled fiscal void | Operator/supervisor via approved workflow | POS Server/Central PMS reference | POS Server for fiscal void; Central PMS for controlled command/reference | APT display | Central PMS/POS Server, not APT direct | Void to refund/cash evidence |
| Exit authorization | Central PMS | Central PMS | Central PMS | Gate Integration/APT display | Central PMS | Fiscal-before-exit validation |
| Gate command | Gate Integration | Gate Integration | Gate Integration under Central PMS authorization | Central PMS status | Gate Integration | Gate outcome audit |
| Cashier shift | Central PMS/Operator Console identity/ops | Central PMS/Operator Console domain | Central PMS/Operator Console | APT local copy | Central PMS/Operator Console | Shift totals and permissions |
| Drawer session | APT local DB; possibly Central PMS registry | APT local DB for physical drawer facts | APT local DB for local cash custody; Central PMS/OC for supervisory state | OC summaries | APT local with supervisor controls | Shift/drawer reconciliation |
| Opening float | Cashier/supervisor count | APT local DB | APT local DB | OC/reconciliation | APT with approval | Closing cash variance |
| Expected closing cash | APT local calculation | APT local DB | APT local DB | OC/reconciliation | APT deterministic from journal | Closing reconciliation |
| Actual closing cash | Cashier/supervisor count | APT local DB | APT local DB | OC/reconciliation | APT with approval | Overage/shortage |
| Overage | APT local reconciliation | APT local DB | APT local DB until finance review | OC/reconciliation | APT/reviewer classification | Finance/audit |
| Shortage | APT local reconciliation | APT local DB | APT local DB until finance review | OC/reconciliation | APT/reviewer classification | Finance/audit |
| No-sale drawer open | Cashier/supervisor action/device result | APT local DB | APT local DB | OC alert/summary | APT/device service | Fraud monitoring |
| Paid-out | Cashier/supervisor action | APT local DB, Central PMS/OC approval if policy | APT local DB for physical payout | OC/reconciliation | APT with approval | Drawer and finance review |
| Safe drop | Cashier/supervisor action | APT local DB, Central PMS/OC visibility | APT local DB for physical removal | OC/reconciliation | APT with approval | Drawer closing |
| Terminal restart evidence | APT runtime/local DB | APT local DB | APT local DB | OC diagnostics | APT | Recovery/audit |
| Synchronization state | APT local outbox | APT local DB | APT local DB for sync attempts; Central PMS for accepted commands | OC health | APT sync worker | Pending/failed submission review |
| Reconciliation result | Central PMS/OC/finance workflow with APT facts | Central PMS/reconciliation domain plus APT retained facts | Central PMS/finance for business closure | APT local historical view | Reconciliation authority | Exception closure |

Target posture:

- Vendor PMS is authoritative for raw parking sessions and tariffs.
- Central PMS is authoritative for accepted payable basis, canonical payment state, and payment finality.
- Payment Orchestrator is authoritative for provider execution and verified provider outcomes.
- POS Server is authoritative for fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, and fiscal voids.
- Central PMS and Gate Integration retain exit-authorization and gate authority.
- The Cashier-Assisted Terminal local database is authoritative only for terminal-local physical cash and device facts.
- Audit evidence does not silently replace business authority.

## 6. Cash-tender lifecycle

Smallest coherent Cashier-Assisted Terminal cash-tender state model:

| State | Meaning | Allowed next states |
| --- | --- | --- |
| `TENDER_STARTED` | Cashier selected cash for a current payable basis; no drawer/cash custody yet | `TENDER_CANCELLED`, `DRAWER_OPEN_REQUESTED` |
| `DRAWER_OPEN_REQUESTED` | Durable local drawer command written; device result pending | `DRAWER_OPENED`, `DRAWER_OPEN_FAILED` |
| `DRAWER_OPEN_FAILED` | Device did not open or result unknown before cash custody | `TENDER_CANCELLED`, `SUPERVISOR_REVIEW_REQUIRED` |
| `DRAWER_OPENED` | Drawer open result recorded; cashier may receive/return cash | `CASH_RECEIVED`, `TENDER_CANCELLED`, `SUPERVISOR_REVIEW_REQUIRED` |
| `CASH_RECEIVED` | Cashier attests physical cash received and amount/change facts are locally durable | `SUBMISSION_PENDING` |
| `SUBMISSION_PENDING` | Local cash receipt committed; Central PMS cash-payment command pending or retrying | `PAYMENT_CONFIRMED`, `PAYMENT_REJECTED`, `PAYMENT_STATUS_UNKNOWN` |
| `PAYMENT_STATUS_UNKNOWN` | APT cannot prove accepted/rejected canonical state | `PAYMENT_CONFIRMED`, `PAYMENT_REJECTED`, `RECONCILIATION_REQUIRED` |
| `PAYMENT_REJECTED` | Central PMS rejected command; cash has already been received | `REFUND_REQUIRED`, `RECONCILIATION_REQUIRED` |
| `PAYMENT_CONFIRMED` | Central PMS canonical cash payment confirmation recorded | `FISCAL_PENDING` |
| `FISCAL_PENDING` | Fiscal reference/issuance status not yet recorded as usable | `FISCAL_RECORDED`, `RECONCILIATION_REQUIRED` |
| `FISCAL_RECORDED` | POS Server fiscal document/fiscal number recorded through Central PMS | `PRINT_PENDING` |
| `PRINT_PENDING` | Receipt/SI print job queued or in progress | `PRINTED`, `PRINT_FAILED` |
| `PRINT_FAILED` | Fiscal document exists but local print failed | `PRINT_PENDING`, `COMPLETED_WITH_PRINT_EXCEPTION`, `RECONCILIATION_REQUIRED` |
| `PRINTED` | Local original print succeeded | `COMPLETED` |
| `COMPLETED` | Local tender, backend payment, fiscal status, and print outcome are closed | terminal |
| `TENDER_CANCELLED` | No physical cash custody was recorded | terminal |
| `REFUND_REQUIRED` | Cash was received but backend did not accept canonical payment | `REFUNDED`, `RECONCILIATION_REQUIRED` |
| `REFUNDED` | Cash returned/refund physically paid and recorded | terminal, with reconciliation |
| `RECONCILIATION_REQUIRED` | Manual review required before closure | terminal after review result |
| `SUPERVISOR_REVIEW_REQUIRED` | Local device/custody ambiguity before or during drawer interaction | `TENDER_CANCELLED`, `CASH_RECEIVED`, `RECONCILIATION_REQUIRED` |

Irreversible point: `CASH_RECEIVED`. After that state, do not delete or rewrite the historical cash event. Any return, refund, rejection, or correction must be another append-only event.

Backend readback required:

- After `SUBMISSION_PENDING` timeout or crash before retry.
- After `PAYMENT_STATUS_UNKNOWN`.
- After `PAYMENT_CONFIRMED` before fiscal completion if fiscal reference is missing.
- After fiscal status is pending/unknown.

States that block a new cash tender for the same parking session:

- `TENDER_STARTED` through `PAYMENT_STATUS_UNKNOWN`, `PAYMENT_CONFIRMED`, `FISCAL_PENDING`, `FISCAL_RECORDED`, `PRINT_PENDING`, `PRINT_FAILED`, and `RECONCILIATION_REQUIRED`.
- `TENDER_CANCELLED` before cash receipt does not block.
- `REFUNDED` may allow a new tender only after Central PMS confirms payment state is not final or an approved reversal/refund workflow has closed.

## 7. Physical cash custody boundary

Cash is physically received only when the cashier attests receipt and the terminal commits a local `CASH_RECEIVED` event with amount, payable basis reference, cashier, shift, drawer session, terminal, timestamp, correlation id, and any denomination/change facts. Pressing a generic "cash" or "open drawer" button is not sufficient.

Denomination capture should be mandatory for opening/closing float and actual closing count. For transaction tender, make denomination capture configurable: mandatory for controlled UAT and production cash launch unless Darwin approves optional capture. Even when optional, amount tendered and change given must be required.

Cancellation rules:

- Before `CASH_RECEIVED`: cashier may cancel without reversal, but drawer-open events remain in the local journal.
- After `CASH_RECEIVED`: cancellation is no longer allowed. Return/refund/correction must be explicit, append-only, and reconciled.
- If cash is returned before Central PMS confirmation, record `CASH_RETURNED_BEFORE_CONFIRMATION` or `REFUNDED` with cashier/supervisor context; do not erase `CASH_RECEIVED`.

Failure posture:

| Failure point | Required behavior |
| --- | --- |
| Before cash received | Fail closed. No payment submitted. Keep local tender/drawer events. |
| While drawer is open | Require cashier decision and possibly supervisor review if drawer result/cash status is ambiguous. |
| After cash received before Central PMS confirmation | Keep cash in local custody, retry/readback using persisted idempotency key, block duplicate tender, require reconciliation if status remains unknown. |
| After payment confirmation before fiscal issuance | Keep transaction fiscal-pending, do not authorize exit, surface status and support path. |
| After fiscal issuance before printing | Fiscal document remains valid; print/reprint must be journaled locally. |
| After printing before local completion | Recover from local print success evidence and complete locally after verifying backend references. |

Availability checks before drawer open:

- Current payable basis not expired.
- Cashier authenticated and shift/drawer session open.
- Terminal/site/POS Server binding valid.
- Local DB writable and healthy.
- Central PMS reachable enough to accept or at least preflight cash command unless offline cash is explicitly approved. Recommendation: do not accept cash if Central PMS is unavailable.
- POS Server/fiscal readiness check should pass before drawer open for normal Cashier-Assisted Terminal operation. Recommendation: do not accept cash if POS Server is unavailable or fiscal path is known blocked, unless an approved supervisor exception exists.

## 8. Local operational database design gaps

Existing designs mention local storage/evidence and leave exact DB changes open; they do not define the required local operational database. The Cashier-Assisted Terminal requires one before physical cash tender.

Recommended posture:

- Use SQLite through EF Core as the approved local operational database baseline for the Windows fixed-station Cashier-Assisted Terminal, with WAL mode, explicit transactions, and deterministic schema migrations.
- Require database-at-rest encryption for controlled UAT and production. Preferred approach: SQLCipher-capable SQLite provider or an approved encrypted embedded DB. If SQLCipher licensing/support is rejected, use a Windows-protected encrypted database file strategy approved by security before cash UAT.
- Store key material with Windows DPAPI or certificate-backed Windows key protection bound to registered terminal identity. Do not store reusable secrets in plaintext.
- Keep the DB append-oriented for cash/device/journal events. Allow mutable rows only for derived local projections and sync attempt counters.

Minimum local database responsibilities:

| Object | Purpose | Posture | Retention | Sensitivity/encryption | Sync | Canonical backend references |
| --- | --- | --- | --- | --- | --- | --- |
| `terminal_configuration` | Last known terminal/site/POS bindings | Mutable cache, not authority | While registered plus audit trail | Encrypt if contains secrets; avoid secrets | Yes | terminal id, site id, site group id, POS Server id |
| `terminal_registration` | Local identity/provisioning evidence | Local authoritative for installed identity evidence only | Terminal lifetime | Encrypt | Yes | registration id/certificate thumbprint |
| `cashier_sessions` | Authenticated cashier login/session evidence | Local fact plus backend identity reference | Policy-defined | Encrypt user identifiers | Yes | cashier user id, auth session id |
| `cashier_shift_context` | Active shift snapshot | Cached backend authority plus local usage | Shift plus retention | Encrypt | Yes | shift id, site id |
| `cash_drawer_sessions` | Drawer open/close lifecycle | Local authoritative for drawer facts | Financial retention | Encrypt | Summary | drawer id, shift id |
| `cash_tender_intents` | One local cash tender workflow | Local authoritative for workflow state | Financial retention | Encrypt | Yes | parking session, tariff snapshot, payment confirmation |
| `cash_tender_events` | Append-only tender events | Local authoritative for physical facts | Financial retention | Encrypt | Yes | event id, tender id, backend command id |
| `cash_denomination_entries` | Denomination counts | Local authoritative | Financial retention | Encrypt | Summary | tender/drawer count id |
| `command_journal` | Durable outbound command log | Local authoritative for sent/retry facts | Operational retention | Encrypt | Yes | idempotency key, command hash |
| `backend_submission_attempts` | HTTP attempts/results | Local authoritative for local attempts | Operational retention | Redact payloads | Yes | correlation id, backend request id |
| `backend_reference_snapshots` | Safe backend status/readback copies | Cache only | Operational retention | Encrypt if customer data | Optional | payment/fiscal/reference ids |
| `print_jobs` | Print/reprint attempts | Local authoritative for local print facts | Financial/fiscal retention | Encrypt payload references; avoid raw SI if possible | Yes | fiscal reference/document id |
| `device_events` | Drawer/printer/scanner/device events | Local authoritative | Operational/financial retention | Encrypt if tied to transaction | Summary | device id, event id |
| `local_audit_events` | User/admin/support actions | Local authoritative | Audit retention | Encrypt/redact | Yes | actor, action, correlation id |
| `synchronization_outbox` | Outbox for backend sync | Local authoritative | Until synced plus retention | Encrypt | Yes | command ids |
| `reconciliation_batches` | Local close/reconcile group | Local working state | Financial retention | Encrypt | Yes | backend reconciliation run id |
| `reconciliation_items` | Exception/link rows | Local working state | Financial retention | Encrypt | Yes | payment/fiscal/shift references |
| `diagnostic_events` | Health and crash evidence | Local fact | Support retention | Redact | Summary | terminal id, version |

Never store locally:

- Raw card data, CVV, unprotected payment credentials, reusable secrets in plaintext.
- Local fiscal numbering truth or invented fiscal documents.
- Invented tariff truth or local tariff extensions.
- Invented payment finality.
- Unnecessary full statutory-discount evidence.
- Gate authority or reusable gate tokens.

## 9. Atomicity, idempotency, and recovery

Physical cash creates a consistency problem: the terminal can receive cash even while Central PMS or POS Server is remote. The required pattern is local durable write before remote submission.

Required sequence:

1. Persist `cash_tender_intent`, current payable basis snapshot reference, and generated local tender id.
2. Persist drawer command and device result.
3. Persist `CASH_RECEIVED` with cashier attestation, amount tendered, change due/given, and immutable event id.
4. Persist command journal row with semantic request hash, idempotency key, correlation id, and target Central PMS cash-payment endpoint.
5. Send remote command.
6. Persist every attempt and response.
7. On timeout/restart, read back before retry where possible; retry only with the same idempotency key and same semantic request identity.

Outbox-like synchronization is required. The same idempotency key must survive restart. APT determines retry safety from the local command hash plus backend readback:

| Backend readback result | APT behavior |
| --- | --- |
| No Central PMS record | Retry same command/key if semantic identity matches and payable basis is still accepted for that tender command; otherwise reconciliation required |
| Confirmed payment | Mark `PAYMENT_CONFIRMED`, store payment confirmation id, continue fiscal status |
| Conflict | Stop retries, mark `RECONCILIATION_REQUIRED` |
| Unknown/timeout | Keep `PAYMENT_STATUS_UNKNOWN`, retry/readback on schedule, block duplicate tender |
| Rejected | Enter `REFUND_REQUIRED` or reconciliation, depending on whether cash is still in drawer/customer present |

Current gap: Central PMS exposes a provider-oriented `POST /v1/public/payment-attempts` requiring `PaymentProvider` and returning provider handoff. It does not define a terminal cash-payment command with tender id, physical cash receipt facts, drawer/session binding, amount tendered, change, cashier attestation, or local journal reference.

## 10. Cashier shift and drawer-session model

Cashier shift and cash drawer session must remain separate objects.

Reason: a cashier shift is an identity/authorization/work-period concept. A drawer session is a cash-custody container with opening float, drawer hardware, no-sale opens, paid-outs, safe drops, cash tenders, and closing count. They often align one-to-one in simple deployments, but merging them blocks valid controls such as cashier handover, drawer reassignment, supervisor drawer count, and temporary cashier suspension.

Recommended rules:

- One transaction must bind cashier, authenticated session, cashier shift, terminal, drawer session, site, site group, and POS Server.
- Multiple physical drawers per terminal should be allowed by model but only one active drawer session per cashier terminal in MVP.
- One drawer shared across cashiers should be disallowed in MVP unless supervisor handover is implemented.
- One cashier moving across terminals should require shift/session transfer policy and should not be in first cash slice.

Ownership:

| Record/fact | Owner |
| --- | --- |
| Cashier authentication, roles, site scope | Central PMS/Operator Console identity/RBAC |
| Shift open/suspend/end/signoff | Central PMS/Operator Console, with APT local usage evidence |
| Drawer session open/close/count/device facts | APT local DB, synchronized summaries/exceptions to Central PMS/Operator Console |
| Opening/closing float and denomination counts | APT local DB, supervisor review in Operator Console if required |
| Safe drops, paid-outs, no-sale opens, cash refunds | APT local DB plus supervisor/backend approval where policy requires |
| Overage/shortage and signoff | APT local DB calculation, Operator Console/finance review closure |

## 11. Cash reconciliation model

Required relationships:

- APT local cash journal to Central PMS payment confirmations.
- Central PMS payment confirmations to POS Server fiscal documents.
- APT print jobs to POS Server fiscal documents.
- Cash tender totals to cashier shift and drawer session expected cash.
- Expected closing cash to actual closing cash.
- Exceptions to Operator Console/finance/audit review.

Exception classifications:

| Case | Classification | Resolution owner |
| --- | --- | --- |
| Local cash received, no Central PMS confirmation | `cash_received_payment_missing` | APT ops + Central PMS reconciliation |
| Central PMS confirmation, no local cash-received record | `backend_payment_missing_local_cash` | Central PMS reconciliation + site supervisor |
| Payment confirmed, no fiscal document | `payment_confirmed_fiscal_missing` | Central PMS/POS Server support |
| Fiscal document exists, no successful print | `fiscal_recorded_print_failed` | APT/site operations |
| Duplicate local tender | `duplicate_local_tender` | APT supervisor/reconciliation |
| Duplicate backend submission | `duplicate_backend_submission` | Central PMS idempotency/reconciliation |
| Cash refund without fiscal void | `cash_refund_without_fiscal_void` | Supervisor/finance/POS review |
| Fiscal void without cash refund evidence | `fiscal_void_without_cash_refund` | Finance/site supervisor |
| Shift closed with pending transactions | `shift_close_pending_cash` | Site supervisor |
| Cash overage | `drawer_overage` | Site supervisor/finance |
| Cash shortage | `drawer_shortage` | Site supervisor/finance |
| Unknown payment state | `payment_state_unknown` | Central PMS/APT reconciliation |
| Failed printer | `print_device_failure` | Site operations/support |
| Terminal clock drift | `terminal_clock_drift` | Support/security |
| Corrupted local database | `local_db_corruption` | Support/security/finance |
| Missing synchronization events | `sync_gap` | APT support/Central PMS reconciliation |

## 12. Fiscal issuance and receipt gaps

Current posture:

- APT should call Central PMS for payment and status. POS Server API contract states public clients, payment channels, terminals, dashboards, and Operator Console must not call POS Server directly unless a later approved boundary permits it.
- Central PMS initiates fiscal issuance to POS Server after payment finality.
- Central PMS fiscal status endpoint is read-only and does not call POS Server live.
- POS Server owns fiscal documents, fiscal numbering, fiscal records, void fiscal posture, and fiscal readback.
- Fiscal-before-exit hard blocking is implemented in Central PMS backend evidence.

Gaps for APT:

- No terminal-facing fiscal completion contract: APT needs a safe status/read model that says when it can print, complete, retry, or hold the customer.
- No receipt/Sales Invoice rendering ownership for APT: POS Server contract explicitly does not implement printable SI rendering, QR presentation, reprints, or customer-facing receipt display.
- No original print versus reprint rules for APT.
- No print-attempt audit contract between APT and Central PMS/POS Server.
- No decision whether APT prints a POS Server-provided payload or renders locally from safe fiscal fields. Recommendation: POS Server should own the fiscal receipt/SI payload or render template; APT should only spool/print and journal local print outcome.
- Successful payment without fiscal issuance must not be considered complete for normal Cashier-Assisted Terminal, because fiscal issuance is required before ExitAuthorization. It may become `FISCAL_PENDING` or exception state, not `COMPLETED`.

## 13. Refund, reversal, correction, and void gaps

| Action | Actor | Approval | Authority | Local APT record | Backend/POS effect | APT posture |
| --- | --- | --- | --- | --- | --- | --- |
| Cancellation before cash receipt | Cashier | No, unless drawer ambiguity | APT local | Append `TENDER_CANCELLED` | None | Execute locally |
| Cash returned before canonical confirmation | Cashier/supervisor | Supervisor if after `CASH_RECEIVED` | APT physical fact; Central PMS for payment state | Append return/refund event | May require backend rejection/readback | Execute only with explicit event |
| Payment reversal | Central PMS/provider flow | Supervisor/finance by policy | Central PMS/provider | Display/reference only plus local cash action if any | Payment state mutation | Request/display, not direct execute unless cash-specific endpoint approves |
| Cash refund | Cashier/supervisor | Required after confirmed payment | APT physical fact plus Central PMS approval | Append cash paid event | May require payment/fiscal relation | Execute after approval |
| Fiscal void | Supervisor/Central PMS/POS Server | Required | POS Server/Central PMS | Display linked local reason | POS fiscal void | Request/display, not direct POS mutation |
| Receipt reprint | Cashier/supervisor | Policy-based | POS Server fiscal rules; APT print fact | Append reprint attempt | POS reprint audit if required | Execute print only after approved payload/status |
| Cashier correction | Cashier/supervisor | Required after cash receipt | APT local event plus backend if business state affected | Append correction | May require reconciliation | Request/execute by approved workflow |
| Drawer adjustment | Cashier/supervisor | Required | APT local drawer facts | Append adjustment | Summary/reconciliation | Execute locally with approval |
| Reconciliation correction | Reconciliation/finance | Required | Central PMS/finance | Local reference only | Exception closure | Display/sync only |

Historical cash events must never be deleted or rewritten in place.

## 14. Hardware and device-service gaps

A local .NET device service becomes mandatory before real cash tender because React/WebView cannot reliably own cash drawer/printer/device protocols or produce trustworthy local device evidence.

| Device | Likely protocol | Preferred integration | Required evidence | Failure handling | Simulator | Stage |
| --- | --- | --- | --- | --- | --- | --- |
| Cash drawer | Printer kick pulse, OPOS/ESC-POS, serial/USB | .NET local device service | command, result, timeout, drawer id | Fail closed before cash; supervisor review on ambiguity | Required | MVP before first cash |
| Receipt printer | ESC/POS, Windows print queue, OPOS | .NET local device service | job id, payload hash/ref, result, reprint flag | Print failed state; allow governed reprint | Required | MVP/UAT |
| Fiscal printer | If separate from receipt printer | .NET service only if POS Server requires local fiscal device | fiscal-device result | Fail fiscal path | Required if used | UAT/later |
| Barcode/QR scanner | HID keyboard, serial, camera | UI for HID; device service for serial/camera | scan source and value classification | Manual fallback | Required | MVP |
| Customer-facing display | Serial/USB/secondary screen | Device service or WPF host | displayed amount/status | Disable if stale | Optional | Later |
| Denomination device | Vendor SDK/serial | Device service | count events | Manual count fallback | Optional | Later |
| Payment terminal/card reader | Vendor SDK/USB/LAN | Separate payment-device integration, not cash slice | provider/session references | Do not mix with cash | Required for card slice | Later |
| Certificate/private key operations | Windows cert store/TPM | WPF/device service/secure helper | thumbprint, signing success | Fail closed | Dev simulator | MVP before live backend |
| Terminal health/diagnostic bundle | OS/WMI/log collection | WPF/device service | redacted logs, versions | Support-only | N/A | Controlled UAT |

Boundary:

- React UI: workflow, cashier prompts, safe status display.
- WPF/WebView2 shell: host, kiosk posture, app lifecycle.
- Local device service: cash drawer, printer, scanner/device IO, secure local evidence.
- Central PMS: canonical payment, cash command, fiscal status facade, exit authorization status.
- POS Server: fiscal document/payload/numbering/void/reprint rules.

## 15. Security and fraud-control gaps

MVP controls:

- Real cashier authentication, not config values.
- Registered terminal identity and site/POS binding.
- Local DB durability and encryption decision closed.
- Append-only cash/device events.
- No cash accepted if local DB is unhealthy.
- No cash accepted if payable basis expired.
- No deletion or local database manual edit workflow.

Controlled UAT controls:

- Supervisor authentication for refunds, void requests, paid-outs, safe drops, no-sale thresholds, and shift close variances.
- Cash drawer open rate alerts.
- Repeated cancellation and repeated shortage alerts.
- Clock drift detection and backend time comparison.
- Diagnostic bundle redaction.
- Windows kiosk/user access policy.

Production controls:

- Certificate/private key protection with rotation/revocation.
- Tamper-evident local journal hash chain or equivalent.
- Retention/purge policy.
- Support access with break-glass audit.
- Finance/audit export controls.
- Segregation of duties for cashier, supervisor, support, finance, and administrator.

## 16. Operator Console and cross-repository gaps

Operator Console must govern and display cash-register state without becoming the cash register.

Needed Operator Console capabilities:

- Terminal registration and suspension.
- Terminal health and local sync backlog.
- Cashier shift and drawer session visibility.
- Pending cash submissions and payment-unknown cases.
- Fiscal-pending and print-failed transactions.
- Overage/shortage review.
- Refund and fiscal-void approval workflow references.
- Reconciliation exception queues.
- Device reassignment and support diagnostics.

Cross-repository changes:

| Owning repository | Proposed capability | Why required | Stage | Blocks |
| --- | --- | --- | --- | --- |
| ExitPass/Central PMS | Terminal cash-payment command API with idempotency/readback | Existing payment-attempt API is provider handoff, not physical cash | Design closure | cash implementation, first cash transaction |
| ExitPass/Central PMS | Cash-payment status/readback by terminal tender id/idempotency key | Restart recovery and duplicate prevention | Design closure | first cash transaction |
| ExitPass/Central PMS | Terminal/shift/drawer binding validation API | Cashier POS control before drawer open | Design closure | controlled UAT |
| ExitPass/Central PMS | Fiscal status facade suitable for APT customer workflow | APT needs completion/hold/print decisions | Cash implementation | first cash transaction |
| ExitPass/POS Server | Printable SI/receipt payload or rendering contract | APT must not invent fiscal receipt rendering | Design closure | first cash transaction |
| ExitPass/POS Server | Reprint and print-copy classification contract | Required for print failure/reprint audit | Controlled UAT | controlled UAT |
| ExitPass/POS Server/Central PMS | Fiscal void plus cash refund linkage | Prevent refund/void divergence | Production | production |
| ExitPass/Operator Console | Cash drawer/shift/reconciliation supervisory views | Governance without payment collection | Controlled UAT | controlled UAT |
| ExitPass/Payment Orchestrator | Explicit statement that physical cash bypasses provider outcome flow unless cash rail is modeled | Avoid misusing provider outcome contracts | Design closure | design lock |
| Shared API contracts | Published APT contract package/OpenAPI for resolve, cash command, fiscal status | Prevent hand-copied DTO drift | Cash implementation | cash implementation |

## 17. Detailed gap register

| Gap ID | Title | Affected source file | Affected section | Current statement/omission | Problem | Risk | Recommended correction | PO decision required | Repo | Severity | Blocker | Stage |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| APT-CR-GAP-001 | Cashier-Assisted Terminal not explicitly classified as cash register | APT BRD, APT System Design, APT README | Executive/product boundary | "payment-capable terminal" and "cashier-assisted" | Weak terminology hides cash custody duties | Design under-scopes audit/local DB/devices | Add Cashier POS Client/computerized cash register classification | Yes | APT + ExitPass | CRITICAL | blocks_design_lock | design closure |
| APT-CR-GAP-002 | "Not a separate POS system per terminal" conflicts with POS client wording | APT BRD | Explicit Non-Authority Scope | Says APT shall not operate as separate POS system per terminal | Could be read to forbid cash-register terminal | Blocks clear product posture | Revise to "not independent fiscal POS authority" | Yes | ExitPass | HIGH | blocks_design_lock | design closure |
| APT-CR-GAP-003 | No cash-payment Central PMS contract | Central PMS contracts/code | Payment attempts | Provider-oriented payment attempt only | Cannot record cash receipt/finality safely | Duplicate or missing cash payments | Define idempotent terminal cash-payment command/readback | Yes | ExitPass | CRITICAL | blocks_cash_implementation | design closure |
| APT-CR-GAP-004 | No local durable operational DB design | APT System Design, APT repo | DB/open questions | DB exact changes open, no APT DB | Physical cash cannot rely on memory/remote call | Cash loss, disputes after crash | Add local DB design with append-only journal/outbox | Yes | APT | CRITICAL | blocks_cash_implementation | design closure |
| APT-CR-GAP-005 | No physical custody point | APT BRD/System Design | Payment workflow | Cash not explicitly modeled | No irreversible boundary | Unsafe cancellation/reversal | Define cashier attested `CASH_RECEIVED` as custody point | Yes | APT | CRITICAL | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-006 | No cash tender state machine | APT System Design | Workflow/state | Generic payment/fiscal status | Cannot handle drawer/cash/backend failures | Duplicate tenders and ambiguous recovery | Add state model in this report | Yes | APT | CRITICAL | blocks_cash_implementation | design closure |
| APT-CR-GAP-007 | Shift and drawer session conflated/undefined | APT BRD/System Design, OC docs | Shift/device | Mentions shift/session but not drawer | Cash custody cannot be closed/reconciled | Overage/shortage untraceable | Separate cashier shift and drawer session model | Yes | APT + ExitPass | HIGH | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-008 | Static cashier/shift config in implementation | APT `config.ts`/README | Runtime config | Dev config supplies cashier/shift | Not authentication/authorization | Fraud and attribution weakness | Replace before cash with backend-authenticated shift context | Yes | APT | HIGH | blocks_first_cash_transaction | cash implementation |
| APT-CR-GAP-009 | No drawer-open/device journal | APT repo | Device integration | No device service | No proof drawer opened/failed | Cash disputes | Add local device service and `device_events` journal | Yes | APT | HIGH | blocks_first_cash_transaction | cash implementation |
| APT-CR-GAP-010 | No denomination policy | APT docs | Cash lifecycle | Omitted | Cannot reconcile counts/float | Weak drawer variance proof | Decide mandatory/optional by stage | Yes | APT | MEDIUM | blocks_controlled_uat | design closure |
| APT-CR-GAP-011 | No rule for cash when Central PMS unavailable | APT/Continuity docs | Offline payment | Offline payment open | Drawer may accept unconfirmable cash | Uncontrolled receivables | Default fail closed for the Cashier-Assisted Terminal; explicit exception only | Yes | APT + ExitPass | HIGH | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-012 | No rule for cash when POS Server unavailable | APT/POS/Continuity docs | Fiscal exception | Fiscal exceptions discussed generally | Paid customer may lack fiscal path | Exit block/customer dispute | Default fail closed before drawer open | Yes | APT + ExitPass | HIGH | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-013 | Existing payment API cannot represent amount tendered/change | Central PMS `CreatePaymentAttemptRequest` | Public payment attempts | parking session, tariff snapshot, provider only | Cash overpay/change facts lost | Drawer mismatch | Add cash tender fields in separate endpoint | Yes | ExitPass | HIGH | blocks_cash_implementation | design closure |
| APT-CR-GAP-014 | No restart recovery/outbox design | APT docs | Recovery | Omitted | Crash after cash receipt unsafe | Duplicate or missing backend command | Add command journal/outbox/readback rules | Yes | APT | HIGH | blocks_first_cash_transaction | cash implementation |
| APT-CR-GAP-015 | No APT print journal | APT docs/POS contract | Receipt printing | Printable SI/reprints deferred | Cannot prove original print/reprint | Fiscal/customer dispute | Add print job journal and POS payload contract | Yes | APT + POS Server | HIGH | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-016 | POS printable receipt contract absent | POS Server API contract | Deferred API | Printable SI rendering/reprints not implemented | APT might render fiscal text locally | Fiscal inconsistency | POS Server owns payload/render template | Yes | POS Server | HIGH | blocks_first_cash_transaction | design closure |
| APT-CR-GAP-017 | No original print vs reprint definition | POS/ATP docs | Reprints | Reprints are BRD concept only | Copy classification/audit unclear | Fiscal noncompliance | Define original/reprint rules and audit fields | Yes | POS Server + APT | MEDIUM | blocks_controlled_uat | design closure |
| APT-CR-GAP-018 | No cash refund/fiscal void linkage | APT/POS/Central docs | Refund/void | Fiscal void exists, refund omitted | Cash paid out may diverge from void | Financial leakage | Define linked workflows and approvals | Yes | ExitPass + APT | HIGH | blocks_production | design closure |
| APT-CR-GAP-019 | Operator Console lacks cash-register governance views | OC BRD/System Design | APT relationship | Supervises generally, non-payment | No drawer/shift/cash exceptions | Blind supervision | Add read-only/supervisory cash views | Yes | ExitPass | HIGH | blocks_controlled_uat | controlled UAT |
| APT-CR-GAP-020 | Local DB encryption/key storage undecided | APT System Design | Security/open questions | Certificate/key storage open | Cash journal and PII at rest exposed | Tamper/privacy risk | Decide SQLCipher/DPAPI/cert storage | Yes | APT | HIGH | blocks_controlled_uat | design closure |
| APT-CR-GAP-021 | No local tamper-evidence posture | APT docs | Audit/security | Omitted | Manual DB modification not detectable | Fraud risk | Hash chain/audit export/support access controls | Yes | APT | MEDIUM | blocks_production | production |
| APT-CR-GAP-022 | No no-sale/paid-out/safe-drop model | APT docs | Drawer control | Omitted | Cash drawer operations unaccounted | Shortage/overage ambiguity | Add drawer operation event types and approvals | Yes | APT | HIGH | blocks_controlled_uat | design closure |
| APT-CR-GAP-023 | No local DB corruption recovery policy | APT docs | Operations | Omitted | Cash journal may be unavailable | Halted operations/evidence loss | Backup, export, corruption fail-closed runbook | Yes | APT | MEDIUM | blocks_controlled_uat | controlled UAT |
| APT-CR-GAP-024 | No terminal clock authority/drift rule | APT/Continuity/POS docs | Security | POS clock open; APT omitted | Cash/fiscal ordering disputed | Audit weakness | Backend time comparison and drift alert | Yes | APT | MEDIUM | blocks_production | controlled UAT |
| APT-CR-GAP-025 | No APT reconciliation exception taxonomy | APT/Central PMS docs | Reconciliation | Generic reconciliation exists | Cash-specific exceptions not classified | Slow closure | Adopt exception classes in Section 11 | Yes | APT + ExitPass | MEDIUM | blocks_controlled_uat | design closure |
| APT-CR-GAP-026 | No customer-present failure messaging | APT docs | UX/workflow | Fiscal/payment display generic | Cashier may tell customer wrong status | Disputes and exit blocking | Add exact copy/flow for pending/unknown/failures | Yes | APT | MEDIUM | blocks_controlled_uat | cash implementation |
| APT-CR-GAP-027 | Continuity Terminal references may overfit Cashier-Assisted Terminal | APT/Continuity docs | Continuity | Offline/degraded questions near Cashier-Assisted Terminal | Could overcomplicate normal cash register | Scope creep | Keep the Cashier-Assisted Terminal fail-closed; do not let Continuity Terminal requirements drive this cash-register analysis | Yes | APT | LOW | informational | design closure |
| APT-CR-GAP-028 | Generated/published APT contracts absent | APT README, contracts snapshot | Contract strategy | Narrow JSON snapshot only | Drift from Central PMS/POS contracts | Integration errors | Publish OpenAPI/package for APT endpoints | Yes | APT + ExitPass | LOW | deferrable | cash implementation |

Gap counts: CRITICAL 5, HIGH 13, MEDIUM 8, LOW 2.

## 18. Required document revisions

| Document | Owning repo | Section to add/update | Purpose | Priority | Prerequisite decisions |
| --- | --- | --- | --- | --- | --- |
| APT BRD | ExitPass | Product purpose, boundary, non-authority, Cashier-Assisted Terminal | Classify the Cashier-Assisted Terminal as a cash-register POS client | P0 | D-001 |
| APT System Design | ExitPass | Workflow/state, local DB, device service, security, recovery | Define cash lifecycle and local durability | P0 | D-002 to D-007 |
| ExitPass BRD | ExitPass | Assisted Payment Terminal positioning | Clarify functional classification without renaming | P1 | D-001 |
| ExitPass System Design | ExitPass | Authority model, APT context, observability | Add local physical-fact authority | P1 | D-001, D-002 |
| POS/Invoicing BRD | ExitPass | Channel/terminal model, reprint/refund/void | Add cash-register terminal implications | P1 | D-008, D-009 |
| POS Server API contracts | ExitPass / POS Server repo | Printable SI, reprint, void/refund adjustment | Define receipt payload and fiscal action contracts | P0 | D-012, D-013 |
| Central PMS API contracts | ExitPass | Cash payment command/readback, shift/drawer validation | Enable canonical cash payment safely | P0 | D-004 to D-006 |
| Operator Console BRD/design | ExitPass | Supervisory views and approvals | Govern cash exceptions without collecting payment | P1 | D-016 |
| APT repository ADRs | APT | Cash-register authority/local DB/device-service ADRs | Capture product decisions in primary repo | P0 | D-001 to D-003 |
| APT local database design | APT | New design doc | Define schema objects, retention, encryption | P0 | D-002, D-015 |
| APT cash-tender state machine | APT | New design doc | Implementation-ready state/transition spec | P0 | D-006 |
| APT shift/reconciliation spec | APT | New design doc | Shift/drawer/reconciliation behavior | P1 | D-010, D-014 |

Do not edit these documents in this task.

## 19. Recommended implementation sequence

Design closure:

1. Product terminology and non-authority correction.
2. Authority matrix including APT local physical/device facts.
3. Cash lifecycle/state machine and custody point.
4. Local database ownership, encryption, retention, corruption recovery.
5. Shift/drawer/session model.
6. Central PMS terminal cash-payment command and readback contract.
7. Fiscal/receipt/print/reprint contract.
8. Cash reconciliation and exception taxonomy.

APT implementation:

1. Local operational database foundation with append-only journal and command outbox.
2. Cashier authentication, shift context, drawer session skeleton.
3. Cash tender journal without remote confirmation, simulator only.
4. Idempotent Central PMS cash-payment submission/readback.
5. Restart and unknown-state recovery.
6. POS fiscal status readback through Central PMS.
7. Receipt print journal and printer simulator.
8. Shift close and reconciliation.
9. Refund and fiscal-void governance.
10. Hardware-in-loop drawer/printer/scanner integration.

Smallest safe first implementation slice after design revision:

> Implement the encrypted/local durable operational database foundation plus simulated drawer/print/device event journal and cash tender state machine through `CASH_RECEIVED` in a non-live mode that cannot submit payment or open real hardware. This proves local durability, restart recovery, append-only events, and shift/drawer bindings before any real cash is accepted.

## 20. Product-owner decision register

| Decision ID | Question | Options | Recommendation | Consequences | Deadline/stage |
| --- | --- | --- | --- | --- | --- |
| D-001 | Formal classification as computerized cash register? | Explicit / implicit | Explicit | Unlocks cash-local design scope | Design closure |
| D-002 | Local DB technology? | SQLite+EF Core, encrypted SQLite/SQLCipher, other embedded DB, no DB profile | SQLite+EF Core with approved encryption before UAT | Balances Windows support and durability | Design closure |
| D-003 | Mandatory denomination capture? | Mandatory all tenders, mandatory only counts, optional tender denominations | Mandatory opening/closing; transaction denominations mandatory for UAT unless waived | Better reconciliation vs cashier speed | Design closure |
| D-004 | Accept cash when Central PMS unavailable? | Yes under local queue, no, supervisor exception | No for normal Cashier-Assisted Terminal operation | Avoids offline finality ambiguity | Design closure |
| D-005 | Accept cash when POS Server unavailable? | Yes fiscal-pending, no, supervisor exception | No for normal cash tender before drawer open | Avoids paid/no fiscal flow | Design closure |
| D-006 | Exact physical custody point? | Drawer opened, cashier confirms amount, backend confirms | Cashier confirms and local `CASH_RECEIVED` commits | Defines irreversible event | Design closure |
| D-007 | Cancellation after cash receipt? | Allow cancel, require refund/return event, supervisor only | No cancel; append return/refund/correction | Preserves evidence | Design closure |
| D-008 | Cash refund authority? | APT cashier, supervisor, Central PMS-approved | Supervisor plus Central PMS-approved where payment final | Prevents unauthorized payouts | Controlled UAT |
| D-009 | Fiscal void authority? | APT direct, Operator Console/Central PMS, POS Server-only | Central PMS/POS Server governed, APT not direct | Preserves fiscal boundary | Controlled UAT |
| D-010 | Shift and drawer relationship? | Same object, separate one-to-one, separate many-to-many | Separate; one active drawer per terminal in MVP | Supports future controls | Design closure |
| D-011 | Safe drops, paid-outs, no-sale opens? | Exclude MVP, include with supervisor, include cashier-only | Include model now; enable with supervisor in UAT | Prevents drawer gaps | Design closure |
| D-012 | Does APT ever call POS Server directly? | Never, only for print payload, direct fiscal read | No direct fiscal authority call; use Central PMS unless approved print gateway | Preserves trust boundary | Design closure |
| D-013 | Receipt rendering ownership? | APT renders, POS Server payload, Central PMS payload | POS Server-owned payload/render template; APT prints | Avoids divergent fiscal text | Design closure |
| D-014 | Windows device-service boundary? | WPF direct, separate local service, browser APIs | .NET local device service for cash drawer/printer/secure device evidence | Testable and auditable | Design closure |
| D-015 | Local retention? | Short operational, financial/audit retention, centralized purge | Financial/audit retention for cash/fiscal print events with purge policy | Storage/privacy tradeoff | Controlled UAT |
| D-016 | Operator Console scope? | Read-only, approvals, full cash operations | Read-only plus approvals/review; no payment collection | Governance without becoming register | Design closure |

## 21. Final readiness classification

`ready_for_design_revision`

The inspected sources are sufficient to revise the design in a focused way. They are not sufficient to implement cash tendering safely. The design must close the cash-register decisions and contract gaps before production code starts.
