# ExitPass APT Encrypted First-Time Database Windows Manual Validation Runbook v1.0

## Purpose

This runbook validates the production APT encrypted first-time SQLite database behavior in an isolated non-production directory. It must not use or alter the normal APT LocalOperations directory.

Manual validation is required before PR preparation for the encrypted first-time database creation slice.

## Operational State Provenance

The manual proof distinguishes configuration identity from recovered operating state:

- Site, cashier, terminal, and POS Server values may be displayed from the `CASHIER_ASSISTED_TERMINAL` development configuration.
- Shift state must come from durable local recovery state in the encrypted database.
- Cash-custody state is separate from shift state and must also come from durable local recovery state.
- A fresh encrypted database is expected to contain no active cashier shift and no active cash-custody session.
- A configured development cashier or terminal must not fabricate `Shift OPEN`, an active cash-custody session, cash readiness, or recovered operational state.

Fresh-install expected operational state:

- Database exists: yes.
- Envelope exists: yes.
- Active shift record count: `0`.
- Active cash-custody record count: `0`.
- UI shift posture: `No active shift`, `CLOSED`, or an approved equivalent.
- Cash acceptance: blocked.

Restart expected operational state:

- If no active shift exists, restart must continue to show no active shift and block cash operations.
- If an active shift exists without an active cash-custody session, restart may show the active shift but must still block cash acceptance.
- If both an active shift and an active cash-custody session exist, restart may recover both; cash acceptance still depends on all Central PMS and local readiness gates.
- A closed shift must not recover as open.

## Fixed Scope

- Repository: `D:\SourceCodes\ExitPass-AssistedPaymentTerminal`
- Branch: `feature/apt-encrypted-database-key-envelope`
- DPAPI scope: `CurrentUser`
- Isolated manual-proof root: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\ManualEncryptionProof`
- Isolated database path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\ManualEncryptionProof\LocalOperations\cash-journal.db`
- Isolated envelope path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\ManualEncryptionProof\LocalOperations\cash-journal.key`
- Published application path: `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\ManualEncryptionProof\publish\AssistedPaymentTerminal.Desktop.exe`

Do not run these scenarios against `%LOCALAPPDATA%\ExitPass\AssistedPaymentTerminal\LocalOperations`.

## Setup

From the repository root:

```powershell
cd D:\SourceCodes\ExitPass-AssistedPaymentTerminal

powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Clean `
  -Force

powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -PreparePublish
```

Record the printed paths before any file operation.

Reusable variables:

```powershell
$manualRoot = Join-Path $env:LOCALAPPDATA "ExitPass\AssistedPaymentTerminal\ManualEncryptionProof"
$localOperationsRoot = Join-Path $manualRoot "LocalOperations"
$databasePath = Join-Path $localOperationsRoot "cash-journal.db"
$envelopePath = Join-Path $localOperationsRoot "cash-journal.key"
$publishRoot = Join-Path $manualRoot "publish"
$applicationPath = Join-Path $publishRoot "AssistedPaymentTerminal.Desktop.exe"
$knownValue = "APT-MANUAL-ENCRYPTION-KNOWN-VALUE-20260729"
```

Startup command:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

Stop command:

```powershell
Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $applicationPath } |
  Stop-Process
```

Cleanup command:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Clean `
  -Force
```

Hash commands:

```powershell
$databaseHash = if (Test-Path -LiteralPath $databasePath) { (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash } else { "MISSING" }
$envelopeHash = if (Test-Path -LiteralPath $envelopePath) { (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash } else { "MISSING" }
```

Raw header scan:

```powershell
$headerBytes = if (Test-Path -LiteralPath $databasePath) { [System.IO.File]::ReadAllBytes($databasePath)[0..15] } else { @() }
$headerText = [System.Text.Encoding]::ASCII.GetString($headerBytes)
$headerText
```

The encrypted database must not print `SQLite format 3`.

Known-value raw scan:

```powershell
$rawBytes = [System.IO.File]::ReadAllBytes($databasePath)
$rawText = [System.Text.Encoding]::ASCII.GetString($rawBytes)
$rawText.Contains($knownValue)
```

The result must be `False`.

Readiness inspection:

```powershell
dotnet run --project tools\AssistedPaymentTerminal.LocalOperations.Proof\AssistedPaymentTerminal.LocalOperations.Proof.csproj -- `
  --database-path $databasePath `
  --inspect-state
```

Record `active shift record count`, `active cash-custody record count`, and any active status values. Do not inspect or print protected key material.

## Scenario 1: New Encrypted Installation

1. Clean the isolated root:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Clean `
  -Force
```

2. Publish and confirm no database or envelope exists:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -PreparePublish

Test-Path -LiteralPath $databasePath
Test-Path -LiteralPath $envelopePath
```

Both checks must be `False`.

3. Launch the actual APT application and require the isolated files:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Launch
```

4. Confirm one database and one envelope exist:

```powershell
Test-Path -LiteralPath $databasePath
Test-Path -LiteralPath $envelopePath
Get-ChildItem -LiteralPath $localOperationsRoot -Filter "cash-journal*"
```

5. Create representative local non-production state through the application. Use only non-production cashier, shift, custody, denomination, and payable-basis fixture values. Do not use real cash or production payment.

6. Close the application:

```powershell
Get-Process -Id $aptProcess.Id -ErrorAction SilentlyContinue | Stop-Process
```

7. Scan the database header and known value:

```powershell
$headerBytes = [System.IO.File]::ReadAllBytes($databasePath)[0..15]
[System.Text.Encoding]::ASCII.GetString($headerBytes)

$rawText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($databasePath))
$rawText.Contains($knownValue)
```

8. Record database and envelope hashes:

```powershell
$databaseHashBeforeRestart = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
$envelopeHashBeforeRestart = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash
```

9. Restart and confirm local state recovery:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

10. Confirm envelope identity did not change unexpectedly:

```powershell
$envelopeHashAfterRestart = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash
$envelopeHashBeforeRestart -eq $envelopeHashAfterRestart
Get-ChildItem -LiteralPath $localOperationsRoot -Filter "cash-journal.key*"
```

The hash comparison must be `True`, and only one active envelope must exist.

Stop after Scenario 1 until it proves:

- isolated database created;
- isolated key envelope created;
- persistence readiness is `Ready`;
- `persistenceReady=true`;
- active shift record count is `0`;
- active cash-custody record count is `0`;
- the UI does not show `Shift OPEN` unless an active durable shift exists;
- cash acceptance remains blocked on the fresh database because no active shift or cash-custody session exists;
- the normal APT LocalOperations database hash is unchanged.

## Scripted Scenarios 2-6

Use the script-supported setup and launch/verify steps below. Each setup and verification prints the scenario name, isolated database path, isolated envelope path, normal database path, normal database hash, file existence, plaintext-header posture, active shift count, active cash-custody count where readable, and `PASS` or a terminating failure.

The script stops the isolated published APT process before mutating scenario files. It does not touch the normal APT LocalOperations directory.

Before each scenario group, publish if needed:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -PreparePublish
```

Scenario 2, envelope exists and database missing:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario2 -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario2 -Launch
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario2 -Restore
```

Expected result: the existing envelope remains unchanged, a new encrypted database is created with no plaintext header, no active shift or custody is fabricated, cash remains blocked, and the normal database hash is unchanged.

Scenario 3, encrypted database exists and envelope missing:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario3 -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario3 -Launch
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario3 -Restore
```

Expected result: startup fails closed, the encrypted database is unchanged, no replacement envelope is created, cash is unavailable, and the normal database hash is unchanged.

Scenario 4, malformed envelope:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario4 -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario4 -Launch
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario4 -Restore
```

Expected result: startup fails closed, the malformed envelope is not overwritten, the encrypted database is unchanged, cash is unavailable, and the normal database hash is unchanged.

Scenario 5, legacy plaintext database:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario5 -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario5 -Launch
```

Expected result: the plaintext header is detected, startup fails closed, no key envelope is generated, the plaintext fixture is unchanged, plaintext migration is not performed, cash is unavailable, and the normal database hash is unchanged.

Scenario 6A, active shift only:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6A -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6A -Launch
```

Expected result: one active shift is recovered, no active cash-custody session is recovered, and cash remains blocked.

Manual UI acceptance is still required after the script reports database verification. The top operational summary must show the recovered durable active shift, terminal details must show `SHIFT-DEV-20260714-A`, the shift status must be `Open`, custody must remain `None`, `No active shift` must not be shown, cash must remain blocked because custody is absent, and the normal database hash must remain unchanged.

Scenario 6B, active shift and active custody:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6B -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6B -Launch
```

Expected result: one active shift and one active cash-custody session are recovered. Cash readiness can proceed only when every other Central PMS and local readiness gate also passes.

Scenario 6C, closed shift:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6C -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6C -Launch
```

Expected result: the closed shift is not recovered as active, linked custody is not treated as active, and cash remains blocked.

Scenario 6D, repeated restart idempotency:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6D -Setup
powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Scenario Scenario6D -Launch
```

Expected result: repeated restart keeps exactly one active shift and one active cash-custody session, does not replace the envelope, does not recreate the database, and leaves the normal database hash unchanged.

## Scenario 2: Envelope Exists, Database Absent

1. Stop the application:

```powershell
Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $applicationPath } |
  Stop-Process
```

2. Record the envelope hash:

```powershell
$envelopeHashBefore = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash
```

3. Remove only the isolated test database:

```powershell
Remove-Item -LiteralPath $databasePath -Force
```

4. Restart:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

5. Confirm encrypted database recreation and unchanged envelope:

```powershell
Test-Path -LiteralPath $databasePath
$envelopeHashAfter = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash
$envelopeHashBefore -eq $envelopeHashAfter
$headerBytes = [System.IO.File]::ReadAllBytes($databasePath)[0..15]
[System.Text.Encoding]::ASCII.GetString($headerBytes)
```

The database must exist, the envelope hash must be unchanged, and the header must not be `SQLite format 3`.

## Scenario 3: Database Exists, Envelope Missing

1. Stop the application:

```powershell
Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $applicationPath } |
  Stop-Process
```

2. Record hashes and move the envelope to a controlled backup path:

```powershell
$databaseHashBefore = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
$envelopeBackupPath = Join-Path $localOperationsRoot "cash-journal.key.manual-backup"
Move-Item -LiteralPath $envelopePath -Destination $envelopeBackupPath
```

3. Launch:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

4. Confirm fail-closed behavior in the startup or local persistence readiness UI. Record `safeStatus=KeyEnvelopeMissing`, `persistenceReady=false`, and `cashOperationsAllowed=false`.

5. Confirm no replacement envelope or database was created:

```powershell
Test-Path -LiteralPath $envelopePath
$databaseHashAfter = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
$databaseHashBefore -eq $databaseHashAfter
```

6. Restore the envelope and restart:

```powershell
Get-Process -Id $aptProcess.Id -ErrorAction SilentlyContinue | Stop-Process
Move-Item -LiteralPath $envelopeBackupPath -Destination $envelopePath
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

Confirm recovery and `persistenceReady=true`.

## Scenario 4: Malformed Envelope

1. Stop the application and preserve valid files:

```powershell
Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $applicationPath } |
  Stop-Process

$databaseHashBefore = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
$envelopeHashBefore = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash
$validEnvelopeBackupPath = Join-Path $localOperationsRoot "cash-journal.key.valid-backup"
Copy-Item -LiteralPath $envelopePath -Destination $validEnvelopeBackupPath -Force
```

2. Replace the envelope with malformed test content. Do not print or inspect the protected key:

```powershell
Set-Content -LiteralPath $envelopePath -Value "{ malformed envelope" -Encoding UTF8
```

3. Launch:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

4. Confirm safe failure in readiness UI. Record `safeStatus=KeyEnvelopeMalformed`, `persistenceReady=false`, and `cashOperationsAllowed=false`.

5. Confirm no plaintext fallback and no database modification:

```powershell
$databaseHashAfter = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
$databaseHashBefore -eq $databaseHashAfter
$headerBytes = [System.IO.File]::ReadAllBytes($databasePath)[0..15]
[System.Text.Encoding]::ASCII.GetString($headerBytes)
```

6. Restore and confirm recovery:

```powershell
Get-Process -Id $aptProcess.Id -ErrorAction SilentlyContinue | Stop-Process
Copy-Item -LiteralPath $validEnvelopeBackupPath -Destination $envelopePath -Force
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

## Scenario 5: Legacy Plaintext Database

1. Stop and clean the isolated root:

```powershell
Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $applicationPath } |
  Stop-Process

powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Clean `
  -Force

powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -PreparePublish
New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
```

2. Create a controlled plaintext SQLite fixture using the published SQLite assemblies:

```powershell
Add-Type -Path (Join-Path $publishRoot "SQLitePCLRaw.core.dll")
Add-Type -Path (Join-Path $publishRoot "SQLitePCLRaw.batteries_v2.dll")
Add-Type -Path (Join-Path $publishRoot "Microsoft.Data.Sqlite.dll")
[SQLitePCL.Batteries_V2]::Init()

$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$databasePath;Mode=ReadWriteCreate;Pooling=False")
$connection.Open()
$command = $connection.CreateCommand()
$command.CommandText = "CREATE TABLE manual_plaintext_fixture (id TEXT NOT NULL PRIMARY KEY, known_value TEXT NOT NULL);"
$command.ExecuteNonQuery() | Out-Null
$command.CommandText = "INSERT INTO manual_plaintext_fixture VALUES ('fixture-1', '$knownValue');"
$command.ExecuteNonQuery() | Out-Null
$connection.Dispose()
```

3. Confirm plaintext fixture before launch:

```powershell
$headerBytes = [System.IO.File]::ReadAllBytes($databasePath)[0..15]
[System.Text.Encoding]::ASCII.GetString($headerBytes)
Test-Path -LiteralPath $envelopePath
```

The header should be `SQLite format 3`, and the envelope should be missing.

4. Launch:

```powershell
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

5. Confirm migration-required fail-closed posture. Record `safeStatus=LegacyPlaintextMigrationRequired`, `migrationRequired=true`, `persistenceReady=false`, and `cashOperationsAllowed=false`.

6. Confirm the plaintext database is unchanged and no envelope was generated:

```powershell
Test-Path -LiteralPath $envelopePath
$headerBytes = [System.IO.File]::ReadAllBytes($databasePath)[0..15]
[System.Text.Encoding]::ASCII.GetString($headerBytes)
$rawText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($databasePath))
$rawText.Contains($knownValue)
```

The envelope check must be `False`; the plaintext header and known value are expected for this controlled legacy fixture only.

## Scenario 6: Cash-Readiness Regression

1. Clean, publish, and launch with valid encrypted storage:

```powershell
powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -Clean `
  -Force

powershell -ExecutionPolicy Bypass `
  -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 `
  -PreparePublish

$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

2. Using only non-production data in the application, create or restore:

- cashier-shift state;
- cash-custody state;
- denomination data;
- durable pre-cash payable-basis state.

3. Restart:

```powershell
Get-Process -Id $aptProcess.Id -ErrorAction SilentlyContinue | Stop-Process
$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
$env:APT_LOCAL_DB_PATH = $databasePath
$aptProcess = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
```

4. Confirm all representative state is recovered.

5. Confirm Central PMS readiness rules remain unchanged and local persistence readiness participates only as a local prerequisite.

6. Do not use real cash or production payment.

## Evidence Record Template

Create the manual evidence record at:

`docs/security/manual-evidence/ExitPass_APT_Encrypted_First_Time_Database_Windows_Manual_Validation_Record_<YYYYMMDD>.md`

Do not include plaintext keys, protected key bytes, connection strings, full database contents, customer data, or production credentials.

Template:

```markdown
# ExitPass APT Encrypted First-Time Database Windows Manual Validation Record

- Repository commit:
- Windows username:
- DPAPI scope: CurrentUser
- Isolated database path:
- Isolated envelope path:
- Published application path:

## Scenario 1: New Encrypted Installation
- Database hash before:
- Database hash after:
- Envelope hash before:
- Envelope hash after:
- Persistence readiness classification:
- cashOperationsAllowed:
- Raw-header scan:
- Known-value scan:
- Result:
- Cleanup result:

## Scenario 2: Envelope Exists, Database Absent
- Database hash before:
- Database hash after:
- Envelope hash before:
- Envelope hash after:
- Persistence readiness classification:
- cashOperationsAllowed:
- Raw-header scan:
- Result:
- Cleanup result:

## Scenario 3: Database Exists, Envelope Missing
- Database hash before:
- Database hash after:
- Envelope hash before:
- Envelope hash after restore:
- Persistence readiness classification:
- cashOperationsAllowed:
- Result:
- Cleanup result:

## Scenario 4: Malformed Envelope
- Database hash before:
- Database hash after:
- Envelope hash before:
- Envelope hash after restore:
- Persistence readiness classification:
- cashOperationsAllowed:
- Result:
- Cleanup result:

## Scenario 5: Legacy Plaintext Database
- Database hash before:
- Database hash after:
- Envelope exists before:
- Envelope exists after:
- Persistence readiness classification:
- migrationRequired:
- cashOperationsAllowed:
- Raw-header scan:
- Known-value scan:
- Result:
- Cleanup result:

## Scenario 6: Cash-Readiness Regression
- Database hash before:
- Database hash after:
- Envelope hash before:
- Envelope hash after:
- Persistence readiness classification:
- cashOperationsAllowed:
- State recovered:
- Central PMS readiness unchanged:
- Result:
- Cleanup result:
```
