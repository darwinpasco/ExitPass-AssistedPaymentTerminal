# ExitPass APT Plaintext Database Migration Contract v1.0

Status: Frozen implementation contract for a later runtime slice. This is documentation-only and does not enable plaintext migration.

Target later branch: `feature/apt-plaintext-database-migration-runtime`.

## 1. Authorization Decision

Normal Cashier-Assisted Terminal startup must continue to fail closed when it detects a legacy plaintext SQLite database. Migration must not run silently during ordinary application startup. A future implementation must use an explicit offline maintenance operation initiated by an authorized installer, administrator, or deployment technician while the normal APT process is stopped.

The migrated target must use the merged encrypted persistence baseline: SQLCipher whole-database encryption, a random database key, a version 1 DPAPI `CurrentUser` protected key envelope, eager startup validation, deterministic readiness classifications, and no key replacement for existing storage.

This contract does not authorize controlled UAT, production rollout, plaintext fallback, key rotation, backup redesign, or terminal replacement recovery.

## 2. Current Baseline

Reviewed merged J-001 evidence:

- `StartupOptions.cs` reads `APT_LOCAL_DB_PATH` and parses `--local-db-path=`. Command-line path takes precedence.
- `MainWindow.xaml.cs` passes the resolved path to `LocalOperationsDatabaseOptions`.
- `LocalPersistenceStartupInitializer.cs` initializes encrypted storage before the operational WebView is exposed.
- `LocalOperationsDatabasePath.cs` resolves `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db` and rejects unresolved placeholders.
- `LocalDatabaseEncryptionManager.cs` initializes SQLCipher, creates or loads the envelope, creates encrypted storage for new installs, validates schema/integrity, detects the plaintext header `SQLite format 3`, and returns fail-closed classifications.
- `LocalDatabaseKeyEnvelope.cs` defines version 1 envelope fields and stores no plaintext key.
- `LocalDatabaseKeyProtection.cs` uses Windows DPAPI `CurrentUser` in production.
- `CashJournalDbContext.cs` uses EF Core SQLite over an opened SQLCipher connection.
- `CashJournalService.cs` owns schema initialization, additive schema maintenance, local state, and restart recovery.
- `LocalJournalBridgeHandler.cs` exposes readiness and durable local state without keys, connection strings, protected envelope bytes, raw SQLite errors, or stack traces.

Current database path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db`.

Current envelope path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.key`.

## 3. Local Table and State Inventory

A future migration must preserve every current table, index, relationship, timestamp, idempotency key, retry marker, correlation reference, semantic hash, and recovery state.

| Table or aggregate | Category | Preservation requirement |
|---|---|---|
| `cashier_shifts` | Cashier shift lifecycle | Preserve open and closed shifts exactly; do not reset or synthesize shifts. |
| `cash_custody_sessions` | Physical custody session | Preserve custody independently from shift; do not fabricate custody. |
| `cash_tenders` | Local tender and CASH_RECEIVED boundary | Preserve tender identity, amount, currency, payable basis, statutory evidence, and immutable custody evidence. |
| `cash_tender_events` | Cash-custody event history | Preserve event ordering and CASH_RECEIVED evidence without duplication. |
| `cash_denomination_entries` | Denomination capture | Preserve counts, attestation, and tender/custody references. |
| `terminal_cash_payment_outbox_commands` | Central PMS payment outbox | Preserve command identity, idempotency, retry, and reconciliation state. |
| `terminal_cash_payment_submission_attempts` | Payment attempt history | Preserve outcomes, classifications, timestamps, and correlation. |
| `terminal_cash_fiscal_outbox_commands` | Fiscal outbox/readback | Preserve fiscal command identity and stage. |
| `terminal_cash_fiscal_attempts` | Fiscal attempt history | Preserve outcomes, classifications, timestamps, and correlation. |
| `terminal_cash_receipt_retrieval_commands` | Receipt retrieval state | Preserve receipt identity, availability, retry, and support posture. |
| `terminal_cash_receipt_retrieval_attempts` | Receipt retrieval attempts | Preserve attempt evidence and classifications. |
| `terminal_cash_receipt_print_jobs` | Print job/history | Preserve original/reprint sequence, print result, printer metadata, and reconciliation indicators. |
| `terminal_cash_payable_basis_states` | Payable-basis/statutory recovery | Preserve original and applied snapshots, amount acknowledgement, readiness, revalidation, statutory references, and support correlation. |
| SQLite/EF metadata | Schema metadata | Preserve or recreate only as part of verified encrypted target creation. |

For each aggregate the runtime must capture row counts, key integrity, relationship integrity, and bounded business-invariant summaries. It must not log row contents, customer data, cashier names, statutory identifiers, tender details, database keys, or envelope bytes.

## 4. Environment Classification Matrix

| Database state | Envelope state | Exact classification | Required posture |
|---|---|---|---|
| Database absent | Envelope absent | `EncryptedFirstTimeCreationEligible` | Existing J-001 encrypted first-time creation. |
| Encrypted database | Valid CurrentUser envelope | `EncryptedStorageReady` | Normal startup after schema and integrity validation. |
| Encrypted database | Envelope absent | `KeyEnvelopeMissing` | Fail closed; do not generate replacement envelope. |
| Encrypted database | Malformed envelope | `KeyEnvelopeMalformed` | Fail closed; preserve artifacts. |
| Encrypted database | Wrong Windows identity envelope | `KeyEnvelopeWrongIdentity` | Fail closed; preserve artifacts. |
| Encrypted database | Wrong key | `EncryptedDatabaseUnreadable` | Fail closed; preserve artifacts. |
| Encrypted database | Corrupt database | `EncryptedDatabaseIntegrityFailed` | Fail closed; preserve artifacts. |
| Plaintext database | Envelope absent | `LegacyPlaintextMigrationRequired` | Normal startup blocked; explicit offline migration eligible. |
| Plaintext database | Valid envelope present | `PlaintextDatabaseWithEnvelopeConflict` | Block until conflict is resolved by maintenance operation. |
| Plaintext database | Malformed envelope present | `KeyEnvelopeMalformed` | Fail closed. |
| Plaintext database | Wrong-identity envelope present | `KeyEnvelopeWrongIdentity` | Fail closed. |
| Database path is a directory | Any | `DatabasePathInvalid` | Fail closed. |
| Database unreadable | Any | `DatabasePathUnreadable` | Fail closed. |
| Database locked by another process | Any | `DatabaseLocked` | Fail closed; retry only after the lock owner is stopped. |
| Unsupported schema version | Any | `UnsupportedPlaintextSchemaVersion` or `UnsupportedEncryptedSchemaVersion` | Fail closed; do not recreate empty storage. |
| Migration state present | Source and target vary | `MigrationInterrupted` with exact phase | Resume, restart safe phase, roll back, or block from state evidence. |

Classification must reflect the earliest proven failure stage and must never be ambiguous.

## 5. Explicit Offline Operation Contract

The future migration operation must be outside normal desktop startup. It may be a dedicated command-line mode or bounded maintenance utility, but it must not be an automatic startup fallback.

Before migration begins it must prove:

- The normal APT process is stopped.
- No other process holds the source database, WAL, SHM, target, backup, envelope, or migration-state files.
- An exclusive migration lock has been acquired; duplicate migration processes are rejected.
- The process runs under the same dedicated Windows account that will operate APT because DPAPI remains `CurrentUser`.
- Source, target, envelope, backup, and migration-state paths resolve under the approved LocalOperations posture unless an explicit administrator configuration authorizes an isolated alternate path.
- No network database or shared UNC path is assumed.
- No cash acceptance, CASH_RECEIVED, terminal-cash submission, fiscal issuance/readback, receipt retrieval, printing, ExitAuthorization, HikCentral, WebPay, or gate activity occurs during migration.

The operation must prove the normal APT process is stopped by process identity and exclusive file/database access. It must not rely on a friendly UI state alone.

## 6. Source Database Validation Contract

Before backup or conversion, validate:

- source exists and is a regular file, not a directory, symlink, junction, or unexpected reparse point;
- source is readable and parent directory is writable for same-volume temporary artifacts;
- source has the plaintext SQLite header and is not encrypted storage;
- `PRAGMA integrity_check` passes on a read-only plaintext connection;
- foreign-key checks pass where supported;
- schema version exists and is supported;
- required table inventory matches the expected local schema;
- unsupported attached databases are absent;
- free disk space is sufficient for source snapshot, backup, target, state files, and rollback artifacts;
- source size and SHA-256 are captured;
- critical-state queries find no duplicate or contradictory active shifts, custody, tenders, payment, receipt, print, or payable-basis state beyond existing application recovery rules.

The runtime must not modify the plaintext source to make it migratable. Unsupported or inconsistent sources remain preserved and fail closed.

## 7. WAL and SHM Posture

The future implementation must not assume that copying only `cash-journal.db` is sufficient. If WAL mode is active or `cash-journal.db-wal` or `cash-journal.db-shm` exists, the runtime must obtain a complete committed source snapshot without losing committed WAL content.

Required posture:

- stop normal APT first;
- open the plaintext source through SQLite with exclusive migration ownership;
- inspect journal mode and WAL/SHM presence;
- use a SQLite-supported backup, vacuum/export, or equivalent consistent-copy method that includes committed WAL content;
- capture source main, WAL, and SHM size/hash evidence where present;
- do not delete WAL/SHM files until backup and cutover evidence prove safety.

## 8. Migration Strategy Decision

Selected strategy: SQLCipher-native export into a separately created encrypted target.

The runtime must create a separate encrypted target with the merged SQLCipher provider and a newly generated random database key, copy all schema and data from the validated plaintext source, verify the target before cutover, then activate the encrypted database and DPAPI envelope as a logical pair.

Reasons selected:

- never encrypts the only source in place;
- uses the same provider and keying behavior as production startup;
- preserves SQLite data types, indexes, metadata, and relationships better than repository-level row mapping;
- permits verification before cutover;
- permits rollback before final completion.

Rejected strategies:

- SQLite backup API followed by in-place conversion: rejected because it blurs source and target authority and risks overwriting the only valid source.
- Row-by-row copy through application repositories: rejected because it can miss future tables, indexes, metadata, ordering, and recovery state.
- In-place rekey or conversion of the plaintext file: rejected because the source must never be encrypted in place or become the only usable copy during conversion.

## 9. Key-Envelope Timing

Freeze this order:

1. Validate source and acquire migration lock.
2. Create and verify plaintext backup or consistent source snapshot.
3. Generate the random SQLCipher database key in memory.
4. Create the encrypted target under a temporary filename.
5. Copy schema and data into the encrypted target.
6. Verify the SQLCipher target with the in-memory key.
7. Protect the key with DPAPI `CurrentUser` using the same database identity derivation that normal startup will use after cutover.
8. Write the envelope to a temporary file in the same directory and flush durably where practical.
9. Validate envelope unprotection under the current user.
10. Reopen the target using only the persisted envelope path and target path.
11. Prepare cutover state.
12. Atomically activate database and envelope as a logical pair through the state machine.
13. Run post-cutover startup verification.
14. Mark migration completed only after verification passes.

Dangerous states to prevent:

- replacing a valid envelope;
- creating a new envelope for an existing encrypted database;
- leaving a final envelope that points to an unverified target;
- deleting the only usable envelope;
- logging the unprotected key;
- accepting a target that can only open while the migration process still holds the key;
- creating the envelope under a different Windows identity than the runtime identity.

## 10. Backup Posture

Backups must live under a controlled LocalOperations maintenance directory, for example `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\MigrationBackups\<operation-id>\`, unless a deployment-approved isolated path is configured.

Record for every backup:

- file name, byte size, SHA-256, schema version, integrity-check result, table row-count summary, operation ID, and timestamp;
- WAL and SHM companion evidence when those files participate in the committed source snapshot.

Plaintext backups remain sensitive. Require restricted ACLs, encrypted Windows volume posture, bounded retention, approved deletion procedure, no cloud synchronization, no logs or ticket uploads, and no support-bundle inclusion by default.

Ordinary file deletion is not guaranteed secure erasure on SSD storage. Do not silently delete the plaintext backup immediately after cutover. Deletion becomes eligible only after post-cutover verification and operator authorization under deployment policy.

## 11. Migration State Machine

The migration state file must be durable, non-secret, atomically written, and stored under the controlled LocalOperations maintenance directory with restrictive ACLs. It must contain no database key, credentials, customer data, cashier data, database rows, tender details, or envelope content.

| Phase | Required artifacts | Must not exist as final accepted artifacts | Next transition | Restart and rollback posture | Normal startup |
|---|---|---|---|---|---|
| `NOT_STARTED` | Legacy plaintext source. | Final encrypted database accepted as ready. | `PREFLIGHT_VALIDATED` | Retry preflight. | Blocked as legacy plaintext. |
| `PREFLIGHT_VALIDATED` | Lock and source evidence. | Final envelope. | `SOURCE_BACKUP_VERIFIED` | Restart preflight if lock lost. | Blocked. |
| `SOURCE_BACKUP_VERIFIED` | Verified backup and source hashes. | Final encrypted database. | `TARGET_CREATED` | Reuse backup only if source hash unchanged. | Blocked. |
| `TARGET_CREATED` | Encrypted temporary target. | Final cutover pair. | `DATA_COPIED` | Quarantine unverified target and recreate if needed. | Blocked. |
| `DATA_COPIED` | Target has copied schema/data. | Completion marker. | `TARGET_VERIFIED` | Recopy from verified snapshot if target evidence is incomplete. | Blocked. |
| `TARGET_VERIFIED` | Integrity, row counts, and invariants pass. | Final envelope without state. | `CUTOVER_PREPARED` | Quarantine target or roll back to source posture. | Blocked. |
| `CUTOVER_PREPARED` | Valid temp envelope and target reopened from envelope. | Completion marker. | `CUTOVER_COMMITTED` | Resume cutover or block on pair mismatch. | Blocked. |
| `CUTOVER_COMMITTED` | Final encrypted database and final envelope active as a logical pair. | Plaintext source at active path. | `POST_CUTOVER_VERIFIED` | Run startup verification; rollback only if source backup is proven. | Fail closed until verified. |
| `POST_CUTOVER_VERIFIED` | Startup validation evidence. | Duplicate replacement database. | `COMPLETED` | Retry verification if no mutation occurred. | May start after readiness passes. |
| `COMPLETED` | Completion marker and audit evidence. | Temp artifacts as active files. | None | Repeated invocation reports already migrated. | Normal encrypted startup. |
| `ROLLBACK_REQUIRED` | Failure evidence and verified source/backup. | Destructive deletion of only valid source. | `ROLLED_BACK` or `BLOCKED` | Operator or automatic rollback according to evidence. | Blocked. |
| `ROLLED_BACK` | Plaintext source restored or preserved. | Completion marker. | `NOT_STARTED` or exit. | Reattempt only after operator approval. | Blocked as legacy plaintext. |
| `BLOCKED` | Safe failure evidence. | Automatic mutation. | Operator intervention. | No retry until blocker is resolved. | Blocked. |

## 12. Interrupted Migration Recovery Matrix

| Interruption point | Exact action |
|---|---|
| Before backup | Resume preflight if source hash and lock can be proven. |
| During backup | Discard incomplete backup and restart backup from unchanged source. |
| After backup before target creation | Reuse verified backup only if source hash still matches; otherwise block. |
| During target creation | Quarantine incomplete target and recreate from verified source snapshot. |
| During data copy | Quarantine target and repeat copy. |
| After target copy before verification | Verify target; roll back to verified source posture if verification fails. |
| During verification | Repeat verification only if no target mutation occurred. |
| After target verification before envelope write | Create temp envelope for the verified target. |
| After envelope write before cutover | Reopen target using temp envelope, then resume cutover or block on mismatch. |
| During source/target rename | Inspect state, final paths, backup hashes, and pair evidence; resume or require operator intervention. |
| After database cutover before envelope cutover | Fail closed; complete envelope cutover only when target hash and temp envelope pair are proven. |
| After both cutovers before startup verification | Run post-cutover verification before completion. |
| After startup verification before completion marker | Write completion marker if hashes and readiness still match. |
| During rollback | Resume rollback only from state evidence; otherwise block. |

The future runtime must never infer recovery from timestamps alone.

## 13. Atomic Cutover Contract

Windows cannot atomically replace the database and envelope as a two-file pair. The state machine makes every intermediate pair fail closed and recoverable.

Cutover uses:

- source database path: final `cash-journal.db`;
- source quarantine path: operation-owned backup/quarantine directory;
- temporary encrypted target path: same volume as final database;
- final encrypted database path: final `cash-journal.db`;
- temporary envelope path: same directory as final envelope;
- final envelope path: final `cash-journal.key`;
- migration-state path: operation-owned maintenance state file.

Use same-volume rename/replace operations and flush file writes where practical. Record `CUTOVER_PREPARED` before any final rename. Normal startup must reject any database/envelope mismatch, including a new encrypted database with an old envelope or a plaintext database with a new envelope.

## 14. Verification Before Cutover

Before cutover the target must prove:

- opens with the generated SQLCipher key;
- fails to open as plaintext SQLite without the key;
- raw header lacks `SQLite format 3`;
- integrity and foreign-key checks pass;
- schema version is correct;
- table inventory matches expected target schema;
- source and target row counts match;
- business invariants match for shifts, custody, tenders, payment outbox, fiscal outbox, receipt retrieval, print jobs, payable-basis state, statutory recovery state, reconciliation, and retry state;
- target SHA-256 is captured;
- DPAPI envelope unprotects under the current user;
- target reopens using only the persisted envelope path;
- no migration-process in-memory key dependency remains.

## 15. Post-Cutover Verification

The operation must not report `COMPLETED` until bounded encrypted startup verification proves:

- local encrypted storage readiness;
- expected schema version;
- durable shift recovery;
- durable custody recovery;
- payment, fiscal, receipt, print, payable-basis, and statutory recovery state remains available;
- no duplicate records are created;
- no new first-time database replaces migrated data;
- database and envelope hashes remain stable across restart;
- normal cash-readiness rules remain in force.

## 16. Rollback Contract

Rollback must preserve the verified plaintext source or backup, preserve evidence, and remove or quarantine incomplete encrypted artifacts. It must never overwrite the verified source with an unverified target and must never destroy the only valid envelope.

Rules:

- Target creation, copy, or integrity failure: quarantine target; source and backup remain.
- Row-count or invariant mismatch: block or roll back to verified source posture.
- Envelope creation or validation failure: remove or quarantine only the temp envelope; preserve source, backup, and target evidence.
- Cutover failure: finish cutover or restore source only from state evidence; otherwise block.
- First encrypted startup failure: roll back only when verified source backup can be restored safely; otherwise block for support.
- Operator cancellation before cutover: remove or quarantine temp encrypted artifacts and return to legacy plaintext blocked posture.
- Operator cancellation after cutover: require support policy; do not silently undo completed cutover.
- Repeated invocation: follow idempotency rules and avoid duplicate backups unless source hash changed under explicit policy.

## 17. Existing Envelope Conflict Posture

A plaintext database beside an existing envelope is never auto-resolved.

| Envelope condition | Classification | Posture |
|---|---|---|
| Valid but unrelated target evidence | `PlaintextDatabaseWithEnvelopeConflict` | Operator intervention. |
| Malformed | `KeyEnvelopeMalformed` | Fail closed. |
| Cannot unprotect under current identity | `KeyEnvelopeWrongIdentity` | Fail closed. |
| Left by interrupted migration | `MigrationInterrupted` | Follow state machine. |
| Paired with verified temporary target | `MigrationInterrupted` | Resume only if state proves the pair. |
| Origin unknown | `ExistingEnvelopeProvenanceUnknown` | Fail closed. |

## 18. Schema-Version Posture

The runtime task must define supported plaintext source schema versions from the actual merged schema. Unsupported old versions and future newer versions must fail closed. The plaintext source must not be upgraded in place.

Preferred ordering is conversion into an encrypted target matching the supported source schema, followed by controlled schema upgrade on the encrypted target only when the same operation owns verification and rollback. If conversion and schema upgrade are combined, evidence must distinguish copy verification from upgrade verification. Unsupported sources must never be recreated as empty encrypted storage.

## 19. Operator Message Contract

Maintenance output may include safe classification, operation ID, phase, retryability, recovery action, and support reference.

Required classifications/messages include:

- `MigrationRequired`: legacy plaintext storage detected; normal startup remains blocked.
- `PreflightFailed`: path, lock, identity, ACL, or process checks failed.
- `DatabaseInUse`: stop APT and retry.
- `InsufficientDiskSpace`: free space before retry.
- `SourceIntegrityFailed`: preserve files and contact support.
- `UnsupportedSchema`: this runtime cannot migrate the source.
- `BackupFailed`: source preserved; retry after storage issue.
- `TargetCreationFailed`: source and backup preserved.
- `VerificationFailed`: encrypted target rejected; source preserved.
- `WrongWindowsIdentity`: run under the dedicated APT Windows account.
- `ExistingEnvelopeConflict`: support intervention required.
- `InterruptedMigrationDetected`: resume, roll back, or block by phase evidence.
- `RollbackCompleted`: normal startup remains blocked as legacy plaintext until a new migration is approved.
- `RollbackRequired`: support intervention required.
- `MigrationCompleted`: encrypted startup verification passed.

Messages must not include keys, envelope bytes, credentials, secret-bearing connection strings, full customer-facing local paths, raw SQLite exceptions, stack traces, SQL statements, row contents, cashier names, customer data, tender details, statutory identifiers, evidence images, or access tokens.

## 20. Authorization and Audit Evidence

The future runtime must require explicit authorized maintenance invocation and record non-secret evidence:

- migration operation ID;
- terminal ID and site ID where safely configured;
- application version;
- source and target schema versions;
- non-reversible Windows account identity reference, such as a SID hash;
- start and completion timestamps;
- source, backup, target, and envelope SHA-256 values and byte sizes;
- row-count summary;
- integrity-check result;
- completed phases;
- final outcome;
- rollback outcome;
- correlation or support reference.

Do not record raw SID, usernames, database content, database key, envelope content, transaction details, statutory identifiers, personal data, tender details, credentials, or raw local database rows.

Evidence should be stored with the migration state file and retained under deployment policy. Support-package inclusion requires privacy review.

## 21. Idempotency Rules

- Already migrated encrypted database with valid envelope: report already migrated; create no new envelope or database.
- Completed migration state: verify final encrypted readiness and report completed.
- Interrupted state: resume, restart a safe phase, roll back, or block by phase evidence.
- Rolled-back state: require explicit operator confirmation before reattempt.
- Eligible plaintext database with verified backup: reuse backup only when the source hash still matches.
- Plaintext database changed since last attempt: block or start a new approved operation ID.
- Existing target: accept only when state evidence proves it belongs to this operation and hash matches.
- Existing envelope: accept only when state evidence proves it belongs to this operation and unprotects under current user for the target identity.
- Duplicate operation ID: reject unless it is a deterministic retry of the same state and artifact set.

The operation must not create endless backups, duplicate targets, duplicate envelopes, duplicate outbox commands, duplicate tenders, duplicate print jobs, or duplicate local recovery state.

## 22. Threat and Privacy Analysis

| Threat | Mitigation | Remaining risk |
|---|---|---|
| Database key exposure | DPAPI `CurrentUser`, no plaintext key persistence, no key in arguments/logs/UI. | Key exists in process memory while SQLite is open. |
| Plaintext backup exposure | Restricted ACLs, encrypted Windows volume, bounded retention, support-bundle exclusion. | SSD deletion is not guaranteed secure erasure. |
| Malicious file replacement | Canonical paths, regular-file checks, hashes, exclusive lock, state evidence. | Local administrator tampering remains possible. |
| Symlink or junction attack | Reject unexpected reparse points unless explicitly approved. | Requires Windows-specific tests. |
| Path traversal | Resolve under approved LocalOperations root by default. | Administrator alternate paths need extra controls. |
| ACL inheritance failure | Validate restrictive ACL posture and block on broad access where policy requires. | ACLs are defense in depth; DPAPI is primary. |
| Wrong Windows identity | Require dedicated APT Windows account and validate envelope unprotect/startup under it. | Cross-account recovery is outside this slice. |
| Concurrent startup or migration | Process checks, database lock, migration lock. | Race coverage requires Windows tests. |
| Disk-full interruption | Free-space preflight plus state-machine recovery. | Writes can still fail after preflight. |
| Power loss | Atomic writes, same-volume rename, durable phases. | Two-file atomicity is impossible; fail-closed recovery is required. |
| Antivirus lock | Detect lock and block/retry with safe guidance. | Endpoint policy coordination may be required. |
| Rollback tampering | Hash evidence and phase validation. | Administrator-level tampering cannot be fully prevented locally. |
| Stale state replay | Operation ID, hashes, phases, final readiness verification. | Requires strict implementation. |
| Unapproved copy location | Controlled local root and explicit administrator override only. | Operator misuse remains possible. |
| Support-bundle leakage | Exclude backups, databases, envelopes, rows, and protected bytes by policy. | Later tooling must enforce. |
| Accidental cloud backup | Local app-data path plus deployment exclusion policy. | Cloud-sync policy is outside application control. |
| Operator deletion of wrong file | Scripted paths and no destructive wildcard operations. | Manual deletion outside tooling remains possible. |

## 23. Future Automated Test Matrix

The runtime task must cover:

1. valid plaintext migration;
2. plaintext source with committed WAL content;
3. empty plaintext database;
4. source integrity failure;
5. unsupported schema;
6. database locked;
7. insufficient disk space;
8. backup failure;
9. target creation failure;
10. interrupted copy;
11. target integrity mismatch;
12. row-count mismatch;
13. business-invariant mismatch;
14. envelope write failure;
15. envelope validation failure;
16. cutover interruption after database rename;
17. cutover interruption after envelope rename;
18. post-cutover startup failure;
19. rollback success;
20. rollback failure;
21. wrong Windows identity or deterministic protection failure;
22. existing valid envelope conflict;
23. malformed envelope conflict;
24. repeated invocation;
25. already migrated database;
26. open shift preservation;
27. open custody preservation;
28. payment recovery preservation;
29. print-history preservation;
30. database and envelope hash stability after restart;
31. no plaintext header after completion;
32. no automatic empty database replacement;
33. no key or protected material in logs;
34. normal database path isolation;
35. migration-state tampering;
36. path traversal or junction attack;
37. application started during migration;
38. migration started twice.

Windows-only testing is required for DPAPI identity, ACL behavior, process locking, reparse-point handling, file-lock interference, same-volume rename behavior, and published/manual proof.

## 24. Future Windows Manual Test Matrix

The runtime task must require significant Windows manual validation for:

- successful migration of a synthetic plaintext database with shifts, custody, tenders, payable-basis state, payment outbox, fiscal outbox, receipt retrieval, print jobs, and recovery metadata;
- migration with committed WAL content;
- blocked migration while APT is running;
- wrong Windows account behavior;
- existing envelope conflict;
- interrupted migration before target verification;
- interrupted migration during cutover;
- rollback success;
- rollback-required support posture;
- post-cutover encrypted startup recovery;
- backup retention and support-output exclusion;
- raw header and known-value scans on encrypted target;
- normal cash-readiness rules after migration;
- no plaintext fallback and no automatic empty replacement database.

No real cashier, transaction, payment, customer, statutory, or production data may be used.

## 25. Runtime Implementation Handoff

Recommended later branch: `feature/apt-plaintext-database-migration-runtime`.

Recommended task:

Implement an explicit offline plaintext-to-encrypted LocalOperations migration operation that follows this contract, preserves the source and verified backup, creates a separate SQLCipher target with a DPAPI `CurrentUser` envelope, verifies all local state before and after cutover, fails closed on ambiguous states, and keeps normal APT startup blocked until completion is proven.

Owning repository: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`.

Do not change Central PMS, POS Server, WebPay, Operator Console, statutory behavior, ordinance behavior, payment finality, HikCentral, or gate behavior.

Deferred after runtime migration:

- backup and diagnostics protection hardening;
- key rotation;
- terminal replacement and cross-account recovery;
- backup deletion tooling and retention enforcement;
- controlled UAT authorization.

## 26. Documentation-Only Validation Expectations

J-002 validation is limited to repository build/test baselines, documentation diff checks, Git status, and secret/privacy scans. No runtime migration is implemented. No plaintext database is migrated. Normal plaintext startup remains fail closed through existing J-001 behavior.
