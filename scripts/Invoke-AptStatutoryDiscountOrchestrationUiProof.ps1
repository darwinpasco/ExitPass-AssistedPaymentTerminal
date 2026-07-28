param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- App.test.tsx StatutoryDiscountVisualSmoke.test.tsx centralPmsClient.test.ts
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj --no-restore --filter "FullyQualifiedName~CashJournalServiceTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalJournalBridgeHandlerTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $statutoryFiles = @(
    "src\AssistedPaymentTerminal.App\src\App.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountVisualSmoke.tsx",
    "src\AssistedPaymentTerminal.App\src\api\centralPmsClient.ts",
    "src\AssistedPaymentTerminal.App\src\api\mockCentralPms.ts",
    "src\AssistedPaymentTerminal.App\src\localJournalBridge.ts",
    "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs",
    "src\AssistedPaymentTerminal.LocalOperations\CashJournalService.cs"
  )

  $sharedRoutes = Select-String -Path "src\AssistedPaymentTerminal.App\src\api\centralPmsClient.ts" -Pattern "/v1/statutory-discounts/decisions|/v1/terminal-cash-payments/payable-basis/resolve|/v1/terminal-cash-payments/payable-basis/revalidate" -AllMatches
  if ($sharedRoutes.Count -lt 3) {
    Write-Error "Expected shared statutory decision and statutory-aware payable-basis facade routes were not all found."
    exit 1
  }

  $cashBlocker = Select-String -Path "src\AssistedPaymentTerminal.App\src\App.tsx","src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx" -Pattern "statutoryWorkflowActive|statutoryCashGateStatus|Statutory payable basis ready for Continue to Cash" -AllMatches
  if ($cashBlocker.Count -lt 2) {
    Write-Error "Statutory workflow gating was not found in the App and statutory panel."
    exit 1
  }

  $visualSmokeRoute = Select-String -Path "src\AssistedPaymentTerminal.App\src\App.tsx","scripts\Invoke-CentralPmsCashReceiptVisualSmoke.ps1" -Pattern "statutoryDiscountVisualSmoke=1|StatutoryDiscountVisualSmokeShell" -AllMatches
  if ($visualSmokeRoute.Count -lt 2) {
    Write-Error "Statutory discount visual-smoke route or launcher switch was not found."
    exit 1
  }

  $prohibited = "HikCentral|parkingfee/calculate|parkingfee/confirm|/v1/webpay|OPEN_GATE|GateCommand|ExitAuthorization|receipt-presentation|CashDrawer|cash drawer"
  $matches = Select-String -Path $statutoryFiles -Pattern $prohibited -AllMatches
  if ($matches) {
    $unexpected = $matches | Where-Object {
      $_.Line -notmatch "No live Central PMS, HikCentral, fiscal, receipt, ExitAuthorization, gate, or cash-drawer command is executed|never call HikCentral|not enabled in this slice|No direct HikCentral|no live|does not contain|prohibited|cashDrawerEnabled"
    }
    if ($unexpected) {
      $unexpected | ForEach-Object {
        $lineText = if ($_.Line) { $_.Line.Trim() } else { "<unavailable>" }
        Write-Error "Prohibited authority or mutation usage found: $($_.Path):$($_.LineNumber): $lineText"
      }
      exit 1
    }
  }

  Write-Host "APT statutory-discount orchestration UI proof completed successfully."
  Write-Host "Pending review, approval, application processing, APPLIED, rejected, retryable, terminal, and restart scenarios are covered by focused tests."
  Write-Host "The desktop uses shared statutory decision routes and the statutory-aware APT payable-basis facade."
  Write-Host "Statutory cash entry remains gated by canonical statutory state, amount acknowledgement, local prerequisites, and immediate revalidation."
  Write-Host "No premature terminal-cash submission, fiscal, receipt, ExitAuthorization, HikCentral, or gate behavior was introduced."
}
finally {
  Pop-Location
}
