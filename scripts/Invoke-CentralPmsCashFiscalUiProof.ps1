param(
  [switch]$Interactive,
  [ValidateSet("Recorded", "Replay", "Pending", "Conflict", "Rejected", "UncertainThenRecorded")]
  [string]$Scenario = "Recorded",
  [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$proofRoot = Join-Path $env:TEMP ("exitpass-apt-central-pms-fiscal-ui-proof-" + [guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $proofRoot "cash-journal.db"

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
  New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null
  dotnet run --no-restore --project (Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof\AssistedPaymentTerminal.CentralPmsCashFiscalUiProof.csproj") -- --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  if (Test-Path $proofRoot) {
    Remove-Item -LiteralPath $proofRoot -Recurse -Force
  }
}
