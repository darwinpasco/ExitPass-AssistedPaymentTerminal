# ExitPass APT Encrypted First-Time Database Creation Implementation v1.0

## Scope

This implementation enables encrypted first-time creation for the local Cashier-Assisted Terminal operational SQLite database. It does not implement plaintext migration, key rotation, backup redesign, terminal replacement recovery, or multi-account recovery.

## Frozen Deployment Decision

Each physical APT uses one dedicated controlled Windows account. Cashiers authenticate through ExitPass application identities and do not require separate Windows profiles. The selected key-protection scope is Windows DPAPI `CurrentUser`.

The encrypted database and protected key envelope are bound to the dedicated Windows user profile. Moving them to another Windows account or replacement terminal is unsupported in this slice.

## Provider

The production local operations project uses the merged provider baseline:

- `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11
- `Microsoft.Data.Sqlite.Core` 8.0.21
- EF Core SQLite 8.0.21

`SQLitePCL.Batteries_V2.Init()` is called before encrypted connections open.

## Paths

Production database path:

`%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db`

Protected key-envelope path:

`%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.key`

The envelope is stored beside the database in the dedicated user's LocalAppData profile. It is not stored in source, Program Files, Temp, Documents, or committed configuration.

## Envelope Format

Envelope schema version: `1`

Fields:

- `schemaVersion`
- `databaseIdentity`
- `keyId`
- `protectionScope`
- `entropyId`
- `keyAlgorithm`
- `protectedKey`
- `createdAt`
- `lastProtectedAt`

The envelope does not contain the plaintext database key, connection string, database content, cashier identity, tender data, credentials, or raw exception details.

Unsupported versions, wrong database identity, wrong scope, malformed payloads, empty protected key material, and DPAPI unwrap failures fail closed.

## Startup Sequence

1. Resolve the production database path.
2. Ensure the LocalOperations directory exists under the dedicated Windows profile.
3. Initialize SQLCipher.
4. If neither database nor envelope exists, generate one random 256-bit SQLCipher key.
5. Protect the key with DPAPI `CurrentUser`.
6. Write the envelope to a temporary file in the same directory and flush it.
7. Atomically activate the envelope.
8. Open or create the database using the protected key.
9. Apply the existing EF schema initialization.
10. Apply existing additive schema checks.
11. Validate local persistence readiness before local cash workflows proceed.

The database is never intentionally created as plaintext in this path.

## Crash-State Handling

Envelope exists and database does not:

- reuse the existing protected key;
- create the encrypted database with that key;
- do not generate a replacement key.

Database exists and envelope is missing:

- fail closed;
- preserve the database;
- do not create a replacement envelope;
- do not create an empty database.

Initialization failure after envelope creation:

- preserve the envelope for deterministic retry;
- do not delete or replace existing local evidence.

## Legacy Plaintext Classification

An existing file with the standard SQLite plaintext header is classified as `LegacyPlaintextMigrationRequired`.

Behavior:

- preserve the plaintext file unchanged;
- do not create a key envelope;
- do not create a replacement encrypted database;
- do not fall back to plaintext;
- block cash operations.

Plaintext migration is deferred to a separate approved slice.

## Local Persistence Readiness

The local journal health response now exposes a safe `localPersistence` readiness object with encryption, envelope, database, migration, integrity, schema, recovery, and cash-operation fields.

No key bytes, protected key bytes, connection strings, SQL statements, raw SQLite errors, or stack traces are exposed.

Cash operations are allowed to evaluate their existing readiness only after encrypted local persistence is ready. Central PMS payable-basis and fiscal readiness remain authoritative for their domains.

## Failure Posture

Fail-closed states include:

- key envelope missing;
- key envelope malformed;
- unsupported envelope version;
- wrong database identity;
- wrong DPAPI scope;
- DPAPI `CurrentUser` unwrap failure;
- wrong key for the encrypted database;
- corrupt or unreadable encrypted database;
- legacy plaintext database.

Cash workflows remain blocked in these states.

## Directory and ACL Posture

The directory is created under the dedicated user's LocalAppData profile. DPAPI `CurrentUser` is the primary key boundary. The current code does not make installer-wide ACL changes; installer ACL hardening and validation remain a deployment task. Administrators or malware running in the same Windows profile remain outside the protection that whole-database encryption alone can provide.

## Logging

Safe events may report envelope created/loaded, encrypted database created/reopened/validated, legacy plaintext detected, envelope unavailable, DPAPI failure, encrypted database open failure, and local persistence blocked.

Do not log plaintext keys, protected key contents, connection strings, SQLCipher key statements, complete envelope serialization, database pages, or sensitive row content.

## Known Limitations

- Plaintext migration is not implemented.
- Database-key rotation is not implemented.
- Backup and support bundle protection are not implemented.
- Terminal replacement recovery is not implemented.
- Cross-account database recovery is not supported.
- The SQLCipher provider requires a transient in-memory key string while opening a connection.

## Automated Tests

Focused tests cover random key generation, envelope round-trip and validation, encrypted first-time creation, correct-key reopen, no-key and wrong-key rejection, encrypted header posture, raw value scan, restart reuse, envelope-without-database recovery, database-without-envelope failure, legacy plaintext detection, malformed and unsupported envelopes, DPAPI unwrap failure, corrupt database failure, local persistence readiness, and cash-operation blocking.

## Manual Windows Test Runbook

Manual test scenarios for Darwin before PR preparation:

1. New encrypted installation.
2. Envelope exists, database absent.
3. Database exists, envelope missing.
4. Malformed or corrupt envelope.
5. Legacy plaintext database.
6. Cash-readiness regression with valid encrypted storage.

Use only controlled non-production data. Do not use real cash, real payment, or production transactions.
