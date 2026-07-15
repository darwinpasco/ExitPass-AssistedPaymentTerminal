# ADR-0001: Mode 1 Cash-Register Authority Boundary

## Status

Accepted

## Date

2026-07-15

## Context

ExitPass Assisted Payment Terminal (APT) Mode 1 is moving from a visible cashier-assisted terminal shell toward cash tender support. Cash tender introduces physical custody, drawer, printer, device, restart-recovery, and local audit evidence that cannot safely exist only in memory or in transient UI state.

Existing ExitPass authority boundaries still apply:

- Vendor PMS owns raw parking-session and tariff truth.
- Central PMS owns accepted payable basis, canonical payment state, payment finality, fiscal reference recording, and exit authorization decisions.
- Payment Orchestrator owns provider execution and verified provider outcomes.
- POS Server owns fiscal documents, fiscal numbering, fiscal records, and controlled fiscal voids.
- Gate Integration owns gate command execution under Central PMS authorization.

Mode 1 therefore needs a durable terminal-local operational database before cash tender implementation. That local durability is for physical, device, and operational evidence. It must not silently replace business, payment, tariff, fiscal, exit, or gate authority.

APT remains a separate product and codebase from Operator Console. Operator Console may supervise approved operational states and exceptions, but APT must not become an Operator Console administrative surface.

## Decision

Mode 1 is classified as the ExitPass Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for staffed parking operations.

APT local storage may own and durably record terminal-local physical and device facts. It must not become authoritative for canonical parking, tariff, payable, payment-finality, provider, fiscal, exit, or gate facts.

APT should communicate with POS Server through Central PMS unless a later explicitly approved contract creates a narrower direct boundary. Any such later boundary must preserve POS Server fiscal authority and Central PMS payment and exit authority.

Audit evidence recorded by APT is evidence. It does not silently replace the system of record for business authority.

## Preferred Mode 1 Product Definition

"Assisted Payment Terminal Mode 1 is the ExitPass Windows fixed-station Cashier POS Client and computerized Cash Register Terminal for staffed parking operations. It records terminal-local physical cash and device facts in a durable local operational database, while Central PMS retains canonical payment authority and POS Server retains fiscal authority."

## Authority Matrix

| Area | Authority | APT posture |
| --- | --- | --- |
| Raw parking-session truth | Vendor PMS | Display only from approved backend workflow. |
| Tariff truth | Vendor PMS | Display only; no local tariff calculation or extension. |
| Accepted payable basis | Central PMS | Display and reference only. |
| Canonical payment state and finality | Central PMS | Submit approved terminal facts and display backend state; do not declare finality. |
| Provider execution and verified provider outcomes | Payment Orchestrator | No direct provider-outcome authority. |
| Fiscal documents, numbering, records, and controlled fiscal voids | POS Server | Display/print approved fiscal output only through approved backend flow. |
| Exit authorization | Central PMS | Display only. |
| Gate control | Gate Integration under Central PMS authorization | No gate command authority. |
| Terminal-local physical cash and device facts | APT local operational database | Durable local authority for evidence of what happened at the terminal. |
| Operator governance and administrative workflows | Operator Console and approved backend services | Separate product boundary; APT is not an administrative console. |

## Terminal-Local Authoritative Facts

APT may own and durably record terminal-local facts including:

- Physical cash receipt.
- Amount tendered.
- Denomination capture.
- Change given.
- Cash drawer activity.
- Printer and device outcomes.
- Local command attempts.
- Restart and recovery evidence.
- Terminal-local cashier and drawer-session activity.

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

- Cash tender implementation must start with durable local operational storage for terminal-local physical, drawer, device, command, and recovery facts.
- The local database is required before accepting cash, but this ADR does not choose database technology, schema, encryption, retention, or migration design.
- Cash workflows must fail closed when local durability is unavailable because physical cash evidence cannot rely on transient UI state.
- Local records must be treated as audit and reconciliation evidence, not as replacements for Central PMS, Vendor PMS, Payment Orchestrator, POS Server, or Gate Integration authority.
- POS/fiscal integration from APT remains mediated through Central PMS unless a later approved contract narrows that boundary without weakening fiscal or payment authority.
- Operator Console remains separate from APT; any supervisory visibility or approvals must be added through explicit contracts and role boundaries.

## Non-Goals

This ADR does not:

- Implement local storage.
- Select database technology.
- Define a local database schema.
- Create migrations.
- Define the cash-tender state machine.
- Define Central PMS APIs.
- Define POS Server receipt APIs.
- Create a device service.
- Modify the terminal shell.
- Modify production code.
- Define or implement Mode 2.

## Required Follow-On Decisions

- Local operational database technology, encryption, retention, backup, corruption handling, and schema.
- Cash-tender state machine, including the physical custody point, cancellation, refund, and recovery behavior.
- Central PMS terminal cash-payment command, idempotency, readback, and shift/drawer validation contracts.
- POS Server fiscal receipt payload, original print, reprint, fiscal void, and fiscal adjustment contracts.
- Whether any narrow direct APT-to-POS Server contract is approved, and what it may do without granting APT fiscal authority.
- Device-service boundary for cash drawer, printer, scanner, and device evidence capture.
- Cashier authentication, drawer-session, shift reconciliation, safe-drop, paid-out, no-sale, overage, and shortage controls.
- Operator Console supervisory visibility and approval scope for cash-register operations without payment collection.
- Mode 2 continuity/payment authority decisions, outside this ADR.
