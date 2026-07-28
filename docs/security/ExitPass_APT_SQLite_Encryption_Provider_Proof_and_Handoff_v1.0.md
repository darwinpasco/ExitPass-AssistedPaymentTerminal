# ExitPass APT SQLite Encryption Provider Proof and Handoff v1.0

## Decision

Selected provider: SQLCipher through `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11, used with `Microsoft.Data.Sqlite.Core` 8.0.21 and `Microsoft.EntityFrameworkCore.Sqlite` 8.0.21.

This is a provider proof only. It does not enable encryption in `CashJournalService`, does not migrate a production APT database, and does not change the production `CashJournalDbContext` startup path.

## Current Persistence Inventory

- Runtime: .NET SDK 8.0.421, `net8.0` for LocalOperations and `net8.0-windows` for Desktop.
- EF Core: `Microsoft.EntityFrameworkCore` 8.0.21 and `Microsoft.EntityFrameworkCore.Sqlite` 8.0.21.
- SQLite provider: production LocalOperations references `Microsoft.Data.Sqlite` 8.0.21.
- Current production connection path: `CashJournalService.CreateDbContext()` builds a `SqliteConnectionStringBuilder` with `DataSource = DatabasePath` and `Pooling = false`, then calls `.UseSqlite(connectionString)`.
- Database initialization: `CashJournalService.InitializeAsync()` creates the containing directory, calls `EnsureCreatedAsync`, then applies additive idempotent schema checks.
- Default database path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db`, unless `APT_LOCAL_DB_PATH` or command-line `--local-db-path` supplies an override.
- Desktop runtime identifier: `win-x64`.
- Publish posture found in source: no `.pubxml` profile; Desktop project is framework-dependent unless publish commands choose otherwise; no single-file setting is present.
- Installer packaging: no installer source was found in this repository.
- Test database factory: focused tests use temporary database paths under the OS temp directory.

## Provider Comparison

| Candidate | License | Compatibility | Deployment | Result |
| --- | --- | --- | --- | --- |
| `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 | Package metadata reports Apache-2.0 for SQLitePCLRaw packages. SQLCipher Community Edition is BSD-style open source. | Compatible with `Microsoft.Data.Sqlite.Core` and EF Core when the SQLCipher bundle is initialized before opening connections. | Publishes `e_sqlcipher.dll` for Windows x64. | Selected. It is the smallest proof-compatible path and keeps EF Core usage intact. |
| `Microsoft.Data.Sqlite` with default `SQLitePCLRaw.bundle_e_sqlite3` | MIT plus SQLite public-domain native library. | Already used by APT. | Simple deployment, but no whole-database encryption. | Rejected because it does not provide database encryption. |
| Commercial SQLite encryption products such as SEE or SQLiteCrypt | Commercial/proprietary. | Can support SQLite encryption, depending on vendor provider. | Requires vendor license, binaries, support, and procurement. | Rejected for this slice because licensing is not already established and the SQLCipher path is technically sufficient. |
| Provider-specific encrypted EF Core wrappers | Varies by vendor/package. | Often introduces provider-specific abstractions or unclear maintenance. | Higher package and support risk. | Rejected unless SQLCipher is later disqualified by policy or deployment constraints. |

## Technical Proof

The isolated proof project is `tools/AssistedPaymentTerminal.SqliteEncryptionProviderProof`.

The proof demonstrates:

- random 256-bit database key generation;
- encrypted database creation;
- EF Core schema creation;
- representative safe value insert;
- close and reopen with the correct key;
- no-key failure;
- wrong-key failure;
- encrypted header check, verifying the file does not expose `SQLite format 3`;
- raw file scan for the known inserted value;
- actual SQLCipher `PRAGMA rekey`;
- old-key rejection after rekey;
- new-key open after rekey;
- plaintext-to-encrypted migration feasibility with `ATTACH DATABASE ... KEY` and `sqlcipher_export`;
- Windows x64 release publish;
- published executable execution;
- published native dependency inventory.

## Native Dependencies and Publish Behavior

The selected proof path requires the SQLCipher native library from SQLitePCLRaw. Windows x64 publish must include:

- `e_sqlcipher.dll`;
- `SQLitePCLRaw.core.dll`;
- `SQLitePCLRaw.batteries_v2.dll`;
- `Microsoft.Data.Sqlite.dll`;
- EF Core assemblies used by the proof.

The proof uses framework-dependent Windows x64 publish, matching the current Desktop project posture. Single-file publish is not currently configured by the APT Desktop project and remains a separate validation item before any future single-file packaging decision.

## DPAPI Recommendation

Recommended DPAPI scope: unresolved pending deployment-account decision.

Security preference: `CurrentUser` when one controlled Windows account operates the terminal. This limits key unwrap to that Windows profile and reduces blast radius if another local account can access the database file.

Use `LocalMachine` only if multiple controlled Windows accounts must open the same local APT database and the installer applies restrictive ACLs to both the database and key-envelope files.

Decision question for Darwin: Will each deployed Assisted Payment Terminal run under one controlled Windows account for the life of the local database, or must multiple Windows accounts on the same terminal open the same database?

## Future Key Envelope Design

Recommended envelope fields:

- `schemaVersion`;
- `databaseIdentity`;
- `keyId`;
- `dpapiScope`;
- `optionalEntropyId`;
- `protectedKeyBytes`;
- `createdAt`;
- `rotatedAt`;
- `algorithm`: `SQLCipher`;
- `providerPackage`: selected package and version;
- `kdfSettings` when explicitly configured;
- `integrityCheckStatus`;
- `lastVerifiedAt`.

Operational rules:

- write envelope to a separate file beside the database or under a terminal-local secure configuration directory;
- never persist the plaintext database key;
- write replacements atomically by creating a new envelope file, flushing it, then replacing the old envelope;
- fail closed for unsupported envelope version, missing envelope, DPAPI unwrap failure, wrong Windows context, missing optional entropy, and failed SQLCipher integrity check;
- apply restrictive ACLs to database, WAL/SHM, backup, diagnostics, and envelope files.

## Migration Feasibility

The proof validates the provider path for plaintext-to-encrypted migration:

1. detect plaintext by checking for the `SQLite format 3` header;
2. open the plaintext source;
3. attach an encrypted destination with SQLCipher key;
4. run `sqlcipher_export`;
5. detach the destination;
6. validate encrypted header and known-value non-disclosure;
7. reopen destination with key and validate data.

Production migration must still add:

- source database backup or quarantine;
- destination integrity validation against the full CashJournal model;
- atomic activation with rename/replace;
- interrupted migration recovery;
- rollback rules;
- secure deletion or quarantine retention policy for plaintext files;
- WAL/SHM handling.

## Threat Model

Addressed:

- offline file inspection of the local SQLite database;
- casual copying of the database without the Windows-protected key envelope;
- raw string extraction from the database file;
- use of a wrong SQLCipher key.

Not fully addressed by the provider alone:

- malware running as the same Windows account after key unwrap;
- memory scraping;
- compromised administrator account;
- weak Windows account controls;
- plaintext backups or diagnostics created outside the encrypted database;
- unencrypted logs.

## Known Limitations

- This proof does not enable production encryption.
- This proof does not implement DPAPI key wrapping.
- This proof does not migrate any APT database.
- This proof does not validate installer ACLs.
- Single-file publish is not proven because the current Desktop project does not use single-file publish.
- CurrentUser versus LocalMachine DPAPI scope depends on deployment account policy.

## Recommended Implementation Slices

1. Production key-envelope and encrypted new-database creation.
2. Plaintext migration and rollback.
3. Missing, wrong, corrupt-key fail-closed readiness.
4. Actual key rotation.
5. Backup and diagnostics protection.
6. Full restart and cash-workflow regression.
7. Windows manual proof for installed desktop deployment.

## Validation

Run:

```powershell
dotnet restore
dotnet build tools\AssistedPaymentTerminal.SqliteEncryptionProviderProof\AssistedPaymentTerminal.SqliteEncryptionProviderProof.csproj --no-restore
dotnet test tests\AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests\AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptSqliteEncryptionProviderProof.ps1
git diff --check
git status --short --branch --untracked-files=all
```

Significant manual testing required for this provider-proof merge: No, provided the Windows x64 published proof executes successfully and automated proof scenarios pass.

Significant manual testing required for the later production encryption implementation: Yes.
