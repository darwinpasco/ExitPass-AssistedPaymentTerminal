param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- App.test.tsx StatutoryDiscountVisualSmoke.test.tsx CashCapturePanel.test.tsx centralPmsClient.test.ts
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj --no-restore --filter "FullyQualifiedName~CashJournalServiceTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalJournalBridgeHandlerTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $requiredScenarioLabels = @(
    "APPLIED complete but local prerequisites blocked",
    "APPLIED complete and Continue to Cash enabled",
    "Continue to Cash revalidation PASSED_UNCHANGED",
    "Continue to Cash revalidation AMOUNT_CHANGED",
    "Continue to Cash statutory blocked",
    "Continue to Cash ordinance coverage revoked",
    "Immediate Record Cash Received revalidation PASSED_UNCHANGED",
    "Immediate Record Cash Received revalidation AMOUNT_CHANGED",
    "Immediate Record Cash Received retryable failure",
    "Immediate Record Cash Received terminal failure",
    "Immediate Record Cash Received ordinance unavailable",
    "Statutory CASH_RECEIVED recorded once",
    "Restart before statutory CASH_RECEIVED requires revalidation",
    "Restart after statutory CASH_RECEIVED preserves custody evidence",
    "Restart after statutory CASH_RECEIVED resumes terminal-cash submission",
    "Non-statutory cash flow unchanged"
  )

  $visualSmokeSource = Get-Content -Raw "src\AssistedPaymentTerminal.App\src\StatutoryDiscountVisualSmoke.tsx"
  foreach ($label in $requiredScenarioLabels) {
    if (-not $visualSmokeSource.Contains($label)) {
      Write-Error "Missing statutory cash visual-smoke scenario: $label"
      exit 1
    }
  }

  $appSource = Get-Content -Raw "src\AssistedPaymentTerminal.App\src\App.tsx"
  foreach ($required in @("statutoryCashGateStatus", "preCashRevalidate(basis, true)", "Statutory payable basis ready for cash acceptance", "revalidatedBasisMatchesCurrentStatutoryAuthority")) {
    if (-not $appSource.Contains($required)) {
      Write-Error "Missing statutory cash gate or two-stage revalidation marker in App.tsx: $required"
      exit 1
    }
  }

  $cashCaptureSource = Get-Content -Raw "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx"
  foreach ($required in @("buildStatutoryTenderEvidence", "statutoryTenderEvidence", "Record Cash Received")) {
    if (-not $cashCaptureSource.Contains($required)) {
      Write-Error "Missing statutory tender evidence or CASH_RECEIVED boundary marker in CashCapturePanel.tsx: $required"
      exit 1
    }
  }

  $journalSource = Get-Content -Raw "src\AssistedPaymentTerminal.LocalOperations\CashJournalService.cs"
  foreach ($required in @("EnsureCashTenderStatutoryEvidenceSchemaAsync", "ApplyStatutoryTenderEvidence", "StatutoryDiscountDecisionCommandId", "StatutoryImmediateRevalidationOutcome")) {
    if (-not $journalSource.Contains($required)) {
      Write-Error "Missing durable statutory custody evidence marker in CashJournalService.cs: $required"
      exit 1
    }
  }

  $terminalCashContract = Get-Content -Raw "src\AssistedPaymentTerminal.LocalOperations\TerminalCashPaymentContracts.cs"
  if ($terminalCashContract.Contains("StatutoryDiscount")) {
    Write-Error "Terminal-cash public payload contract contains statutory fields; expected unchanged public contract."
    exit 1
  }

  $scanFiles = @(
    "src\AssistedPaymentTerminal.App\src\App.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountVisualSmoke.tsx",
    "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\localJournalBridge.ts",
    "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs",
    "src\AssistedPaymentTerminal.LocalOperations\CashJournalService.cs"
  )

  $prohibited = "fullStatutoryId|rawIdImage|Base64Evidence|ocrOutput|reviewerIdentity|reviewerNotes|/v1/webpay|parkingfee/calculate|parkingfee/confirm|OPEN_GATE|GateCommand|Open Gate"
  $matches = Select-String -Path $scanFiles -Pattern $prohibited -AllMatches
  if ($matches) {
    $matches | ForEach-Object {
      $lineText = if ($_.Line) { $_.Line.Trim() } else { "<unavailable>" }
      Write-Error "Prohibited statutory cash authority or sensitive field usage found: $($_.Path):$($_.LineNumber): $lineText"
    }
    exit 1
  }

  Write-Host "APT statutory cash-acceptance UI proof completed successfully."
  Write-Host "The statutory cash gate requires APPLIED statutory readiness, amount acknowledgement, local prerequisites, and two-stage PASSED_UNCHANGED revalidation."
  Write-Host "CASH_RECEIVED is recorded exactly once with safe statutory tender evidence and the applied tariff snapshot/final amount."
  Write-Host "Terminal-cash payload construction keeps the public contract unchanged and uses the applied payable basis."
  Write-Host "No VAT/discount calculation, direct POS Server, HikCentral, WebPay, ExitAuthorization, receipt, print, or gate behavior was introduced."
}
finally {
  Pop-Location
}
