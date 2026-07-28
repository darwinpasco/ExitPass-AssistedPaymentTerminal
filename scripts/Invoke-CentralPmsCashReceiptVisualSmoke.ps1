param(
  [int]$WebUiPort = 5179,
  [int]$CentralPmsPort = 5180,
  [string]$DatabasePath,
  [switch]$PayableBasisVisualSmoke,
  [switch]$TransactionCompletionVisualSmoke,
  [switch]$StatutoryDiscountVisualSmoke,
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
  APT_ENABLE_RECEIPT_PRINTING = $env:APT_ENABLE_RECEIPT_PRINTING
  APT_RECEIPT_PAPER_WIDTH_MM = $env:APT_RECEIPT_PAPER_WIDTH_MM
  APT_RECEIPT_PRINTER_NAME = $env:APT_RECEIPT_PRINTER_NAME
  APT_RECEIPT_PRINTER_MODE = $env:APT_RECEIPT_PRINTER_MODE
  APT_SITE_TIME_ZONE_ID = $env:APT_SITE_TIME_ZONE_ID
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
  $env:APT_ENABLE_RECEIPT_PRINTING = "true"
  $env:APT_RECEIPT_PAPER_WIDTH_MM = "57"
  $env:APT_RECEIPT_PRINTER_NAME = "APT Controlled Printer"
  $env:APT_RECEIPT_PRINTER_MODE = "visual-smoke"
  $env:APT_SITE_TIME_ZONE_ID = "Singapore Standard Time"
  $env:CENTRAL_PMS_BASE_URL = "http://127.0.0.1:$CentralPmsPort"

  $visualSmokeUrl = if ($StatutoryDiscountVisualSmoke) {
    "http://127.0.0.1:$WebUiPort/?statutoryDiscountVisualSmoke=1"
  } elseif ($TransactionCompletionVisualSmoke) {
    "http://127.0.0.1:$WebUiPort/?transactionCompletionVisualSmoke=1"
  } elseif ($PayableBasisVisualSmoke) {
    "http://127.0.0.1:$WebUiPort/?payableBasisVisualSmoke=1"
  } else {
    "http://127.0.0.1:$WebUiPort/?receiptVisualSmoke=1"
  }
  Write-Host $(if ($StatutoryDiscountVisualSmoke) { "Development-only statutory-discount visual smoke launcher" } elseif ($TransactionCompletionVisualSmoke) { "Development-only transaction-completion visual smoke launcher" } elseif ($PayableBasisVisualSmoke) { "Development-only payable-basis visual smoke launcher" } else { "Development-only receipt visual smoke launcher" })
  Write-Host "WebView URL: $visualSmokeUrl"
  Write-Host "Desktop window: ExitPass Assisted Payment Terminal"
  Write-Host "Central PMS fixture: $env:CENTRAL_PMS_BASE_URL"
  Write-Host "SQLite journal: $databasePath"
  Write-Host ""
  if ($StatutoryDiscountVisualSmoke) {
    Write-Host "Scenario buttons in the desktop window:"
    Write-Host "- No statutory request"
    Write-Host "- Draft statutory request"
    Write-Host "- Awaiting review"
    Write-Host "- Approved, application not requested"
    Write-Host "- Application processing"
    Write-Host "- Applied complete"
    Write-Host "- Applied amount changed"
    Write-Host "- Rejected"
    Write-Host "- Retryable decision failure"
    Write-Host "- Retryable application failure"
    Write-Host "- Terminal failure"
    Write-Host "- Required facts unavailable"
    Write-Host "- Restart awaiting review"
    Write-Host "- Restart after approval"
    Write-Host "- Restart during application processing"
    Write-Host "- Restart after applied amount change"
    Write-Host "- APPLIED complete but local prerequisites blocked"
    Write-Host "- APPLIED complete and Continue to Cash enabled"
    Write-Host "- Continue to Cash revalidation PASSED_UNCHANGED"
    Write-Host "- Continue to Cash revalidation AMOUNT_CHANGED"
    Write-Host "- Continue to Cash statutory blocked"
    Write-Host "- Immediate Record Cash Received revalidation PASSED_UNCHANGED"
    Write-Host "- Immediate Record Cash Received revalidation AMOUNT_CHANGED"
    Write-Host "- Immediate Record Cash Received retryable failure"
    Write-Host "- Immediate Record Cash Received terminal failure"
    Write-Host "- Statutory CASH_RECEIVED recorded once"
    Write-Host "- Restart before statutory CASH_RECEIVED requires revalidation"
    Write-Host "- Restart after statutory CASH_RECEIVED preserves custody evidence"
    Write-Host "- Restart after statutory CASH_RECEIVED resumes terminal-cash submission"
    Write-Host "- Non-statutory cash flow unchanged"
    Write-Host ""
    Write-Host "Statutory cash scenarios use controlled non-production fixtures and require canonical readiness, acknowledgement, local prerequisites, and immediate revalidation before CASH_RECEIVED."
    Write-Host ""
  } elseif ($TransactionCompletionVisualSmoke) {
    Write-Host "Scenario buttons in the desktop window:"
    Write-Host "- CASH_RECEIVED awaiting submission"
    Write-Host "- Terminal-cash submission accepted"
    Write-Host "- Terminal-cash submission retryable"
    Write-Host "- Payment finality pending"
    Write-Host "- Payment final, fiscal pending"
    Write-Host "- Fiscal retryable"
    Write-Host "- Fiscal document recorded, receipt unavailable"
    Write-Host "- Receipt available"
    Write-Host "- Terminal payment failure"
    Write-Host "- Terminal fiscal failure"
    Write-Host "- Receipt malformed or unsupported"
    Write-Host "- Restart after CASH_RECEIVED"
    Write-Host "- Restart during payment pending"
    Write-Host "- Restart during fiscal pending"
    Write-Host "- Restart with receipt available"
    Write-Host ""
    Write-Host "ExitAuthorization pending/available scenarios are excluded because no APT-usable Central PMS ExitAuthorization readback contract was found."
    Write-Host "Restart recovery: select a restart scenario and verify the same CASH_RECEIVED tender identity remains visible after relaunching with the same database."
    Write-Host ""
  } elseif ($PayableBasisVisualSmoke) {
    Write-Host "Scenario buttons in the desktop window:"
    Write-Host "- Ticket ready for cash"
    Write-Host "- Plate ready for cash"
    Write-Host "- Fiscal readiness blocked"
    Write-Host "- Session already paid"
    Write-Host "- Vendor PMS temporarily unavailable"
    Write-Host "- Revalidation passed unchanged"
    Write-Host "- Revalidation amount changed"
    Write-Host "- Restart recovery before cash acceptance"
    Write-Host ""
    Write-Host "Restart recovery: choose Restart recovery before cash acceptance, click Resolve, then click Simulate restart."
    Write-Host "The restored state remains before CASH_RECEIVED and requires revalidation before cash capture."
    Write-Host ""
  } else {
    Write-Host "Scenario buttons in the desktop window:"
    Write-Host "- Temporarily unavailable"
    Write-Host "- Available"
    Write-Host "- Terminal failure"
    Write-Host "- Incomplete configuration"
    Write-Host "- Restart-recovery setup"
    Write-Host ""
  }
  Write-Host "Controlled print checks:"
  Write-Host "- Original print available"
  Write-Host "- Reprint"
  Write-Host "- Printer unavailable"
  Write-Host "- Retryable printer failure"
  Write-Host "- Unknown spooler outcome"
  Write-Host "- Restart-recovery setup"
  Write-Host "- Unsupported width or invalid printer configuration"
  Write-Host "These scenarios use the controlled proof adapter and never target the default Windows printer."
  Write-Host ""
  Write-Host "Read-only print history checks:"
  Write-Host "- No print history"
  Write-Host "- Original submitted"
  Write-Host "- Original plus reprints"
  Write-Host "- Latest attempt failed"
  Write-Host "- Unknown outcome"
  Write-Host "- Printer changed"
  Write-Host "- Paper width changed"
  Write-Host "- Inconsistent copy sequence"
  Write-Host "- Print history restart recovery"
  Write-Host "History scenarios read only from the local SQLite journal and do not submit, retry, resolve, or cancel print jobs."
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
    --enable-receipt-printing `
    --local-db-path=$databasePath `
    --central-pms-base-url=$env:CENTRAL_PMS_BASE_URL `
    --receipt-paper-width-mm=57 `
    --receipt-printer-name="APT Controlled Printer" `
    --receipt-printer-mode=visual-smoke `
    --site-time-zone-id="Singapore Standard Time"
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
