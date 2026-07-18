param(
  [switch]$Interactive,
  [ValidateSet("Available", "NotReady", "RetryPending", "Inconsistent", "Rejected", "Voided", "UnavailableThenAvailable")]
  [string]$Scenario = "Available",
  [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$databasePath = Join-Path $env:TEMP ("exitpass-apt-central-pms-receipt-status-ui-proof-" + [guid]::NewGuid().ToString("N") + ".db")
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
  dotnet run --no-restore --project $projectPath -- --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  if (Test-Path $databasePath) {
    Remove-Item -LiteralPath $databasePath -Force
  }

  $walPath = "$databasePath-wal"
  if (Test-Path $walPath) {
    Remove-Item -LiteralPath $walPath -Force
  }

  $shmPath = "$databasePath-shm"
  if (Test-Path $shmPath) {
    Remove-Item -LiteralPath $shmPath -Force
  }
}
