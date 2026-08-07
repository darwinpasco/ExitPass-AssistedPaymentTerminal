param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- cashierSafeReferences.test.ts CashierSafePresentation.test.tsx statutoryIdMasking.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $presentationFiles = @(
    "src\AssistedPaymentTerminal.App\src\App.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryEvidencePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\ReceiptVisualSmoke.tsx",
    "src\AssistedPaymentTerminal.App\src\TransactionCompletionVisualSmoke.tsx"
  )

  $unsafeLabels = Select-String -Path $presentationFiles -Pattern @(
    "Parking session ID",
    "Local tender ID",
    "Payment-attempt ID",
    "Payment-confirmation ID",
    "POS fiscal-document ID",
    "Print job ID",
    "Correlation ID"
  ) -SimpleMatch
  if ($unsafeLabels) {
    $unsafeLabels | ForEach-Object { Write-Error "Unsafe cashier identifier label remains: $($_.Path):$($_.LineNumber)" }
    exit 1
  }

  Write-Host "APT cashier-safe reference UI proof completed successfully."
  Write-Host "Deterministic payable-basis, statutory, evidence, cash, receipt, and print surfaces rendered without full GUID exposure."
  Write-Host "Canonical identifiers remain available to API, idempotency, SQLite recovery, and reconciliation code paths."
}
finally {
  Pop-Location
}
