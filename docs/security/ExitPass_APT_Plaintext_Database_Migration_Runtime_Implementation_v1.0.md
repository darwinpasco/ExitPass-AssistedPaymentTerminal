# ExitPass APT Plaintext Database Migration Runtime Implementation v1.0

Status: Implementation note for the explicit offline maintenance migration runtime.

The runtime entry point is `tools/AssistedPaymentTerminal.PlaintextDatabaseMigration`. It is an explicit maintenance executable and is not called by normal desktop startup. Normal startup still fails closed for `LegacyPlaintextMigrationRequired`.

## Invocation

Use the maintenance executable under the dedicated APT Windows account:

```powershell
dotnet run --project tools\AssistedPaymentTerminal.PlaintextDatabaseMigration -- --local-db-path <isolated-or-approved-cash-journal.db> --authorize-offline-migration
```

Dry classification:

```powershell
dotnet run --project tools\AssistedPaymentTerminal.PlaintextDatabaseMigration -- --local-db-path <isolated-or-approved-cash-journal.db> --dry-classify
```

Rollback:

```powershell
dotnet run --project tools\AssistedPaymentTerminal.PlaintextDatabaseMigration -- --local-db-path <isolated-or-approved-cash-journal.db> --rollback
```

The normal APT process must be stopped. The operation rejects detected desktop process activity and database or migration-lock contention.

## Contract Alignment

The implementation follows `ExitPass_APT_Plaintext_Database_Migration_Contract_v1.0.md`:

- path resolution reuses `LocalOperationsDatabasePath`;
- SQLCipher and key-envelope behavior reuse J-001 primitives;
- migration is explicit and offline only;
- plaintext source is never encrypted in place;
- backup is created before cutover;
- encrypted target is separate and verified;
- final envelope is prepared only after target verification;
- database and envelope cutover is phased through durable migration state;
- rollback preserves verified plaintext state where safety is proven;
- operator output is safe and does not include keys, protected bytes, raw rows, credentials, or stack traces.

## Limitations

## Deterministic Validation Harness

The validation-only executable is:

```powershell
dotnet run --project tools\AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness --configuration Release -- --acknowledge-validation-only --validation-root D:\Temp\ExitPass\APT-PlaintextMigration-J003
```

The harness is separate from the production maintenance CLI. The production CLI does not expose fault-injection switches, skip-verification switches, forced cutover, envelope override, lock override, or plaintext fallback controls.

The harness:

- requires `--acknowledge-validation-only`;
- requires a caller-supplied disposable validation root;
- rejects repository paths;
- rejects the operational `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal` path;
- creates synthetic plaintext LocalOperations databases through the EF Core model;
- invokes the real migration service;
- injects faults only through an in-process `ILocalDatabasePlaintextMigrationFaultInjector`;
- uses real SQLCipher and real DPAPI `CurrentUser` for same-user scenarios;
- emits sanitized scenario summaries;
- does not print database keys, protected envelope values, connection strings, raw rows, customer data, cashier data, or statutory evidence.

The synthetic fixture profiles cover minimum schema, WAL-committed data, WAL plus SHM posture, unsupported schema, corrupt source, relationship failure, and a full recovery fixture with shift, custody, tender, denomination, payment, fiscal, receipt, print, payable-basis, and statutory recovery state.

## Fault-Injection Design

`NoOpLocalDatabasePlaintextMigrationFaultInjector` is the production default. Tests and the validation harness can inject a one-shot fault scoped to an operation ID, phase, and timing. Supported material phases are:

- `SourceClassified`
- `SourceValidated`
- `BackupStarted`
- `BackupVerified`
- `TargetCreated`
- `ExportStarted`
- `ExportCompleted`
- `TargetVerified`
- `EnvelopePrepared`
- `EnvelopeVerified`
- `CutoverStarted`
- `DatabaseSwitched`
- `EnvelopeSwitched`
- `PostCutoverVerificationStarted`
- `Completed`
- `RollbackStarted`
- `RollbackSourceRestored`
- `RolledBack`

The hook records only phase and safe classification. It does not receive or expose the database key, envelope bytes, SQLCipher connection string, or row data.

## Validation Results

The validation harness passed same-user scenarios for supported migration, WAL preservation, WAL/SHM posture, non-plaintext encrypted header, no-key rejection, same-user envelope reopen, normal encrypted initialization, repeated idempotent invocation, application-running rejection, database lock rejection, missing authorization, migration-lock rejection, existing envelope conflict, existing target conflict, existing backup conflict, unsupported schema, corrupt source, relationship failure, source-change blocking, insufficient disk simulation, deterministic interruption recovery, rollback-required failure, interrupted rollback recovery, and durable operational-state preservation.

The wrong-user DPAPI proof passed as a separate Windows account action. A disposable non-administrator Windows account read the disposable proof artifacts and ran the validation-only read/unprotect verification. The process returned exit code `2` with the safe `KeyEnvelopeWrongIdentity` classification, the creating and verifying safe identity references differed, stderr was empty, no SQLCipher key or envelope bytes were exposed, the proof database was not mutated, and no plaintext fallback occurred.

## Wrong-User DPAPI Procedure

1. Under the primary APT Windows account, run the validation harness `prepare-wrong-user-proof` command in a disposable root and preserve artifacts for the wrong-user scenario.
2. Do not print or copy the protected key envelope contents.
3. Under an approved disposable non-administrator Windows account, run the validation harness `verify-wrong-user-proof` command against the same disposable proof root.
4. Expected result: verification returns the safe `KeyEnvelopeWrongIdentity` classification and a non-success process exit code because DPAPI `CurrentUser` rejects the creating user's envelope.
5. Run the validation harness `cleanup-wrong-user-proof` command after the result is recorded safely.

Do not create, delete, or modify Windows accounts from the migration tool or harness.

Confirmed cleanup evidence after the proof:

- disposable account exists: `False`
- disposable profile exists: `False`
- disposable proof root exists: `False`

## Limitations

All Windows validation scenarios passed for this implementation slice. Plaintext backup retention cleanup, key rotation, terminal replacement recovery, cross-account recovery, controlled UAT, and production rollout remain outside this slice and are not authorized.
