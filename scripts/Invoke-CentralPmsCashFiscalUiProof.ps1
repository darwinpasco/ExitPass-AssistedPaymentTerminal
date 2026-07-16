param(
  [switch]$Interactive,
  [ValidateSet("Recorded", "Replay", "Pending", "Conflict", "Rejected", "UncertainThenRecorded")]
  [string]$Scenario = "Recorded",
  [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$databasePath = Join-Path $env:TEMP ("exitpass-apt-central-pms-fiscal-ui-proof-" + [guid]::NewGuid().ToString("N") + ".db")

if ($Interactive) {
  $arguments = @("--interactive", "--scenario", $Scenario)
  if ($Port -gt 0) {
    $arguments += @("--port", $Port)
  }

  dotnet run --no-restore --project (Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof.csproj") -- @arguments
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  exit 0
}

try {
  dotnet run --no-restore --project (Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof.csproj") -- --database-path $databasePath
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
