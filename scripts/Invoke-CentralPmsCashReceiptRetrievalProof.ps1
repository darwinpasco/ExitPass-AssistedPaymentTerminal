param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$testProject = Join-Path $repoRoot "tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj"

Write-Host "Assisted Payment Terminal Central PMS cash receipt retrieval proof"
Write-Host "Repository: $repoRoot"
Write-Host "Test project: $testProject"
Write-Host "Scope: recorded fiscal command -> durable receipt retrieval -> Central PMS receipt GET -> opaque authoritative payload persistence"
Write-Host "Database posture: focused tests use temporary SQLite databases outside the repository"
Write-Host "Stop boundaries: no direct POS Server client, rendering, printing, exit authorization, gate behavior, or Payment Orchestrator call"

dotnet test $testProject `
    --configuration $Configuration `
    --no-restore `
    --filter "FullyQualifiedName~TerminalCashReceipt" `
    --logger "console;verbosity=minimal"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Proof completed successfully."
