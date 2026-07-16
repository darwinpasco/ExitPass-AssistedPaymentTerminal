$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$databasePath = Join-Path $env:TEMP ("exitpass-apt-central-pms-fiscal-outbox-proof-" + [guid]::NewGuid().ToString("N") + ".db")

try {
  dotnet run --no-restore --project (Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashFiscalOutboxProof\AssistedPaymentTerminal.CentralPmsCashFiscalOutboxProof.csproj") -- --database-path $databasePath
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
