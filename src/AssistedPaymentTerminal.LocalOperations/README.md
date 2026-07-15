# Cash-Custody Local Journal Foundation

This component is a non-live local operational journal for the Cashier-Assisted Terminal. It records terminal-local cash-custody evidence through `CASH_RECEIVED`; it does not submit payment, issue fiscal documents, call POS Server, call Central PMS, or operate hardware.

The default SQLite database path resolves under the current Windows per-application local-data directory:

```text
%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db
```

Development and tests may pass an explicit database path override. Database files must not be created in or committed to the repository.

`CASH_RECEIVED` is authoritative only for the terminal-local physical custody fact. It is not Central PMS payment confirmation and it is not POS Server fiscal authority.

This slice does not implement database-at-rest encryption or protected key material. Approved encryption remains required before controlled UAT with real cash.

Runtime proof:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-CashJournalDurabilityProof.ps1
```
