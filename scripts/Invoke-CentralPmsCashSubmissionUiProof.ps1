$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$proofRoot = Join-Path $env:TEMP ("exitpass-apt-central-pms-ui-proof-" + [guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $proofRoot "cash-journal.db"

try {
  New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null
  dotnet run --no-restore --project (Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashSubmissionUiProof\AssistedPaymentTerminal.CentralPmsCashSubmissionUiProof.csproj") -- --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  if (Test-Path $proofRoot) {
    Remove-Item -LiteralPath $proofRoot -Recurse -Force
  }
}
