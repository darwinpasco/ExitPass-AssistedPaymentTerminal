param(
  [switch]$Interactive,
  [ValidateSet("Available", "NotReady", "RetryPending", "Inconsistent", "Rejected", "Voided", "UnavailableThenAvailable", "Unsupported", "Malformed", "IncompleteConfiguration", "VisualMatrix")]
  [string]$Scenario = "Available",
  [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$proofRoot = Join-Path $env:TEMP ("exitpass-apt-central-pms-receipt-status-ui-proof-" + [guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $proofRoot "cash-journal.db"
$projectPath = Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashReceiptStatusUiProof\AssistedPaymentTerminal.CentralPmsCashReceiptStatusUiProof.csproj"

if ($Interactive) {
  $arguments = @("--interactive", "--scenario", $Scenario)
  if ($Port -gt 0) {
    $arguments += @("--port", $Port)
  }

  dotnet run --no-restore --project $projectPath -- @arguments
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  exit 0
}

try {
  New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null
  dotnet run --no-restore --project $projectPath -- --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  if (Test-Path $proofRoot) {
    Remove-Item -LiteralPath $proofRoot -Recurse -Force
  }
}
