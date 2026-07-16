# Cash-Custody Local Journal Foundation

This component is a non-live local operational journal for the Cashier-Assisted Terminal. It records terminal-local cash-custody evidence through `CASH_RECEIVED`, persists Central PMS cash-payment and fiscal-issuance commands before network submission, and records durable Central PMS readback outcomes. It does not call POS Server directly, render or print receipts, issue exit authorization, command gates, or operate hardware.

The default SQLite database path resolves under the current Windows per-application local-data directory:

```text
%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db
```

Development and tests may pass an explicit database path override. Database files must not be created in or committed to the repository.

`CASH_RECEIVED` is authoritative only for the terminal-local physical custody fact. Central PMS confirmation is required for canonical payment status. Central PMS fiscal readback is local evidence of the fiscal workflow state, while POS Server remains authoritative for fiscal document creation and numbering.

This slice does not implement database-at-rest encryption or protected key material. Approved encryption remains required before controlled UAT with real cash.

Runtime proof:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CashJournalDurabilityProof.ps1
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CentralPmsCashSubmissionOutboxProof.ps1
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CentralPmsCashFiscalOutboxProof.ps1
```
