param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- StatutoryEvidencePanel.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- StatutoryDiscountVisualSmoke.test.tsx -t evidence
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  dotnet test tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj `
    --configuration Release `
    --no-build `
    --filter "FullyQualifiedName~StatutoryEvidence"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $clientSource = Get-Content -Raw "src\AssistedPaymentTerminal.Desktop\CentralPmsStatutoryEvidenceClient.cs"
  $requiredRoutes = @(
    "/v1/apt/statutory-discounts/evidence/bootstrap",
    "/v1/apt/statutory-discounts/evidence/status",
    "/v1/apt/statutory-discounts/evidence/revalidate",
    "/v1/apt/statutory-discounts/evidence/upload-sessions"
  )
  foreach ($route in $requiredRoutes) {
    if (-not $clientSource.Contains($route)) {
      throw "Missing merged I-016 APT route: $route"
    }
  }
  foreach ($marker in @("HttpMethod.Put", "/finalize", "X-ExitPass-Service-Identity-Id", "HttpCompletionOption.ResponseHeadersRead")) {
    if (-not $clientSource.Contains($marker)) {
      throw "Missing secure evidence client marker: $marker"
    }
  }

  $frontendFiles = @(
    "src\AssistedPaymentTerminal.App\src\App.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx",
    "src\AssistedPaymentTerminal.App\src\StatutoryEvidencePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\statutoryEvidenceBridge.ts",
    "src\AssistedPaymentTerminal.App\src\api\centralPmsTypes.ts"
  )
  $privilegedFrontend = Select-String -Path $frontendFiles -Pattern @(
    '"Authorization"',
    "'Authorization'",
    "X-ExitPass-Permissions",
    "X-ExitPass-Service-Identity-Id",
    "APT_CENTRAL_PMS_SERVICE_IDENTITY_ID"
  ) -AllMatches
  if ($privilegedFrontend) {
    throw "A privileged Central PMS header or host credential marker was found in the frontend evidence boundary."
  }

  $productionFiles = @(
    "src\AssistedPaymentTerminal.Desktop\CentralPmsStatutoryEvidenceClient.cs",
    "src\AssistedPaymentTerminal.Desktop\StatutoryEvidenceBridgeHandler.cs",
    "src\AssistedPaymentTerminal.App\src\StatutoryEvidencePanel.tsx",
    "src\AssistedPaymentTerminal.App\src\statutoryEvidenceBridge.ts"
  )
  $prohibitedIntegration = Select-String -Path $productionFiles -Pattern "HikCentral|MinIO|AmazonS3|S3Client|ListObjects|GetObject|scanner endpoint|/v1/webpay|operator-console|management-platform" -AllMatches
  if ($prohibitedIntegration) {
    throw "A prohibited direct integration was found in the J-006 production boundary."
  }

  $persistenceChanges = git diff --name-only -- "src/AssistedPaymentTerminal.LocalOperations" "**/*Migration*"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  if ($persistenceChanges) {
    throw "J-006 changed LocalOperations persistence or schema files unexpectedly: $($persistenceChanges -join ', ')"
  }

  $appSource = Get-Content -Raw "src\AssistedPaymentTerminal.App\src\App.tsx"
  foreach ($marker in @("statutoryEvidenceBridge.revalidate", "statutoryEvidenceReadiness", "readyForAptPreCash", "STALE_LOCAL_STATE")) {
    if (-not $appSource.Contains($marker)) {
      throw "Missing J-006 restart or pre-cash marker: $marker"
    }
  }

  Write-Host "APT statutory-evidence UI proof completed successfully."
  Write-Host "Focused frontend evidence and statutory CASH_RECEIVED tests passed."
  Write-Host "Focused desktop bridge and Central PMS evidence client tests passed."
  Write-Host "All six merged I-016 route forms are represented by the bounded host client."
  Write-Host "Service identity remains host-owned; no privileged frontend headers were found."
  Write-Host "No LocalOperations schema or persistence source changed."
  Write-Host "No direct HikCentral, object-storage administration, scanner, WebPay, Operator Console, or Management Platform path was found."
}
finally {
  Pop-Location
}
