param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- CashCapturePanel.test.tsx TransactionCompletionVisualSmoke.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $stateMachineFiles = @(
    "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\TransactionCompletionVisualSmoke.tsx",
    "src\AssistedPaymentTerminal.App\src\localJournalBridge.ts",
    "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs"
  )

  $requiredTerms = Select-String -Path "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx" -Pattern "CASH_RECEIVED|Terminal Cash Submission|Payment Finality|Fiscal Document Recorded|Receipt Available|Exit Authorization Readback Contract Missing" -AllMatches
  if ($requiredTerms.Count -lt 6) {
    Write-Error "Cashier transaction state-machine terms were not all found in CashCapturePanel."
    exit 1
  }

  $visualSmokeRoute = Select-String -Path "src\AssistedPaymentTerminal.App\src\App.tsx","scripts\Invoke-CentralPmsCashReceiptVisualSmoke.ps1" -Pattern "transactionCompletionVisualSmoke=1|TransactionCompletionVisualSmokeShell" -AllMatches
  if ($visualSmokeRoute.Count -lt 2) {
    Write-Error "Transaction-completion visual-smoke route or launcher switch was not found."
    exit 1
  }

  $prohibited = "HikCentral|parkingfee/calculate|parkingfee/confirm|/v1/webpay|OPEN_GATE|GateCommand|issue-exit-authorization|consume.*authorization|CashDrawer"
  $matches = Select-String -Path $stateMachineFiles -Pattern $prohibited -AllMatches
  if ($matches) {
    $unexpected = $matches | Where-Object {
      $_.Line -notmatch "no live Central PMS, HikCentral, gate, or printer|no APT-usable Central PMS ExitAuthorization readback contract|does not infer ExitAuthorization|does not retrieve another receipt or change payment, fiscal, ExitAuthorization, HikCentral, gate, or cash-drawer state|cashDrawerEnabled"
    }
    if ($unexpected) {
      $unexpected | ForEach-Object {
        $lineText = if ($_.Line) { $_.Line.Trim() } else { "<unavailable>" }
        Write-Error "Prohibited authority or mutation usage found: $($_.Path):$($_.LineNumber): $lineText"
      }
      exit 1
    }
  }

  Write-Host "APT cashier transaction-completion UI proof completed successfully."
  Write-Host "The state machine begins from one durable CASH_RECEIVED tender identity."
  Write-Host "Payment, fiscal, receipt, and completion states remain separate."
  Write-Host "Receipt availability does not infer ExitAuthorization or local transaction completion."
  Write-Host "No direct HikCentral, WebPay, ExitAuthorization mutation, gate, or cash-drawer behavior was introduced."
}
finally {
  Pop-Location
}
