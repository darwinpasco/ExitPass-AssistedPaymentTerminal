# Cash-Custody Local Journal Foundation

This component is a non-live local operational journal for the Cashier-Assisted Terminal. It records terminal-local cash-custody evidence through `CASH_RECEIVED`, persists Central PMS cash-payment and fiscal-issuance commands before network submission, and records durable Central PMS readback outcomes. It does not call POS Server directly, render or print receipts, issue exit authorization, command gates, or operate hardware.

The default SQLite database path resolves under the current Windows per-application local-data directory:

```text
%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db
```

Development and tests may pass an explicit database path override. Database files must not be created in or committed to the repository.

`CASH_RECEIVED` is authoritative only for the terminal-local physical custody fact. Central PMS confirmation is required for canonical payment status. Central PMS fiscal readback is local evidence of the fiscal workflow state, while POS Server remains authoritative for fiscal document creation and numbering.

This slice does not implement database-at-rest encryption or protected key material. Approved encryption remains required before controlled UAT with real cash.

## Authoritative Sales Invoice Printing

Thermal printing uses an already retrieved, locally stored, integrity-validated POS Server-owned Sales Invoice presentation. The print path must not call Central PMS receipt retrieval, submit another terminal-cash payment, request fiscal issuance, allocate another fiscal number, issue or mutate ExitAuthorization, call HikCentral, command a gate, or trigger a cash drawer.

The local journal durably records print-job evidence separately from receipt preview state. A print job records the terminal cash tender, fiscal document identity and number, presentation and template versions, semantic or payload hash evidence, paper width, configured Windows printer, ORIGINAL or REPRINT classification, local copy sequence, timestamps, safe failure classification, retryability, and any Windows spooler job ID that is safely returned.

The ORIGINAL boundary is successful Windows spooler acceptance, or an unknown-after-submission state after process interruption. Preview does not consume ORIGINAL. Failed preparation before spooler submission does not consume ORIGINAL. Later cashier print attempts for the same fiscal document and presentation identity are REPRINT jobs and must be visibly marked `REPRINTED: yyyy-MM-dd HH:mm` above the `SALES INVOICE` heading using the site-local accepted reprint timestamp, without mutating the authoritative receipt payload or hash.

Supported governed paper widths are 57 mm, 58 mm, and 80 mm. Width changes may affect wrapping, margins, separators, and pagination only; they must not change fiscal facts, totals, statutory wording, fiscal identity, or hash evidence. Unsupported width or missing printer configuration produces a safe configuration-required posture.

Spooler acceptance is local device evidence, not proof of physical paper output. Unknown outcomes after restart are preserved and are not silently resubmitted. Retry is allowed only for retryable printer failures and creates a separate linked attempt.

## Sales Invoice Print History

Print history is read-only visibility over durable `terminal_cash_receipt_print_jobs` records. It exposes local evidence for cashier and supervisor review, including Original/Reprint classification, copy sequence, configured printer, paper width, spooler job ID when available, safe failure classification, retryability, timestamps, support reference, presentation/template versions, and shortened hash evidence.

History and reconciliation indicators are local operational evidence only. They may flag no original spooler evidence, unknown outcomes, latest retryable failure, printer or paper-width changes, duplicate copy sequence, missing evidence, or inconsistent fiscal/presentation identity. They do not derive legal or fiscal conclusions and do not claim physical paper output unless the printer subsystem provides that evidence.

Opening, filtering, or viewing print-history detail must not create a print job, submit to a printer, retrieve a receipt, submit payment, request fiscal issuance, issue or mutate ExitAuthorization, call HikCentral, command a gate, or trigger a cash drawer. Unknown outcomes remain visible as requiring confirmation; this history slice does not resolve, retry, cancel, delete, or repair historical records.

Runtime proof:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CashJournalDurabilityProof.ps1
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CentralPmsCashSubmissionOutboxProof.ps1
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CentralPmsCashFiscalOutboxProof.ps1
```
