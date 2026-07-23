param(
  [int]$WebUiPort = 5179,
  [int]$CentralPmsPort = 5180,
  [string]$DatabasePath,
  [switch]$CleanupDatabaseOnExit
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$databasePath = if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
  Join-Path $env:TEMP "exitpass-apt-receipt-visual-smoke.db"
} else {
  [System.IO.Path]::GetFullPath($DatabasePath)
}

if ($databasePath.StartsWith($repoRoot.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Visual smoke database path must be outside the Git repository."
}

$hostProcess = $null
$viteProcess = $null
$originalEnv = @{
  APT_PROFILE = $env:APT_PROFILE
  APT_LOCAL_DB_PATH = $env:APT_LOCAL_DB_PATH
  APT_ENABLE_NON_LIVE_CASH_CAPTURE = $env:APT_ENABLE_NON_LIVE_CASH_CAPTURE
  APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION = $env:APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION
  APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE = $env:APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE
  APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL = $env:APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL
  APT_ENABLE_RECEIPT_PREVIEW = $env:APT_ENABLE_RECEIPT_PREVIEW
  APT_RECEIPT_PAPER_WIDTH_MM = $env:APT_RECEIPT_PAPER_WIDTH_MM
  CENTRAL_PMS_BASE_URL = $env:CENTRAL_PMS_BASE_URL
}

function Restore-Environment {
  foreach ($entry in $originalEnv.GetEnumerator()) {
    if ($null -eq $entry.Value) {
      Remove-Item -Path "Env:$($entry.Key)" -ErrorAction SilentlyContinue
    } else {
      Set-Item -Path "Env:$($entry.Key)" -Value $entry.Value
    }
  }
}

function Stop-StartedProcess {
  param([System.Diagnostics.Process]$Process)

  if ($null -ne $Process -and -not $Process.HasExited) {
    Stop-Process -Id $Process.Id -Force
  }
}

function Wait-ForLoopbackPort {
  param(
    [int]$Port,
    [string]$Name
  )

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
      $client = [System.Net.Sockets.TcpClient]::new()
      $connect = $client.ConnectAsync("127.0.0.1", $Port)
      if ($connect.Wait(500) -and $client.Connected) {
        $client.Dispose()
        return
      }
      $client.Dispose()
    } catch {
    }

    Start-Sleep -Milliseconds 250
  }

  throw "$Name did not bind to 127.0.0.1:$Port within 30 seconds."
}

try {
  $hostProcess = Start-Process `
    -FilePath "powershell" `
    -ArgumentList @(
      "-NoProfile",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      (Join-Path $repoRoot "scripts\Invoke-CentralPmsCashReceiptStatusUiProof.ps1"),
      "-Interactive",
      "-Scenario",
      "VisualMatrix",
      "-Port",
      $CentralPmsPort
    ) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -PassThru
  Wait-ForLoopbackPort -Port $CentralPmsPort -Name "Central PMS receipt visual smoke fixture"

  $viteProcess = Start-Process `
    -FilePath "npm.cmd" `
    -ArgumentList @(
      "run",
      "dev",
      "--workspace",
      "src/AssistedPaymentTerminal.App",
      "--",
      "--host",
      "127.0.0.1",
      "--port",
      $WebUiPort,
      "--strictPort"
    ) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -PassThru
  Wait-ForLoopbackPort -Port $WebUiPort -Name "Receipt visual smoke Web UI"

  $env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
  $env:APT_LOCAL_DB_PATH = $databasePath
  $env:APT_ENABLE_NON_LIVE_CASH_CAPTURE = "true"
  $env:APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION = "true"
  $env:APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE = "true"
  $env:APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL = "true"
  $env:APT_ENABLE_RECEIPT_PREVIEW = "true"
  $env:APT_RECEIPT_PAPER_WIDTH_MM = "57"
  $env:CENTRAL_PMS_BASE_URL = "http://127.0.0.1:$CentralPmsPort"

  $visualSmokeUrl = "http://127.0.0.1:$WebUiPort/?receiptVisualSmoke=1"
  Write-Host "Development-only receipt visual smoke launcher"
  Write-Host "WebView URL: $visualSmokeUrl"
  Write-Host "Desktop window: ExitPass Assisted Payment Terminal"
  Write-Host "Central PMS fixture: $env:CENTRAL_PMS_BASE_URL"
  Write-Host "SQLite journal: $databasePath"
  Write-Host ""
  Write-Host "Scenario buttons in the desktop window:"
  Write-Host "- Temporarily unavailable"
  Write-Host "- Available"
  Write-Host "- Terminal failure"
  Write-Host "- Incomplete configuration"
  Write-Host "- Restart-recovery setup"
  Write-Host ""
  Write-Host "Restart recovery: close the desktop window, then rerun this same command with:"
  Write-Host "-DatabasePath `"$databasePath`""
  Write-Host ""
  Write-Host "Clean shutdown: close the desktop window or press Ctrl+C in this console."
  Write-Host "Optional cleanup after manual testing:"
  Write-Host "Remove-Item `"$databasePath*`" -Force -ErrorAction SilentlyContinue"
  Write-Host ""

  $desktopProjectPath = Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop"
  dotnet run --no-restore --project $desktopProjectPath -- `
    --profile=CASHIER_ASSISTED_TERMINAL `
    --web-ui-url=$visualSmokeUrl `
    --enable-non-live-cash-capture `
    --enable-central-pms-cash-submission `
    --enable-central-pms-fiscal-issuance `
    --enable-central-pms-receipt-retrieval `
    --enable-receipt-preview `
    --local-db-path=$databasePath `
    --central-pms-base-url=$env:CENTRAL_PMS_BASE_URL `
    --receipt-paper-width-mm=57
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  Stop-StartedProcess -Process $viteProcess
  Stop-StartedProcess -Process $hostProcess
  Restore-Environment

  if ($CleanupDatabaseOnExit) {
    Remove-Item "$databasePath*" -Force -ErrorAction SilentlyContinue
  }
}
