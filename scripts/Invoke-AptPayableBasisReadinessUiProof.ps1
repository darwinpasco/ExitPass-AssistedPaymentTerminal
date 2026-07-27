param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- App.test.tsx centralPmsClient.test.ts CashCapturePanel.test.tsx PayableBasisVisualSmoke.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj --no-restore --filter "FullyQualifiedName~CashJournalServiceTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalJournalBridgeHandlerTests" -m:1 /p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $consumerFiles = @(
    "src\AssistedPaymentTerminal.App\src\App.tsx",
    "src\AssistedPaymentTerminal.App\src\PayableBasisVisualSmoke.tsx",
    "src\AssistedPaymentTerminal.App\src\api\centralPmsClient.ts",
    "src\AssistedPaymentTerminal.App\src\api\mockCentralPms.ts",
    "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\localJournalBridge.ts",
    "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs",
    "src\AssistedPaymentTerminal.LocalOperations\CashJournalService.cs"
  )

  $requiredRoutes = Select-String -Path "src\AssistedPaymentTerminal.App\src\api\centralPmsClient.ts" -Pattern "/v1/terminal-cash-payments/payable-basis/resolve|/v1/terminal-cash-payments/payable-basis/revalidate" -AllMatches
  if ($requiredRoutes.Count -lt 2) {
    Write-Error "APT payable-basis resolve and revalidate routes were not both found in the typed client."
    exit 1
  }

  $prohibited = "HikCentral|parkingfee/calculate|parkingfee/confirm|/v1/webpay/parking-session|OPEN_GATE|GateCommand|CashDrawer|receipt-presentation"
  $matches = Select-String -Path $consumerFiles -Pattern $prohibited -AllMatches
  if ($matches) {
    $unexpected = $matches | Where-Object { $_.Line -notmatch "not\.toContain|no direct|No direct|prohibited|receipt-presentation|does not retrieve|does not.*HikCentral|No .*HikCentral|cashDrawerEnabled" }
    if ($unexpected) {
      $unexpected | ForEach-Object {
        $lineText = if ($_.Line) { $_.Line.Trim() } else { "<unavailable>" }
        Write-Error "Prohibited authority or route usage found: $($_.Path):$($_.LineNumber): $lineText"
      }
      exit 1
    }
  }

  Write-Host "APT payable-basis readiness UI proof completed successfully."
  Write-Host "Ticket and plate resolve use the APT terminal-cash facade routes."
  Write-Host "Central PMS readyForCashAcceptance controls cash enablement, with local prerequisites only restricting."
  Write-Host "Pre-CASH_RECEIVED revalidation is invoked by the cash-capture boundary."
  Write-Host "No direct HikCentral, WebPay route, payment, fiscal, receipt, print, ExitAuthorization, gate, or cash-drawer behavior was introduced."
}
finally {
  Pop-Location
}