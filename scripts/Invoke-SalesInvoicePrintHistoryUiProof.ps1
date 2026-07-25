param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  dotnet test tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj --no-restore --filter "TerminalCashReceiptPrintJobTests"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj --no-restore --filter "CashReceiptPrintBridgeHandlerTests"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- CashCapturePanel.test.tsx ReceiptVisualSmoke.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $historyFiles = @(
    "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\localJournalBridge.ts",
    "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs",
    "src\AssistedPaymentTerminal.LocalOperations\TerminalCashReceiptPrintJobService.cs"
  )

  $prohibited = "receipt-presentation|terminal-cash-payments|SubmitCentralPmsCashReceiptPrintAsync\(|SubmitAsync\(document|ExitAuthorization|HikCentral|OPEN_GATE|CashDrawer"
  $matches = Select-String -Path $historyFiles -Pattern $prohibited -AllMatches |
    Where-Object {
      $_.Line -match "PrintHistory|SalesInvoicePrintHistory|getSalesInvoicePrintHistory|GetSalesInvoicePrintHistory|GetJobsFor"
    }

  if ($matches) {
    $matches | ForEach-Object { Write-Error "Prohibited authority or mutation call found in print-history path: $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    exit 1
  }

  Write-Host "Sales Invoice print history reads local SQLite print-job evidence only."
  Write-Host "Original/Reprint, copy sequence, failure, unknown outcome, and reconciliation indicators are visible."
  Write-Host "Opening, filtering, detail read, and restart-oriented history reads create no print job and call no printer."
  Write-Host "No Central PMS, POS Server, receipt retrieval, payment, fiscal, ExitAuthorization, HikCentral, gate, or cash-drawer side effects were introduced."
  Write-Host "Development print-history scenarios are excluded from production by the existing receiptVisualSmoke development gate."
  Write-Host "Sales Invoice print history UI proof completed successfully."
}
finally {
  Pop-Location
}
