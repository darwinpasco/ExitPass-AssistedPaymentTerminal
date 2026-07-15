# ADR-0001: Cashier-Assisted Terminal Authority Boundary

## Status

Accepted

## Date

2026-07-15

## Context

The Assisted Payment Terminal is the product family. The Cashier-Assisted Terminal is the Windows fixed-station cashier POS client and computerized cash register for normal staffed parking operations. It is separate from Operator Console, which remains a non-payment governance and supervisory surface.

Cash tendering introduces terminal-local physical cash custody, cashier attestation, cash-accounting, device outcomes, restart recovery, and local audit evidence. Those facts need durable terminal-local storage before real cash tendering. That local durability is for physical, device, and operational evidence; it must not silently replace business, parking, payment, fiscal, exit, or gate authority.

Current parking-site cashiers use ordinary PCs without electronic cash-drawer hardware. The Cashier-Assisted Terminal therefore must support cash tendering without electronic drawer integration while preserving an optional cash-drawer path for future terminals or sites.

The Continuity Terminal is a separate future BCP/degraded-operation terminal. It is outside this ADR. Shared backend contracts may exist, but shared application implementation is not assumed.

## Decision

The Cashier-Assisted Terminal is classified as the ExitPass Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations.

APT local storage may own and durably record terminal-local physical cash and device facts. It must not become authoritative for canonical parking, tariff, payable, payment-finality, provider, fiscal, exit, or gate facts.

APT should communicate with POS Server through Central PMS unless a later explicitly approved contract creates a narrower direct boundary. Any such later boundary must preserve POS Server fiscal authority and Central PMS payment and exit authority.

Audit evidence recorded by APT is evidence. It does not silently replace the system of record for business authority.

## Preferred Product Definition

"The ExitPass Cashier-Assisted Terminal is a Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for normal staffed parking operations. It records terminal-local physical cash and device facts in a durable local operational database, while Central PMS retains canonical payment authority and POS Server retains fiscal authority."

## Authority Matrix

| Area | Authority | APT posture |
| --- | --- | --- |
| Raw parking-session truth | Vendor PMS | Display only from approved backend workflow. |
| Tariff truth | Vendor PMS | Display only; no local tariff calculation or extension. |
| Accepted payable basis | Central PMS | Display and reference only. |
| Canonical payment state and finality | Central PMS | Submit approved terminal facts and display backend state; do not declare finality. |
| Provider execution and verified provider outcomes | Payment Orchestrator | No direct provider-outcome authority. |
| Fiscal documents, fiscal numbering, fiscal records, receipt fiscal content, and controlled fiscal voids | POS Server | Display/print approved fiscal output only through approved backend flow. |
| Exit authorization | Central PMS | Display only. |
| Gate control | Gate Integration under Central PMS authorization | No gate command authority. |
| Terminal-local physical cash and device facts | APT local operational database | Durable local authority for evidence of what happened at the terminal. |
| Operator governance and administrative workflows | Operator Console and approved backend services | Separate non-payment product boundary; APT is not an administrative console. |

## Cash-Custody Model

The cash-custody session is the primary cash-accounting model for the Cashier-Assisted Terminal. Cashier shift and cash-custody session are separate business objects.

A cash-custody session records terminal-local cash-accounting facts including:

- Cashier.
- Authenticated cashier session.
- Work shift.
- Terminal.
- Site.
- Site group.
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

`CASH_RECEIVED` remains the irreversible physical cash-custody point. Cash receipt requires cashier attestation and a durable local `CASH_RECEIVED` commit. Opening a physical drawer, when one exists, does not mean cash was received.

## Optional Cash-Drawer Posture

Cash-drawer integration is optional, configurable, and disabled by default through a business/configuration posture equivalent to `CASH_DRAWER_ENABLED=false`.

Cash tendering must work without electronic cash-drawer hardware:

- Cash-drawer hardware is not a prerequisite to start a cash-custody session.
- Cash tender can be accepted and recorded without drawer integration.
- `CASH_RECEIVED` can be reached without drawer integration.
- When cash-drawer integration is disabled, no drawer commands or drawer device evidence are expected.

When cash-drawer integration is enabled:

- The enabled drawer device may be associated with the cash-custody session.
- Drawer commands, results, faults, timeouts, and no-sale opens may be recorded as supplemental terminal-local device evidence.
- Enabled drawer integration must not alter the `CASH_RECEIVED` custody rule.
- Drawer-open success remains device evidence only, not cash-receipt evidence.

Cash tendering alone does not make the local .NET device service mandatory. A local device service becomes necessary only when a confirmed enabled peripheral requires reliable local integration, such as a cash drawer, receipt printer, serial scanner, customer display, payment device, or secure device evidence capture.

## Terminal-Local Authoritative Facts

APT may own and durably record terminal-local facts including:

- Physical cash receipt.
- Amount tendered.
- Denomination capture.
- Change given.
- Cash-custody session activity.
- Optional enabled cash-drawer activity.
- Printer and device outcomes.
- Local command attempts.
- Restart and recovery evidence.
- Terminal-local cashier activity.

These facts may be submitted to backend services for reconciliation, payment processing, fiscal processing, operational supervision, and audit. Backend acceptance or rejection does not rewrite the fact that the terminal-local physical or device event occurred; it determines the canonical business consequence of that fact.

## Prohibited Local Authority

APT local storage and runtime must not become authoritative for:

- Raw parking-session truth.
- Tariff truth.
- Canonical payable basis.
- Canonical payment finality.
- Provider outcomes.
- Fiscal documents.
- Fiscal numbering.
- Receipt fiscal content.
- Exit authorization.
- Gate control.

APT must not:

- Call databases directly.
- Calculate or extend tariffs locally.
- Declare payment finality.
- Allocate fiscal numbers.
- Create independent fiscal truth.
- Command gates.
- Become an Operator Console administrative surface.

## Consequences

- Cash tender implementation must start with durable local operational storage for terminal-local physical cash, cash-custody, optional device, command, and recovery facts.
- The local database is required before real cash tendering, but this ADR does not choose database technology, schema, encryption, retention, or migration design.
- Cash workflows must fail closed when local durability is unavailable because physical cash evidence cannot rely on transient UI state.
- Cash tendering must remain possible on ordinary cashier PCs without electronic cash-drawer hardware.
- Local records must be treated as audit and reconciliation evidence, not as replacements for Central PMS, Vendor PMS, Payment Orchestrator, POS Server, or Gate Integration authority.
- POS/fiscal integration from APT remains mediated through Central PMS unless a later approved contract narrows that boundary without weakening fiscal or payment authority.
- Operator Console remains separate and non-payment; any supervisory visibility or approvals must be added through explicit contracts and role boundaries.

## Non-Goals

This ADR does not:

- Implement local storage.
- Select database technology.
- Define a local database schema.
- Create migrations.
- Define the cash-tender state machine.
- Define exact configuration files.
- Define Central PMS APIs.
- Define POS Server receipt APIs.
- Create a device service.
- Modify the terminal shell.
- Modify production code.
- Define or implement Continuity Terminal behavior.

## Required Follow-On Decisions

- Local operational database technology, encryption, retention, backup, corruption handling, and schema.
- Cash-tender state machine, including cancellation, refund, and recovery behavior around `CASH_RECEIVED`.
- Central PMS terminal cash-payment command, idempotency, readback, and shift/cash-custody validation contracts.
- POS Server fiscal receipt payload, original print, reprint, fiscal void, and fiscal adjustment contracts.
- Whether any narrow direct APT-to-POS Server contract is approved, and what it may do without granting APT fiscal authority.
- Device-service boundary for optional enabled cash drawer, printer, scanner, customer display, payment device, and secure device evidence capture.
- Cashier authentication, cash-custody session, shift reconciliation, safe-drop/cash-turnover, paid-out, optional no-sale, overage, and shortage controls.
- Operator Console supervisory visibility and approval scope for cash-register operations without payment collection.
- Continuity Terminal authority decisions, outside this ADR.
