# ExitPass Assisted Payment Terminal BRD v1.1

## 1. Document Control

| Field | Value |
| --- | --- |
| Document title | ExitPass Assisted Payment Terminal BRD v1.1 |
| Product family | Assisted Payment Terminal |
| Primary implementation scope | Cashier-Assisted Terminal |
| Repository | `D:\SourceCodes\ExitPass-AssistedPaymentTerminal` |
| Date | 2026-07-15 |
| Status | Baseline for Cashier-Assisted Terminal implementation |
| Supersedes | Assisted Payment Terminal BRD v1.0 for Assisted Payment Terminal implementation |

Revision summary:

| Version | Date | Summary |
| --- | --- | --- |
| v1.1 | 2026-07-15 | Rewrites the Assisted Payment Terminal BRD around the Windows Cashier-Assisted Terminal, cash custody, local durability, optional cash-drawer posture, fiscal authority boundaries, and approved ExitPass authority decisions. |

This BRD is a business requirements document. It is not a system design, database design, API contract, engineering pack, migration plan, device-service design, runbook, or test implementation.

## 2. Executive Summary

The Assisted Payment Terminal is the ExitPass product family for cashier-facing terminal workflows. This BRD centers the implementation baseline on the Cashier-Assisted Terminal.

The ExitPass Cashier-Assisted Terminal is a Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations. It records terminal-local physical cash and device facts in a durable local operational database, while Central PMS retains canonical payment authority and POS Server retains fiscal authority.

The Cashier-Assisted Terminal shall support staffed cash payment operations without weakening ExitPass authority boundaries. It shall not calculate tariffs, declare payment finality, issue fiscal documents, allocate fiscal numbers, authorize exit, command gates, or become an Operator Console administrative surface.

The Continuity Terminal is a separate future BCP/degraded-operation terminal. It is outside this BRD's implementation scope. Shared backend contracts may exist, but shared application implementation is not assumed.

## 3. Product Purpose and Classification

The Assisted Payment Terminal product family shall provide controlled terminal workflows for parking operations that require cashier assistance.

The Cashier-Assisted Terminal shall be the Windows fixed-station cashier application for normal staffed parking operations. It shall function as:

- A Cashier POS Client.
- A computerized Cash Register Terminal.
- A cashier payment workstation connected to ExitPass backend authority.
- A channel under Central PMS payment authority and POS Server fiscal authority.

The Cashier-Assisted Terminal shall use the runtime profile identifier `CASHIER_ASSISTED_TERMINAL` where technically required.

The Cashier-Assisted Terminal shall remain separate from Operator Console. Operator Console may supervise, review, and approve governed exceptions through approved backend workflows, but Operator Console shall never collect cash.

## 4. Business Problem

Parking sites still operate staffed payment points where cashiers accept physical cash from parkers. Without a dedicated Cashier-Assisted Terminal baseline, these operations risk:

- Cashier tools expanding into unauthorized payment or fiscal authority.
- Operator Console being misused for cash collection.
- Physical cash receipt lacking durable terminal-local evidence.
- Cash events being lost during crash, restart, or network interruption.
- Tariff, payable-basis, payment-finality, fiscal, and exit authority being mixed into the terminal.
- Fiscal receipts being rendered or numbered outside POS Server authority.
- Cash drawer assumptions blocking ordinary cashier PCs that do not have electronic drawers.
- Reconciliation gaps across cashier, shift, cash-custody session, payment confirmation, fiscal issuance, receipt printing, and exit authorization.

The business needs a controlled cashier terminal that can safely record physical cash facts locally while preserving backend authority for payment, fiscal, and exit decisions.

## 5. Business Objectives and Success Measures

Business objectives:

| Objective | Success measure |
| --- | --- |
| Support normal staffed cash payment | Authorized cashiers can resolve a payable basis and record cash tender under approved readiness conditions. |
| Preserve authority boundaries | No terminal workflow declares payment finality, creates tariff truth, issues fiscal documents, allocates fiscal numbers, authorizes exit, or commands gates. |
| Provide durable cash evidence | `CASH_RECEIVED` and related cash-custody facts survive terminal restart and can be reconciled. |
| Support ordinary cashier PCs | Cash tender works when cash-drawer integration is disabled. |
| Protect fiscal correctness | POS Server remains owner of fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, and fiscal voids. |
| Support reconciliation | Pending, unknown, overage, shortage, print-failed, fiscal-pending, and duplicate cases are visible for supervised closure. |

## 6. Product Scope and Explicit Exclusions

In scope for this BRD:

- Cashier-Assisted Terminal business requirements.
- Cash tender readiness, custody, recovery, reconciliation, and audit requirements.
- Local operational database business requirements.
- Optional cash-drawer posture.
- Fiscal and receipt business boundaries.
- Operator Console supervision boundary.
- External dependency and acceptance requirements.

Explicit exclusions:

- Continuity Terminal implementation behavior.
- Local database schema, tables, columns, migrations, and exact storage layout.
- Exact Central PMS, POS Server, or Payment Orchestrator DTOs and endpoints.
- Device-service implementation.
- Cash-drawer integration implementation.
- Production code, test code, packaging, deployment, runbooks, and UI design.
- Any modification to the read-only ExitPass reference repository.

## 7. Users and Roles

| Role | Business responsibility | Must not do through Cashier-Assisted Terminal |
| --- | --- | --- |
| Cashier | Resolve customer reference, review payable basis, receive cash, attest cash facts, print approved receipt payloads, and follow customer messaging. | Declare payment finality, allocate fiscal numbers, authorize exit, command gates, or approve fiscal voids. |
| Parker | Presents ticket/card/reference, pays cash, receives customer instructions and receipt where available. | Operate terminal controls. |
| Supervisor | Approves governed exceptions such as refund after confirmation, shift/custody close exceptions, overage/shortage review, and governed reversals where policy allows. | Use Operator Console or terminal supervision to bypass payment, fiscal, or exit authority. |
| Finance/revenue assurance | Reviews payment, fiscal, cash-custody, overage, shortage, and reconciliation evidence. | Treat terminal-local evidence as canonical payment finality or fiscal truth. |
| Technical support | Supports terminal health, local database health, device status, diagnostics, and recovery. | Modify cash history or close financial exceptions without approved workflow. |
| Operator Console user | Supervises terminal state and approved exception queues. | Collect cash, mark payment final, issue fiscal documents, or command gates. |

## 8. Authority and Source-of-Truth Model

Authority boundaries:

| Domain | Authority | Cashier-Assisted Terminal requirement |
| --- | --- | --- |
| Raw parking-session truth | Vendor PMS | Shall display only through approved backend workflow. |
| Tariff truth | Vendor PMS | Shall not calculate, extend, or invent tariffs locally. |
| Accepted payable basis | Central PMS | Shall use only backend-accepted payable basis. |
| Canonical payment state and payment finality | Central PMS | Shall not declare finality; shall submit terminal-local facts and display backend status. |
| Provider execution and verified provider outcomes | Payment Orchestrator | Shall not own provider outcome truth. |
| Fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, and fiscal voids | POS Server | Shall not create fiscal truth; shall print only approved fiscal payloads. |
| Exit authorization | Central PMS | Shall display only. |
| Gate control | Gate Integration under Central PMS authorization | Shall not command gates. |
| Terminal-local physical cash and device facts | APT local operational database | Shall durably record local physical and device evidence. |
| Supervision and governance | Operator Console and approved backend workflows | Shall remain separate from payment collection. |

Audit evidence shall not silently replace business authority. APT communicates with POS Server through Central PMS unless a later explicitly approved narrow contract changes that boundary.

## 9. Cashier-Assisted Terminal Operating Preconditions

Cash tender shall not start unless all required readiness prerequisites pass:

- Cashier is authenticated.
- Terminal is registered and assigned to the correct Site and Site Group.
- Work shift is valid for the cashier and terminal.
- One active cash-custody session exists for the cashier terminal.
- Local operational database is available, writable, and healthy.
- Current payable basis is not expired.
- Central PMS is available for cash-payment command and readback.
- POS Server or the approved fiscal path is available.
- Required fiscal receipt payload/readiness path is available.
- The terminal is not already processing an unresolved cash tender for the same payable basis or parking session.

Cash shall not be accepted when Central PMS is unavailable, when POS Server or the fiscal path is unavailable, when the payable basis is expired, or when the local database is unavailable or unhealthy.

Cash drawer availability shall not be a prerequisite unless cash-drawer integration is explicitly enabled for that terminal.

## 10. Core Business Workflow

The Cashier-Assisted Terminal shall support this normal staffed cash workflow:

1. Cashier signs in to an authorized terminal.
2. Terminal establishes cashier, authenticated session, work shift, terminal, Site, Site Group, and POS Server context.
3. Cashier starts or resumes the active cash-custody session.
4. Cashier scans or manually enters the customer reference.
5. Terminal requests session lookup and payable basis through Central PMS.
6. Central PMS uses the approved Vendor PMS path and returns the accepted payable basis or a blocked/error state.
7. Terminal displays session, payable basis, tariff expiry, and readiness state.
8. If the payable basis is expired, terminal blocks cash tender.
9. Cashier selects cash tender only after readiness passes.
10. Cashier enters amount tendered and required denominations.
11. Cashier attests physical cash receipt.
12. Terminal durably commits `CASH_RECEIVED`.
13. Terminal submits the cash-payment command to Central PMS using durable idempotency identity.
14. Central PMS determines canonical payment state and payment finality.
15. Central PMS routes fiscal issuance to POS Server after payment finality.
16. POS Server owns fiscal document issuance and receipt fiscal content.
17. Terminal prints the approved fiscal payload where available and journals local print result.
18. Terminal displays payment, fiscal, and exit status returned by backend authority.

## 11. Cash Tender Requirements

| ID | Requirement |
| --- | --- |
| CASH-001 | The Cashier-Assisted Terminal shall support cash tender only after all operating preconditions pass. |
| CASH-002 | The terminal shall block cash tender when the payable basis is expired. |
| CASH-003 | The terminal shall block duplicate cash tender while a prior tender for the same parking session or payable basis is pending, unknown, confirmed, fiscal-pending, print-pending, or under reconciliation. |
| CASH-004 | The terminal shall record amount tendered and change given for each cash tender. |
| CASH-005 | Denomination capture shall be mandatory for opening and closing counts. |
| CASH-006 | Denomination capture shall be mandatory per transaction during controlled UAT unless later changed through an approved decision. |
| CASH-007 | The terminal shall calculate change due from backend-accepted payable basis and cashier-entered amount tendered. |
| CASH-008 | The terminal shall not accept cash for expired, ambiguous, inactive, not found, blocked, or unsafe session/payable states. |
| CASH-009 | The terminal shall use the same durable idempotency identity for retry of the same cash-payment command after timeout or restart. |
| CASH-010 | The terminal shall read back payment state before unsafe retry when backend status is unknown. |

## 12. Physical Cash-Custody Rules

`CASH_RECEIVED` is the irreversible physical cash-custody point.

Mandatory rules:

- Cash receipt shall require cashier attestation.
- Cash receipt shall require durable local `CASH_RECEIVED` commit before remote Central PMS submission.
- Historical cash events shall not be deleted or rewritten after `CASH_RECEIVED`.
- Any return, refund, correction, reversal, or adjustment after `CASH_RECEIVED` shall be recorded as a new append-only event.
- Opening a cash drawer shall never mean cash was received.
- Pressing a cash action without cashier attestation and durable local commit shall not establish physical cash custody.

`CASH_RECEIVED` shall capture enough business context to reconstruct who received what cash, for which accepted payable basis, at which terminal, under which cashier, authenticated session, work shift, cash-custody session, Site, Site Group, POS Server, and correlation reference.

## 13. Cashier Shift and Cash-Custody Session Requirements

Cashier shift and cash-custody session are separate business objects.

The MVP posture shall allow one active cash-custody session per cashier terminal.

A cash-custody session shall bind:

- Cashier.
- Authenticated cashier session.
- Work shift.
- Terminal.
- Site.
- Site Group.
- POS Server.
- Opening cash balance.
- Opening denomination count.
- Cash tenders.
- Change given.
- Refunds.
- Paid-outs.
- Safe drops or cash turnovers.
- Adjustments.
- Closing cash count.
- Overage or shortage.

Shift close shall identify pending transactions, unknown payment states, fiscal-pending cases, print-failed cases, unresolved refunds, overage, and shortage.

## 14. Local Operational Database Business Requirements

The Cashier-Assisted Terminal shall have a durable local operational database before real cash tendering.

SQLite with EF Core is the approved local operational database baseline. Approved encryption is required before controlled UAT with real cash.

The local operational database shall:

- Durably record terminal-local physical cash facts.
- Durably record cash-custody session activity.
- Durably record local command attempts and idempotency identity.
- Durably record restart and recovery evidence.
- Durably record local print attempts and outcomes.
- Durably record optional enabled device events.
- Preserve append-only cash history after `CASH_RECEIVED`.
- Support recovery after terminal restart.
- Fail cash tender closed when unavailable, unwritable, unhealthy, or unsafe.

This BRD does not define database tables, columns, migrations, indexes, retention implementation, encryption provider, or file layout.

## 15. Payment and Central PMS Requirements

Central PMS owns accepted payable basis, canonical payment state, and payment finality.

Requirements:

| ID | Requirement |
| --- | --- |
| PAY-001 | The terminal shall submit cash-payment facts to Central PMS through an approved backend contract. |
| PAY-002 | The terminal shall not declare payment finality. |
| PAY-003 | The terminal shall display Central PMS payment state without converting local cash evidence into canonical finality. |
| PAY-004 | The terminal shall preserve the same idempotency identity across retry for the same semantic cash-payment command. |
| PAY-005 | The terminal shall block duplicate cash tender while payment state is pending or unknown. |
| PAY-006 | The terminal shall require readback or reconciliation when Central PMS status cannot be determined. |
| PAY-007 | The terminal shall fail closed for new cash tender when Central PMS is unavailable. |

Physical cash custody is not the same as canonical payment finality. Central PMS determines whether a locally recorded cash tender becomes canonical payment state.

## 16. Fiscal and Receipt Requirements

POS Server owns fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, and fiscal voids.

Requirements:

| ID | Requirement |
| --- | --- |
| FIS-001 | The terminal shall not issue fiscal documents independently. |
| FIS-002 | The terminal shall not allocate fiscal numbers. |
| FIS-003 | The terminal shall not create independent fiscal truth. |
| FIS-004 | The terminal shall obtain fiscal status and approved receipt/Sales Invoice payload through Central PMS unless a later approved narrow contract changes that boundary. |
| FIS-005 | POS Server shall own the fiscal receipt or Sales Invoice payload or rendering template. |
| FIS-006 | APT shall only print the approved fiscal payload and journal the local print result. |
| FIS-007 | Receipt printing shall be journaled separately from fiscal truth. |
| FIS-008 | Payment confirmed but fiscal missing shall remain incomplete for normal exit flow. |
| FIS-009 | The terminal shall not show exit authorized when fiscal issuance is missing, failed, pending, or unknown. |

## 17. Failure and Recovery Requirements

The Cashier-Assisted Terminal shall handle failures conservatively.

| Failure | Required behavior |
| --- | --- |
| Local database unavailable or unhealthy | Block cash tender. |
| Payable basis expired | Block cash tender and request approved refresh/recalculation path. |
| Central PMS unavailable | Block cash tender. |
| POS Server or fiscal path unavailable | Block cash tender. |
| Restart before `CASH_RECEIVED` | Recover local intent and allow safe cancellation or restart of tender if no cash custody was recorded. |
| Restart after `CASH_RECEIVED` | Recover cash receipt evidence and resume readback/submission/reconciliation without duplicating tender. |
| Central PMS submission timeout | Preserve idempotency identity and read back before unsafe retry. |
| Payment confirmed but fiscal missing | Keep transaction incomplete and route to fiscal-pending/exception workflow. |
| Fiscal exists but print failed | Preserve fiscal truth and journal print failure separately. |

`CASH_RECEIVED` shall survive restart. The same idempotency identity shall survive retry.

## 18. Refund, Return, Correction, and Void Requirements

Refunds after canonical payment confirmation shall require supervisor authorization and an approved Central PMS workflow.

Fiscal voids remain governed by Central PMS and POS Server.

Requirements:

- Before `CASH_RECEIVED`, cashier may cancel the tender if no physical cash custody has been recorded.
- After `CASH_RECEIVED`, cancellation shall not erase the cash receipt event.
- Cash returned before canonical confirmation shall be recorded as an append-only local event and reconciled.
- Cash refund after canonical payment confirmation shall require supervisor authorization and approved backend workflow.
- Fiscal void requests shall not be executed directly by APT against POS Server unless a later approved narrow contract explicitly permits a bounded action.
- Corrections and adjustments shall preserve original cash history and add a new event.

## 19. Reconciliation and Exception Requirements

The terminal shall support reconciliation across:

- Local cash journal.
- Cash-custody session.
- Central PMS payment state.
- POS Server fiscal document state.
- Receipt print result.
- Shift close.
- Overage and shortage.
- Refunds, paid-outs, safe drops, cash turnovers, and adjustments.

The terminal shall classify or expose enough evidence for these exception categories:

| Category | Meaning |
| --- | --- |
| `cash_received_payment_missing` | Local cash received, no Central PMS confirmation. |
| `payment_state_unknown` | Central PMS state cannot be proven. |
| `payment_confirmed_fiscal_missing` | Payment confirmed, fiscal document missing or pending. |
| `fiscal_recorded_print_failed` | Fiscal document exists, local print failed. |
| `duplicate_local_tender` | Duplicate tender attempt detected locally. |
| `shift_close_pending_cash` | Shift close attempted with pending cash items. |
| `cash_custody_overage` | Actual closing cash exceeds expected cash. |
| `cash_custody_shortage` | Actual closing cash is less than expected cash. |
| `local_db_corruption` | Local storage health is unsafe. |
| `sync_gap` | Local and backend submission/reconciliation evidence diverge. |

Overage and shortage shall be explicit and reviewable.

## 20. Device and Hardware Requirements

Cash-drawer integration is optional and configurable per terminal or site. The default posture is equivalent to `CASH_DRAWER_ENABLED=false`.

Cash tendering shall work without electronic cash-drawer hardware:

- Cash drawer hardware is not required to start a cash-custody session.
- Cash drawer hardware is not required to reach `CASH_RECEIVED`.
- When cash-drawer integration is disabled, no drawer commands or drawer evidence are expected.
- Opening a drawer never means cash was received.

When cash-drawer integration is enabled:

- Drawer requests, results, faults, timeouts, and no-sale opens may be recorded as supplemental local evidence.
- Drawer evidence shall not alter the `CASH_RECEIVED` custody rule.
- Hardware-in-loop validation shall be required for the enabled drawer integration before controlled use.

A local .NET device service is required only when an enabled or confirmed peripheral needs reliable integration. Such peripherals may include cash drawer, receipt printer, serial scanner, customer display, payment device, or secure device evidence capture.

## 21. Security, Privacy, Fraud, and Segregation of Duties

Security and fraud requirements:

- Cashier authentication shall be required before cash activity.
- Terminal identity and Site/Site Group/POS Server binding shall be required.
- Supervisor authorization shall be required for governed refunds after confirmation and for material cash-custody adjustments.
- Cashier, supervisor, support, finance, and administrator duties shall remain segregated.
- Support access shall not allow deletion or rewriting of historical cash events.
- Local operational database encryption shall be approved before controlled UAT with real cash.
- Terminal diagnostics shall redact sensitive customer, cashier, payment, and fiscal details where possible.
- The terminal shall collect only information needed for approved cashier workflow, cash custody, fiscal printing, reconciliation, and audit.

Operator Console can supervise but cannot collect cash.

## 22. Audit and Traceability Requirements

The Cashier-Assisted Terminal shall support reconstruction of:

- Cashier and authenticated session.
- Work shift.
- Cash-custody session.
- Terminal, Site, Site Group, and POS Server.
- Customer reference and backend parking-session reference.
- Accepted payable basis and tariff expiry.
- Cash tender amount, denominations, and change.
- `CASH_RECEIVED` event and timestamp.
- Local command attempts and idempotency identity.
- Central PMS payment state.
- POS Server fiscal document reference and receipt payload reference.
- Print attempt and print outcome.
- Refund, paid-out, safe drop, cash turnover, adjustment, overage, shortage, and close evidence.
- Optional enabled device events.
- Restart and recovery evidence.

Traceability shall distinguish terminal-local evidence from canonical business authority.

## 23. Non-Functional Requirements

| Area | Requirement |
| --- | --- |
| Availability | The terminal shall block unsafe cash tender instead of accepting cash when required dependencies are unavailable. |
| Reliability | Cash receipt and command identity shall survive restart after durable commit. |
| Usability | Cashier flows shall be clear enough for repeated staffed-lane operations. |
| Performance | Lookup, readiness, tender, confirmation, fiscal status, and print status shall meet operationally acceptable targets defined later. |
| Recoverability | Unknown payment and fiscal states shall be recoverable through readback or reconciliation. |
| Auditability | Terminal-local facts and backend authority facts shall be correlated end to end. |
| Privacy | Sensitive evidence and identifiers shall be minimized and protected. |
| Configurability | Cash-drawer integration shall be configurable per terminal or site and disabled by default. |

## 24. External Dependencies and Interfaces

External dependencies:

| Dependency | Business dependency |
| --- | --- |
| Vendor PMS | Raw parking session and tariff truth. |
| Central PMS | Accepted payable basis, cash-payment command/readback, canonical payment state, payment finality, fiscal reference status, exit authorization status. |
| Payment Orchestrator | Provider execution and verified provider outcomes for non-cash or provider-backed flows. |
| POS Server | Fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, fiscal voids, and approved receipt payload/rendering template. |
| Gate Integration | Gate command execution under Central PMS authorization. |
| Operator Console | Supervision, review, approvals, exception visibility, and governance without cash collection. |
| Local operational database | Durable terminal-local cash, command, device, print, restart, and recovery facts. |
| Optional local device service | Reliable integration for enabled peripherals that require it. |

## 25. Acceptance Criteria

| ID | Acceptance criterion |
| --- | --- |
| AC-001 | Cash cannot start unless readiness prerequisites pass. |
| AC-002 | Expired tariff blocks cash tender. |
| AC-003 | `CASH_RECEIVED` requires cashier attestation and durable local persistence. |
| AC-004 | `CASH_RECEIVED` survives restart. |
| AC-005 | Duplicate cash tender is blocked. |
| AC-006 | The same idempotency identity survives retry. |
| AC-007 | Central PMS remains payment-finality authority. |
| AC-008 | Payment confirmed but fiscal missing remains incomplete. |
| AC-009 | Receipt printing is journaled separately from fiscal truth. |
| AC-010 | Shift close identifies pending transactions. |
| AC-011 | Overage and shortage are explicit. |
| AC-012 | Cash tender works with cash-drawer support disabled. |
| AC-013 | Enabled drawer events remain supplemental evidence. |
| AC-014 | Operator Console can supervise but cannot collect cash. |
| AC-015 | APT cannot command gates. |
| AC-016 | APT cannot allocate fiscal numbers. |

## 26. Assumptions, Constraints, and Risks

Assumptions:

- Current staffed sites use ordinary cashier PCs without electronic cash drawers.
- Central PMS, POS Server, and approved backend workflows are available for normal cash tender.
- Cash tender with real cash will not begin before local durability is implemented.
- Fiscal issuance is required before normal exit authorization.

Constraints:

- APT shall not call databases directly.
- APT shall not calculate or extend tariffs locally.
- APT shall not declare payment finality.
- APT shall not allocate fiscal numbers or create fiscal truth.
- APT shall not command gates.
- APT shall not become Operator Console.
- This BRD does not define schema, DTO, endpoint, migration, or device-service implementation details.

Risks:

| Risk | Mitigation |
| --- | --- |
| Cash accepted without durable local evidence | Block cash if local database is unavailable or unhealthy. |
| Duplicate cash after restart | Preserve idempotency identity and require readback before unsafe retry. |
| Fiscal pending after payment finality | Keep transaction incomplete and visible for fiscal exception handling. |
| Drawer assumptions block ordinary PCs | Keep cash-drawer integration optional and disabled by default. |
| Operator Console scope creep | Preserve non-payment boundary and require approved workflows for supervision only. |

## 27. Deferred Items

Deferred items:

- Local operational database schema, migrations, retention implementation, backup, and corruption recovery design.
- Exact encryption technology and key management implementation, while preserving encryption requirement before controlled UAT with real cash.
- Central PMS cash-payment command, idempotency, readback, and shift/cash-custody validation contracts.
- POS Server receipt/Sales Invoice payload, print, original/reprint, fiscal void, and fiscal adjustment contracts.
- Exact local device-service design and enabled peripheral integration design.
- Cash tender state machine implementation details beyond the business rules in this BRD.
- Operator Console supervisory views and approval workflow implementation details.
- Exact permission matrix for cashier, supervisor, support, finance, and administrator roles.
- Continuity Terminal authority and implementation decisions.

## 28. Requirements Traceability Summary

| Requirement area | Source decision or baseline |
| --- | --- |
| Cashier-Assisted Terminal product definition | ADR-0001 and cash-register design gap analysis v1.1 |
| Authority boundaries | ExitPass BRD v1.3, ExitPass System Design v1.3, ADR-0001 |
| Local physical cash and device fact authority | ADR-0001 and cash-register design gap analysis v1.1 |
| Cash-custody session model | ADR-0001 and cash-register design gap analysis v1.1 |
| `CASH_RECEIVED` custody point | ADR-0001 and cash-register design gap analysis v1.1 |
| SQLite with EF Core baseline | Cash-register design gap analysis v1.1 |
| Optional cash-drawer posture | ADR-0001 and cash-register design gap analysis v1.1 |
| Central PMS payment authority | ExitPass BRD v1.3, ExitPass System Design v1.3 |
| POS Server fiscal authority | POS/Invoicing BRD, POS Server API and Central PMS integration contracts |
| Operator Console non-payment boundary | Operator Console BRD and ExitPass authority baselines |
| Current implementation limitations | APT repository README and current terminal shell baseline |
