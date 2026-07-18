param(
  [switch]$Interactive,
  [switch]$KeepDatabase,
  [ValidateSet("Available", "Complete", "Voided", "UnsupportedVersion", "PayloadHashMismatch", "MalformedPayload")]
  [string]$Scenario = "Available",
  [ValidateSet("57", "58", "80", "99")]
  [string]$PaperWidthMm = "57",
  [string]$DatabasePath
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$databasePath = if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
  Join-Path $env:TEMP ("exitpass-apt-receipt-preview-ui-proof-" + [guid]::NewGuid().ToString("N") + ".db")
} else {
  [System.IO.Path]::GetFullPath($DatabasePath)
}
$projectPath = Join-Path $repoRoot "tools\AssistedPaymentTerminal.CentralPmsCashReceiptPreviewUiProof\AssistedPaymentTerminal.CentralPmsCashReceiptPreviewUiProof.csproj"

if ($Interactive) {
  dotnet run --no-restore --project $projectPath -- --interactive --scenario $Scenario --paper-width-mm $PaperWidthMm --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "Interactive proof database was not found after proof exit: $databasePath"
  }

  Write-Host ""
  Write-Host "Seeded database exists: $(Test-Path -LiteralPath $databasePath)"
  Write-Host "Copy-ready validation command:"
  Write-Host "Test-Path `"`$env:APT_LOCAL_DB_PATH`""
  Write-Host "Direct path validation command:"
  Write-Host "Test-Path `"$databasePath`""
  Write-Host "Cleanup command for after manual testing:"
  Write-Host "Remove-Item `"$databasePath*`" -Force -ErrorAction SilentlyContinue"
  exit 0
}

try {
  dotnet run --no-restore --project $projectPath -- --database-path $databasePath
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  if ($KeepDatabase) {
    Write-Host "Keeping proof database: $databasePath"
    Write-Host "Cleanup command:"
    Write-Host "Remove-Item `"$databasePath*`" -Force -ErrorAction SilentlyContinue"
  } else {
    if (Test-Path -LiteralPath $databasePath) {
      Remove-Item -LiteralPath $databasePath -Force
    }

    $walPath = "$databasePath-wal"
    if (Test-Path -LiteralPath $walPath) {
      Remove-Item -LiteralPath $walPath -Force
    }

    $shmPath = "$databasePath-shm"
    if (Test-Path -LiteralPath $shmPath) {
      Remove-Item -LiteralPath $shmPath -Force
    }
  }
}
