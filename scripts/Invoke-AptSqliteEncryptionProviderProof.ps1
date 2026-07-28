param(
  [string]$OutputRoot
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$proofProject = Join-Path $repoRoot "tools\AssistedPaymentTerminal.SqliteEncryptionProviderProof\AssistedPaymentTerminal.SqliteEncryptionProviderProof.csproj"
$testProject = Join-Path $repoRoot "tests\AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests\AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests.csproj"
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
  Join-Path $env:TEMP ("exitpass-apt-sqlite-encryption-proof-" + [Guid]::NewGuid().ToString("N"))
} else {
  [System.IO.Path]::GetFullPath($OutputRoot)
}

if ($outputRoot.StartsWith($repoRoot.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Proof output root must be outside the Git repository."
}

$publishRoot = Join-Path $outputRoot "publish-win-x64"
$databasePath = Join-Path $outputRoot "published-proof.db"

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

dotnet restore $repoRoot
dotnet build $proofProject --no-restore
dotnet test $testProject --no-restore -m:1 /p:UseSharedCompilation=false
dotnet publish $proofProject -c Release -r win-x64 --self-contained false --no-restore -o $publishRoot

$proofExe = Join-Path $publishRoot "AssistedPaymentTerminal.SqliteEncryptionProviderProof.exe"
if (-not (Test-Path $proofExe)) {
  throw "Published proof executable was not found: $proofExe"
}

& $proofExe --database-path $databasePath --keep-artifacts
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

$nativeDependencies = Get-ChildItem -Path $publishRoot -Recurse -File |
  Where-Object { $_.Name -in @("e_sqlcipher.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll") } |
  Select-Object -ExpandProperty FullName

if (-not ($nativeDependencies | Where-Object { [System.IO.Path]::GetFileName($_) -eq "e_sqlcipher.dll" })) {
  throw "Published output does not include e_sqlcipher.dll."
}

$licensePackages = @(
  (Join-Path $env:USERPROFILE ".nuget\packages\sqlitepclraw.bundle_e_sqlcipher\2.1.11\sqlitepclraw.bundle_e_sqlcipher.nuspec"),
  (Join-Path $env:USERPROFILE ".nuget\packages\sqlitepclraw.lib.e_sqlcipher\2.1.11\sqlitepclraw.lib.e_sqlcipher.nuspec"),
  (Join-Path $env:USERPROFILE ".nuget\packages\sqlitepclraw.core\2.1.11\sqlitepclraw.core.nuspec"),
  (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.data.sqlite.core\8.0.21\microsoft.data.sqlite.core.nuspec")
)

Write-Host "Package license inventory:"
foreach ($package in $licensePackages) {
  if (-not (Test-Path $package)) {
    throw "Expected package metadata missing: $package"
  }

  $content = Get-Content -Raw -LiteralPath $package
  $license = if ($content -match "<license[^>]*>(?<license>[^<]+)</license>") {
    $Matches["license"]
  } elseif ($content -match "<licenseUrl>(?<license>[^<]+)</licenseUrl>") {
    $Matches["license"]
  } else {
    "UNKNOWN"
  }

  Write-Host "- $([System.IO.Path]::GetFileNameWithoutExtension($package)): $license"
  if ($license -eq "UNKNOWN") {
    throw "Package license metadata was not found for $package"
  }
}

$forbiddenPatterns = @(
  "BEGIN PRIVATE KEY",
  "Authorization:",
  "Bearer ",
  "ApiKey",
  "Password="
)

foreach ($pattern in $forbiddenPatterns) {
  $scanRoots = @(
    "Directory.Packages.props",
    "tools\AssistedPaymentTerminal.SqliteEncryptionProviderProof",
    "tests\AssistedPaymentTerminal.SqliteEncryptionProviderProof.Tests",
    "scripts\Invoke-AptSqliteEncryptionProviderProof.ps1",
    "docs\security"
  ) |
    ForEach-Object { Join-Path $repoRoot $_ } |
    Where-Object { Test-Path $_ }

  $matches = Get-ChildItem -Path $scanRoots -Recurse -File |
    Where-Object { $_.FullName -notmatch "\\(bin|obj|dist|node_modules|TestResults)\\" } |
    Where-Object { $_.FullName -ne $PSCommandPath } |
    Select-String -Pattern $pattern -ErrorAction SilentlyContinue
  if ($matches) {
    $firstMatch = $matches | Select-Object -First 1
    throw "Forbidden secret-like pattern found in source: $pattern at $($firstMatch.Path):$($firstMatch.LineNumber)"
  }
}

$publishedMatches = Get-ChildItem -Path $publishRoot -Recurse -File |
  Where-Object { $_.Length -lt 10MB } |
  Select-String -Pattern "BEGIN PRIVATE KEY|Authorization:|Bearer |ApiKey|Password=" -ErrorAction SilentlyContinue

if ($publishedMatches) {
  throw "Forbidden secret-like pattern found in published proof output."
}

Write-Host "Native dependency inventory:"
$nativeDependencies | ForEach-Object { Write-Host "- $([System.IO.Path]::GetFileName($_))" }
Write-Host "APT SQLite encryption provider proof completed successfully."
