param()

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
  npm.cmd run test --workspace src/AssistedPaymentTerminal.App -- statutoryIdMasking.test.tsx
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $panelPath = "src\AssistedPaymentTerminal.App\src\StatutoryDiscountPanel.tsx"
  $maskPath = "src\AssistedPaymentTerminal.App\src\statutoryIdMasking.ts"

  if (Select-String -Path $panelPath -Pattern 'maskedIdReference: "SC-****-0001"' -SimpleMatch) {
    Write-Error "The statutory form still seeds a manually masked identifier."
    exit 1
  }
  if (-not (Select-String -Path $maskPath -Pattern '"*".repeat(trimmed.length - 6)' -SimpleMatch)) {
    Write-Error "The authoritative middle-character masking rule is missing."
    exit 1
  }

  Write-Host "APT automatic statutory ID masking proof completed successfully."
  Write-Host "Raw synthetic input was supplied by the test; cashier presentation and submission used the derived first-two/last-four mask."
  Write-Host "Manual asterisk entry was not required and raw input was absent after the masking boundary."
}
finally {
  Pop-Location
}
