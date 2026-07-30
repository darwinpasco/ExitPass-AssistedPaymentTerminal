$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tools\AssistedPaymentTerminal.PlaintextDatabaseMigration\AssistedPaymentTerminal.PlaintextDatabaseMigration.csproj"
$harnessProject = Join-Path $root "tools\AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness\AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness.csproj"
$baseProofRoot = if (Test-Path -LiteralPath "D:\Temp") {
    "D:\Temp\ExitPass"
}
else {
    $env:TEMP
}
$proofRoot = Join-Path $baseProofRoot ("ExitPass.APT.PlaintextMigrationProof." + [guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $proofRoot "LocalOperations\cash-journal.db"
$harnessRoot = Join-Path $proofRoot "ValidationHarness"
$coreHarnessRoot = Join-Path $harnessRoot "Core"
$failuresHarnessRoot = Join-Path $harnessRoot "Failures"
$interruptionsHarnessRoot = Join-Path $harnessRoot "Interruptions"
$recoveryHarnessRoot = Join-Path $harnessRoot "Recovery"

Write-Host "APT plaintext database migration proof"
Write-Host "Proof root: $proofRoot"
Write-Host "Database path: $databasePath"

try {
    dotnet test (Join-Path $root "tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj") `
        --filter "FullyQualifiedName~LocalDatabasePlaintextMigrationTests" `
        -m:1 `
        /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Focused plaintext migration tests failed."
    }

    dotnet run --project $project -- --local-db-path $databasePath --dry-classify
    if ($LASTEXITCODE -ne 0) {
        throw "Dry classification failed."
    }

    dotnet build $harnessProject --configuration Release -m:1 /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Plaintext migration validation harness build failed."
    }

    dotnet run --no-build --project $harnessProject --configuration Release -- `
        --acknowledge-validation-only `
        --validation-root $coreHarnessRoot `
        --scenario Core
    if ($LASTEXITCODE -ne 0) {
        throw "Core plaintext migration validation harness scenarios failed."
    }

    dotnet run --no-build --project $harnessProject --configuration Release -- `
        --acknowledge-validation-only `
        --validation-root $failuresHarnessRoot `
        --scenario Failures
    if ($LASTEXITCODE -ne 0) {
        throw "Failure-classification plaintext migration validation harness scenarios failed."
    }

    dotnet run --no-build --project $harnessProject --configuration Release -- `
        --acknowledge-validation-only `
        --validation-root $interruptionsHarnessRoot `
        --scenario Interruptions
    if ($LASTEXITCODE -ne 0) {
        throw "Interruption and rollback plaintext migration validation harness scenarios failed."
    }

    dotnet run --no-build --project $harnessProject --configuration Release -- `
        --acknowledge-validation-only `
        --validation-root $recoveryHarnessRoot `
        --scenario Recovery
    if ($LASTEXITCODE -ne 0) {
        throw "Operational recovery plaintext migration validation harness scenarios failed."
    }

    Write-Host "Focused migration tests and validation harness covered synthetic plaintext source migration, WAL preservation, conflicts, interruptions, rollback, idempotency, and encrypted recovery."
    Write-Host "No production database, Central PMS, POS Server, receipt, print, ExitAuthorization, HikCentral, WebPay, or gate behavior is used."
}
finally {
    if (Test-Path -LiteralPath $proofRoot) {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force
    }
}
