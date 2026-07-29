param(
  [string]$PublishRoot
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
  Join-Path $env:TEMP ("exitpass-apt-encrypted-db-publish-" + [Guid]::NewGuid().ToString("N"))
} else {
  [System.IO.Path]::GetFullPath($PublishRoot)
}

if ($publishRoot.StartsWith($repoRoot.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Publish proof output root must be outside the Git repository."
}

dotnet build (Join-Path $repoRoot "tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj") `
  -c Release `
  -m:1 `
  /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

dotnet test (Join-Path $repoRoot "tests\AssistedPaymentTerminal.LocalOperations.Tests\AssistedPaymentTerminal.LocalOperations.Tests.csproj") `
  -c Release `
  --no-build `
  --filter "FullyQualifiedName~LocalDatabaseEncryptionTests" `
  -m:1 `
  /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

dotnet publish (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\AssistedPaymentTerminal.Desktop.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $publishRoot
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

$requiredNativeDependencies = @(
  "e_sqlcipher.dll",
  "SQLitePCLRaw.batteries_v2.dll",
  "SQLitePCLRaw.core.dll"
)

foreach ($dependency in $requiredNativeDependencies) {
  $match = Get-ChildItem -Path $publishRoot -Recurse -File -Filter $dependency -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $match) {
    throw "Published APT output is missing required SQLCipher dependency: $dependency"
  }
}

$forbiddenPatterns = @(
  ("BEGIN " + "PRIVATE KEY"),
  ("Author" + "ization: Bear" + "er "),
  ("Bear" + "er "),
  ("Api" + "Key"),
  ("Pass" + "word="),
  ("PRAGMA " + "key")
)

$publishedFiles = Get-ChildItem -Path $publishRoot -Recurse -File |
  Where-Object { $_.Length -lt 10MB }

foreach ($pattern in $forbiddenPatterns) {
  $matches = $publishedFiles | Select-String -SimpleMatch -CaseSensitive -Pattern $pattern -ErrorAction SilentlyContinue
  if ($matches) {
    throw "Forbidden secret-like pattern found in published APT output."
  }
}

powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts\check-no-secrets.ps1")
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

Write-Host "APT encrypted database creation proof completed successfully."
Write-Host "Published output: $publishRoot"
Write-Host "Native dependency inventory:"
$requiredNativeDependencies | ForEach-Object { Write-Host "- $_" }
