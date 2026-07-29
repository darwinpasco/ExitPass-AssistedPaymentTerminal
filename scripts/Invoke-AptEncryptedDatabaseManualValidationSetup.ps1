param(
  [switch]$PreparePublish,
  [switch]$ForcePublish,
  [switch]$PrintManifest,
  [switch]$Launch,
  [switch]$Clean,
  [switch]$Force,
  [switch]$AllowActiveLocalState,
  [ValidateSet("Scenario2", "Scenario3", "Scenario4", "Scenario5", "Scenario6A", "Scenario6B", "Scenario6C", "Scenario6D")]
  [string]$Scenario,
  [switch]$Setup,
  [switch]$Verify,
  [switch]$Restore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$manualRoot = Join-Path $env:LOCALAPPDATA "ExitPass\AssistedPaymentTerminal\ManualEncryptionProof"
$localOperationsRoot = Join-Path $manualRoot "LocalOperations"
$databasePath = Join-Path $localOperationsRoot "cash-journal.db"
$envelopePath = Join-Path $localOperationsRoot "cash-journal.key"
$publishRoot = Join-Path $manualRoot "publish"
$applicationPath = Join-Path $publishRoot "AssistedPaymentTerminal.Desktop.exe"
$publishManifestPath = Join-Path $manualRoot "publish-manifest.json"
$runtimeDiagnosticPath = Join-Path $manualRoot "scenario6a-runtime-diagnostic.jsonl"
$normalDatabasePath = Join-Path $env:LOCALAPPDATA "ExitPass\AssistedPaymentTerminal\LocalOperations\cash-journal.db"
$stateInspectorProject = Join-Path $repoRoot "tools\AssistedPaymentTerminal.LocalOperations.Proof\AssistedPaymentTerminal.LocalOperations.Proof.csproj"
$scenarioStatePath = Join-Path $manualRoot "manual-validation-state.json"

function Assert-ManualRoot {
  $expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "ExitPass\AssistedPaymentTerminal\ManualEncryptionProof"))
  $actualRoot = [System.IO.Path]::GetFullPath($manualRoot)
  if (-not $actualRoot.Equals($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Manual proof root safety check failed."
  }
}

function Read-SharedFileBytes {
  param([Parameter(Mandatory = $true)][string]$LiteralPath)

  $stream = [System.IO.File]::Open(
    $LiteralPath,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
  try {
    $buffer = [byte[]]::new($stream.Length)
    $offset = 0
    while ($offset -lt $buffer.Length) {
      $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
      if ($read -eq 0) {
        break
      }

      $offset += $read
    }

    if ($offset -eq $buffer.Length) {
      return ,$buffer
    }

    $truncated = [byte[]]::new($offset)
    [Array]::Copy($buffer, $truncated, $offset)
    return ,$truncated
  }
  finally {
    $stream.Dispose()
  }
}

function Stop-IsolatedAptProcess {
  $processes = @(Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $applicationPath })

  foreach ($process in $processes) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  }

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $remaining = @(Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue |
      Where-Object { $_.Path -eq $applicationPath })
    if ($remaining.Count -eq 0) {
      return
    }

    Start-Sleep -Milliseconds 500
  }
}

function Remove-HarnessPublishDirectory {
  if (-not (Test-Path -LiteralPath $publishRoot)) {
    return
  }

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  do {
    try {
      Remove-Item -LiteralPath $publishRoot -Recurse -Force
      return
    }
    catch [System.IO.IOException] {
      Start-Sleep -Milliseconds 750
    }
    catch [System.UnauthorizedAccessException] {
      Start-Sleep -Milliseconds 750
    }
  } while ([DateTimeOffset]::UtcNow -lt $deadline)

  Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

function Get-HashOrMissing {
  param([Parameter(Mandatory = $true)][string]$LiteralPath)

  if (Test-Path -LiteralPath $LiteralPath) {
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
  }

  return "MISSING"
}

function Get-TextHash {
  param([Parameter(Mandatory = $true)][string]$Value)

  $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    $hash = $sha256.ComputeHash($bytes)
    return [BitConverter]::ToString($hash).Replace("-", "")
  }
  finally {
    $sha256.Dispose()
  }
}

function Get-DirectoryHash {
  param([Parameter(Mandatory = $true)][string]$LiteralPath)

  if (-not (Test-Path -LiteralPath $LiteralPath)) {
    return "MISSING"
  }

  $root = [System.IO.Path]::GetFullPath($LiteralPath)
  $rootPrefix = $root.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
  $entries = Get-ChildItem -LiteralPath $LiteralPath -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
      $fullName = [System.IO.Path]::GetFullPath($_.FullName)
      $relative = if ($fullName.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $fullName.Substring($rootPrefix.Length)
      } else {
        $_.Name
      }
      $relative = $relative.Replace("\", "/")
      "$relative=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }

  return Get-TextHash -Value ($entries -join "`n")
}

function Get-GitText {
  param([Parameter(Mandatory = $true)][string[]]$Arguments)

  $output = & git @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "git $($Arguments -join ' ') failed."
  }

  return ($output -join "`n")
}

function Get-CurrentSourceFingerprint {
  $appSourcePath = Join-Path $repoRoot "src\AssistedPaymentTerminal.App\src\App.tsx"
  $bridgeSourcePath = Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\LocalJournalBridgeHandler.cs"
  $status = Get-GitText -Arguments @("status", "--short", "--untracked-files=all")
  $diff = Get-GitText -Arguments @("diff", "--binary", "--no-ext-diff")

  return [pscustomobject]@{
    GitHead = Get-GitText -Arguments @("rev-parse", "HEAD")
    Branch = Get-GitText -Arguments @("branch", "--show-current")
    DirtyWorktreeFingerprint = Get-TextHash -Value ($status + "`n" + $diff)
    AppSourceHash = Get-HashOrMissing -LiteralPath $appSourcePath
    BridgeSourceHash = Get-HashOrMissing -LiteralPath $bridgeSourcePath
  }
}

function New-PublishManifest {
  param([Parameter(Mandatory = $true)][string]$PublishMode)

  $fingerprint = Get-CurrentSourceFingerprint
  $manifest = [pscustomobject]@{
    schemaVersion = 1
    gitHead = $fingerprint.GitHead
    branch = $fingerprint.Branch
    dirtyWorktreeFingerprint = $fingerprint.DirtyWorktreeFingerprint
    appSourcePath = "src/AssistedPaymentTerminal.App/src/App.tsx"
    appSourceHash = $fingerprint.AppSourceHash
    bridgeSourcePath = "src/AssistedPaymentTerminal.Desktop/LocalJournalBridgeHandler.cs"
    bridgeSourceHash = $fingerprint.BridgeSourceHash
    frontendBundleHash = Get-DirectoryHash -LiteralPath (Join-Path $publishRoot "wwwroot")
    publishedDesktopAssemblyHash = Get-HashOrMissing -LiteralPath (Join-Path $publishRoot "AssistedPaymentTerminal.Desktop.dll")
    publishedExecutableHash = Get-HashOrMissing -LiteralPath $applicationPath
    publishTimestamp = [DateTimeOffset]::UtcNow.ToString("O")
    buildConfiguration = "Release"
    runtimeIdentifier = "win-x64"
    selfContained = $false
    publishDirectory = $publishRoot
    publishMode = $PublishMode
  }

  New-Item -ItemType Directory -Force -Path $manualRoot | Out-Null
  $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $publishManifestPath -Encoding UTF8
  return $manifest
}

function Read-PublishManifest {
  if (-not (Test-Path -LiteralPath $publishManifestPath)) {
    return $null
  }

  return Get-Content -LiteralPath $publishManifestPath -Raw | ConvertFrom-Json
}

function Test-PublishCurrent {
  $manifest = Read-PublishManifest
  if ($null -eq $manifest) {
    return $false
  }

  $fingerprint = Get-CurrentSourceFingerprint
  return $manifest.gitHead -eq $fingerprint.GitHead `
    -and $manifest.branch -eq $fingerprint.Branch `
    -and $manifest.dirtyWorktreeFingerprint -eq $fingerprint.DirtyWorktreeFingerprint `
    -and $manifest.appSourceHash -eq $fingerprint.AppSourceHash `
    -and $manifest.bridgeSourceHash -eq $fingerprint.BridgeSourceHash `
    -and (Test-Path -LiteralPath $applicationPath) `
    -and (Test-Path -LiteralPath (Join-Path $publishRoot "wwwroot\index.html"))
}

function Write-PublishManifestSummary {
  $manifest = Read-PublishManifest
  if ($null -eq $manifest) {
    Write-Host "Publish manifest: MISSING"
    return
  }

  Write-Host "Publish manifest: $publishManifestPath"
  Write-Host "Git HEAD: $($manifest.gitHead)"
  Write-Host "Branch: $($manifest.branch)"
  Write-Host "Dirty worktree fingerprint: $($manifest.dirtyWorktreeFingerprint)"
  Write-Host "App.tsx source hash: $($manifest.appSourceHash)"
  Write-Host "LocalJournalBridgeHandler.cs source hash: $($manifest.bridgeSourceHash)"
  Write-Host "Frontend bundle hash: $($manifest.frontendBundleHash)"
  Write-Host "Published desktop assembly hash: $($manifest.publishedDesktopAssemblyHash)"
  Write-Host "Published executable hash: $($manifest.publishedExecutableHash)"
  Write-Host "Publish timestamp: $($manifest.publishTimestamp)"
  Write-Host "Build configuration: $($manifest.buildConfiguration)"
  Write-Host "Publish directory: $($manifest.publishDirectory)"
  Write-Host "Publish mode: $($manifest.publishMode)"
}

function Invoke-FreshPublish {
  param([Parameter(Mandatory = $true)][string]$PublishMode)

  Stop-IsolatedAptProcess
  Remove-HarnessPublishDirectory

  New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

  npm.cmd run app:build
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

  $manifest = New-PublishManifest -PublishMode $PublishMode
  if ($manifest.frontendBundleHash -eq "MISSING" -or $manifest.publishedDesktopAssemblyHash -eq "MISSING" -or $manifest.publishedExecutableHash -eq "MISSING") {
    throw "Fresh publish did not produce all required frontend and desktop artifacts."
  }

  Write-Host "Fresh publish completed."
  Write-PublishManifestSummary
}

function Assert-PublishCurrent {
  if (-not (Test-PublishCurrent)) {
    Write-PublishManifestSummary
    throw "Published APT artifacts are stale or missing. Run with -PreparePublish -ForcePublish, or include -ForcePublish with the scenario command."
  }

  Write-PublishManifestSummary
}

function Test-PlainSqliteHeader {
  param([Parameter(Mandatory = $true)][string]$LiteralPath)

  if (-not (Test-Path -LiteralPath $LiteralPath)) {
    return $false
  }

  $bytes = Read-SharedFileBytes -LiteralPath $LiteralPath
  if ($bytes.Length -lt 16) {
    return $false
  }

  $headerText = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 16)
  return $headerText -eq ("SQLite format 3" + [char]0)
}

function Invoke-StateInspector {
  $stateOutput = & dotnet run --project $stateInspectorProject -- --database-path $databasePath --inspect-state
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  $stateOutput | ForEach-Object { Write-Host $_ }
  $activeShiftCountLine = $stateOutput | Where-Object { $_ -like "active shift record count:*" } | Select-Object -First 1
  $activeCustodyCountLine = $stateOutput | Where-Object { $_ -like "active cash-custody record count:*" } | Select-Object -First 1
  return [pscustomobject]@{
    ActiveShiftCount = [int](($activeShiftCountLine -split ":", 2)[1].Trim())
    ActiveCustodyCount = [int](($activeCustodyCountLine -split ":", 2)[1].Trim())
  }
}

function Write-ScenarioHeader {
  param([Parameter(Mandatory = $true)][string]$Name)

  Write-Host ""
  Write-Host "Scenario: $Name"
  Write-Host "Isolated database path: $databasePath"
  Write-Host "Isolated envelope path: $envelopePath"
  Write-Host "Normal database path: $normalDatabasePath"
  Write-Host "Normal database hash before: $(Get-HashOrMissing -LiteralPath $normalDatabasePath)"
}

function Save-ScenarioState {
  param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$ExpectedStatus
  )

  $state = [pscustomobject]@{
    scenario = $Name
    expectedStatus = $ExpectedStatus
    databasePath = $databasePath
    envelopePath = $envelopePath
    normalDatabasePath = $normalDatabasePath
    databaseHash = Get-HashOrMissing -LiteralPath $databasePath
    envelopeHash = Get-HashOrMissing -LiteralPath $envelopePath
    normalDatabaseHash = Get-HashOrMissing -LiteralPath $normalDatabasePath
    capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
  }

  New-Item -ItemType Directory -Force -Path $manualRoot | Out-Null
  $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $scenarioStatePath -Encoding UTF8
}

function Read-ScenarioState {
  if (-not (Test-Path -LiteralPath $scenarioStatePath)) {
    throw "Scenario state was not found. Run the matching -Setup command first: $scenarioStatePath"
  }

  return Get-Content -LiteralPath $scenarioStatePath -Raw | ConvertFrom-Json
}

function Assert-NormalDatabaseUnchanged {
  param([Parameter(Mandatory = $true)]$State)

  $normalHashAfter = Get-HashOrMissing -LiteralPath $normalDatabasePath
  Write-Host "Normal database hash after:  $normalHashAfter"
  if ($State.normalDatabaseHash -ne $normalHashAfter) {
    throw "Normal APT LocalOperations database hash changed during isolated scenario."
  }
}

function Assert-ManualPathsMatchState {
  param([Parameter(Mandatory = $true)]$State)

  if ($State.databasePath -ne $databasePath -or $State.envelopePath -ne $envelopePath) {
    throw "Scenario state paths do not match the current isolated manual-proof paths."
  }
}

function Invoke-PublishedStartup {
  if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Published desktop executable not found. Rerun with -PreparePublish first."
  }

  Assert-PublishCurrent
  New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
  Remove-Item -LiteralPath $runtimeDiagnosticPath -Force -ErrorAction SilentlyContinue
  $env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
  $env:APT_LOCAL_DB_PATH = $databasePath
  $env:APT_MANUAL_PROOF_DIAGNOSTIC_PATH = $runtimeDiagnosticPath
  $process = Start-Process -FilePath $applicationPath -ArgumentList @(
    "--local-db-path=$databasePath",
    "--manual-proof-diagnostic-path=$runtimeDiagnosticPath"
  ) -PassThru
  Write-Host "Started APT process id: $($process.Id)"
  Start-Sleep -Seconds 3
  $process.Refresh()
  if ($process.HasExited) {
    throw "Published APT process exited during startup validation. Exit code: $($process.ExitCode)"
  }

  Write-Host "Process outcome: running"
}

function Initialize-ValidIsolatedStorage {
  Stop-IsolatedAptProcess
  if (Test-Path -LiteralPath $localOperationsRoot) {
    Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
  }

  New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
  Invoke-PublishedStartup

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ((Test-Path -LiteralPath $databasePath) -and (Test-Path -LiteralPath $envelopePath)) {
      break
    }

    Start-Sleep -Milliseconds 500
  }

  if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "Isolated encrypted database was not created: $databasePath"
  }

  if (-not (Test-Path -LiteralPath $envelopePath)) {
    throw "Isolated protected key envelope was not created: $envelopePath"
  }

  Stop-IsolatedAptProcess
  if (Test-PlainSqliteHeader -LiteralPath $databasePath) {
    throw "Isolated database exposes the standard SQLite plaintext header."
  }
}

function Seed-LocalOperationsState {
  param([Parameter(Mandatory = $true)][string]$Mode)

  $seedOutput = & dotnet run --project $stateInspectorProject -- --database-path $databasePath $Mode
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  $seedOutput | ForEach-Object { Write-Host $_ }
}

function Read-Scenario6ADiagnostic {
  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $runtimeDiagnosticPath) {
      $lines = @(Get-Content -LiteralPath $runtimeDiagnosticPath)
      if ($lines.Count -gt 0) {
        return $lines[-1] | ConvertFrom-Json
      }
    }

    Start-Sleep -Milliseconds 500
  }

  throw "Scenario6A runtime diagnostic was not written: $runtimeDiagnosticPath"
}

function Assert-Scenario6ADiagnostic {
  $diagnostic = Read-Scenario6ADiagnostic

  Write-Host "Runtime diagnostic path: $runtimeDiagnosticPath"
  Write-Host "Configured cashier shift ID: $($diagnostic.configuredCashierShiftId)"
  Write-Host "Shift filter sent by React: $($diagnostic.shiftFilterSent)"
  Write-Host "Bridge request cashierId: $($diagnostic.bridgeRequestScope.cashierId)"
  Write-Host "Bridge request cashierShiftId: $($diagnostic.bridgeRequestScope.cashierShiftId)"
  Write-Host "Bridge request terminalId: $($diagnostic.bridgeRequestScope.terminalId)"
  Write-Host "Bridge request siteId: $($diagnostic.bridgeRequestScope.siteId)"
  Write-Host "Bridge request siteGroupId: $($diagnostic.bridgeRequestScope.siteGroupId)"
  Write-Host "Bridge request posServerId: $($diagnostic.bridgeRequestScope.posServerId)"
  Write-Host "Bridge returned active shift ID: $($diagnostic.bridgeReturnedActiveShiftId)"
  Write-Host "Bridge returned active shift status: $($diagnostic.bridgeReturnedActiveShiftStatus)"
  Write-Host "React received active shift ID: $($diagnostic.reactReceivedActiveShiftId)"
  Write-Host "React received active shift status: $($diagnostic.reactReceivedActiveShiftStatus)"
  Write-Host "React rendered shift label: $($diagnostic.reactRenderedShiftLabel)"
  Write-Host "Active custody ID: $($diagnostic.activeCustodyId)"
  Write-Host "Active custody status: $($diagnostic.activeCustodyStatus)"
  Write-Host "Cash blocked without custody: $($diagnostic.cashBlockedWithoutCustody)"

  if ($diagnostic.shiftFilterSent -ne $false) {
    throw "Scenario6A React sent a configured shift filter."
  }

  if ($diagnostic.bridgeReturnedActiveShiftId -ne "SHIFT-DEV-20260714-A" -or $diagnostic.bridgeReturnedActiveShiftStatus -ne "Open") {
    throw "Scenario6A bridge did not return the durable active shift."
  }

  if ($diagnostic.reactReceivedActiveShiftId -ne "SHIFT-DEV-20260714-A" -or $diagnostic.reactReceivedActiveShiftStatus -ne "Open") {
    throw "Scenario6A React did not receive the durable active shift."
  }

  if ($diagnostic.reactRenderedShiftLabel -ne "OPEN") {
    throw "Scenario6A React did not map the durable active shift to the OPEN label."
  }

  if ($diagnostic.activeCustodyId -ne $null -or $diagnostic.cashBlockedWithoutCustody -ne $true) {
    throw "Scenario6A did not preserve the no-custody cash-blocked state."
  }
}

Assert-ManualRoot

Write-Host "APT encrypted database manual validation setup"
Write-Host "Repository: $($repoRoot.Path)"
Write-Host "Manual proof root: $manualRoot"
Write-Host "Isolated database path: $databasePath"
Write-Host "Isolated envelope path: $envelopePath"
Write-Host "Publish root: $publishRoot"
Write-Host "Application path: $applicationPath"
Write-Host "Publish manifest path: $publishManifestPath"
Write-Host "Runtime diagnostic path: $runtimeDiagnosticPath"

if ($Clean) {
  if (-not $Force) {
    throw "Refusing to clean manual proof root without -Force. Target: $manualRoot"
  }

  if (Test-Path -LiteralPath $manualRoot) {
    Write-Host "Removing isolated manual proof root: $manualRoot"
    Remove-Item -LiteralPath $manualRoot -Recurse -Force
  }
}

if ($PreparePublish) {
  if ($ForcePublish -or -not (Test-PublishCurrent)) {
    Invoke-FreshPublish -PublishMode ($(if ($ForcePublish) { "ForcedFresh" } else { "SourceFingerprintChanged" }))
  } else {
    Write-Host "Published artifacts are current; reusing existing publish."
    Write-PublishManifestSummary
  }
}

if ($PrintManifest) {
  Write-PublishManifestSummary
}

Write-Host ""
Write-Host "Manual startup command:"
Write-Host ('$env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"')
Write-Host ('$env:APT_LOCAL_DB_PATH = "' + $databasePath + '"')
Write-Host ('$env:APT_MANUAL_PROOF_DIAGNOSTIC_PATH = "' + $runtimeDiagnosticPath + '"')
Write-Host ('Start-Process -FilePath "' + $applicationPath + '" -ArgumentList @("--local-db-path=' + $databasePath + '", "--manual-proof-diagnostic-path=' + $runtimeDiagnosticPath + '") -PassThru')
Write-Host ""
Write-Host "Manual stop command:"
Write-Host ('Get-Process AssistedPaymentTerminal.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "' + $applicationPath + '" } | Stop-Process')
Write-Host ""
Write-Host "Manual cleanup command:"
Write-Host ('powershell -ExecutionPolicy Bypass -File scripts\Invoke-AptEncryptedDatabaseManualValidationSetup.ps1 -Clean -Force')

if ($ForcePublish -and -not $PreparePublish) {
  Invoke-FreshPublish -PublishMode "ScenarioForcedFresh"
}

if ($Scenario) {
  Write-ScenarioHeader -Name $Scenario
  Assert-PublishCurrent

  if ($Setup) {
    switch ($Scenario) {
      "Scenario2" {
        Write-Host "Expected precondition: existing envelope, missing database."
        Initialize-ValidIsolatedStorage
        Remove-Item -LiteralPath $databasePath -Force
        Save-ScenarioState -Name $Scenario -ExpectedStatus "EnvelopeExistsDatabaseMissing"
      }
      "Scenario3" {
        Write-Host "Expected precondition: encrypted database exists, envelope missing."
        Initialize-ValidIsolatedStorage
        $backupPath = Join-Path $localOperationsRoot "cash-journal.key.scenario3-backup"
        Move-Item -LiteralPath $envelopePath -Destination $backupPath -Force
        Save-ScenarioState -Name $Scenario -ExpectedStatus "DatabaseExistsEnvelopeMissing"
      }
      "Scenario4" {
        Write-Host "Expected precondition: encrypted database exists, malformed envelope."
        Initialize-ValidIsolatedStorage
        $backupPath = Join-Path $localOperationsRoot "cash-journal.key.scenario4-valid-backup"
        Copy-Item -LiteralPath $envelopePath -Destination $backupPath -Force
        Set-Content -LiteralPath $envelopePath -Value "{ malformed manual validation envelope" -Encoding UTF8
        Save-ScenarioState -Name $Scenario -ExpectedStatus "MalformedEnvelope"
      }
      "Scenario5" {
        Write-Host "Expected precondition: isolated plaintext SQLite fixture, no envelope."
        Stop-IsolatedAptProcess
        if (Test-Path -LiteralPath $localOperationsRoot) {
          Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
        Seed-LocalOperationsState -Mode "--create-plaintext"
        Save-ScenarioState -Name $Scenario -ExpectedStatus "LegacyPlaintextDatabase"
      }
      "Scenario6A" {
        Write-Host "Expected precondition: encrypted persistence with active shift only."
        Stop-IsolatedAptProcess
        if (Test-Path -LiteralPath $localOperationsRoot) {
          Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
        Seed-LocalOperationsState -Mode "--seed-active-shift-only"
        Save-ScenarioState -Name $Scenario -ExpectedStatus "ActiveShiftOnly"
      }
      "Scenario6B" {
        Write-Host "Expected precondition: encrypted persistence with active shift and active custody."
        Stop-IsolatedAptProcess
        if (Test-Path -LiteralPath $localOperationsRoot) {
          Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
        Seed-LocalOperationsState -Mode "--seed-active-shift-and-custody"
        Save-ScenarioState -Name $Scenario -ExpectedStatus "ActiveShiftAndCustody"
      }
      "Scenario6C" {
        Write-Host "Expected precondition: encrypted persistence with a closed shift and no recoverable active custody."
        Stop-IsolatedAptProcess
        if (Test-Path -LiteralPath $localOperationsRoot) {
          Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
        Seed-LocalOperationsState -Mode "--seed-closed-shift"
        Save-ScenarioState -Name $Scenario -ExpectedStatus "ClosedShift"
      }
      "Scenario6D" {
        Write-Host "Expected precondition: encrypted persistence with active shift and active custody for repeated restart."
        Stop-IsolatedAptProcess
        if (Test-Path -LiteralPath $localOperationsRoot) {
          Remove-Item -LiteralPath $localOperationsRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
        Seed-LocalOperationsState -Mode "--seed-active-shift-and-custody"
        Save-ScenarioState -Name $Scenario -ExpectedStatus "RestartIdempotency"
      }
    }

    Write-Host "Database exists: $(Test-Path -LiteralPath $databasePath)"
    Write-Host "Envelope exists: $(Test-Path -LiteralPath $envelopePath)"
    Write-Host "Raw SQLite plaintext header present: $(Test-PlainSqliteHeader -LiteralPath $databasePath)"
    $state = Read-ScenarioState
    Assert-NormalDatabaseUnchanged -State $state
    Write-Host "$Scenario setup: PASS"
    return
  }

  if ($Launch -or $Verify) {
    $state = Read-ScenarioState
    Assert-ManualPathsMatchState -State $state
    if ($state.scenario -ne $Scenario) {
      throw "Scenario state was prepared for '$($state.scenario)', not '$Scenario'."
    }

    switch ($Scenario) {
      "Scenario2" {
        Write-Host "Expected behavior: recreate encrypted database with existing envelope; operational UI may mount only after encrypted persistence is ready; cash remains blocked without shift/custody."
        $envelopeHashBefore = Get-HashOrMissing -LiteralPath $envelopePath
        Invoke-PublishedStartup
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $databasePath)) {
          Start-Sleep -Milliseconds 500
        }

        if (-not (Test-Path -LiteralPath $databasePath)) {
          throw "Scenario2 did not recreate the encrypted database."
        }

        if ($envelopeHashBefore -ne (Get-HashOrMissing -LiteralPath $envelopePath)) {
          throw "Scenario2 replaced the existing key envelope."
        }

        if (Test-PlainSqliteHeader -LiteralPath $databasePath) {
          throw "Scenario2 recreated a plaintext database."
        }

        $localState = Invoke-StateInspector
        if ($localState.ActiveShiftCount -ne 0 -or $localState.ActiveCustodyCount -ne 0) {
          throw "Scenario2 fabricated active local operational state."
        }
      }
      "Scenario3" {
        Write-Host "Expected behavior: fail closed with database unchanged and no replacement envelope."
        $databaseHashBefore = $state.databaseHash
        Invoke-PublishedStartup
        if (Test-Path -LiteralPath $envelopePath) {
          throw "Scenario3 created a replacement key envelope."
        }

        if ($databaseHashBefore -ne (Get-HashOrMissing -LiteralPath $databasePath)) {
          throw "Scenario3 modified the encrypted database."
        }
      }
      "Scenario4" {
        Write-Host "Expected behavior: fail closed with malformed envelope and database unchanged."
        $databaseHashBefore = $state.databaseHash
        $envelopeHashBefore = Get-HashOrMissing -LiteralPath $envelopePath
        Invoke-PublishedStartup
        if ($databaseHashBefore -ne (Get-HashOrMissing -LiteralPath $databasePath)) {
          throw "Scenario4 modified the encrypted database."
        }

        if ($envelopeHashBefore -ne (Get-HashOrMissing -LiteralPath $envelopePath)) {
          throw "Scenario4 modified or replaced the malformed envelope."
        }
      }
      "Scenario5" {
        Write-Host "Expected behavior: detect plaintext database and fail closed without creating an envelope."
        $databaseHashBefore = $state.databaseHash
        if (-not (Test-PlainSqliteHeader -LiteralPath $databasePath)) {
          throw "Scenario5 plaintext precondition was not present."
        }

        Invoke-PublishedStartup
        if (-not (Test-PlainSqliteHeader -LiteralPath $databasePath)) {
          throw "Scenario5 changed the plaintext fixture header."
        }

        if ($databaseHashBefore -ne (Get-HashOrMissing -LiteralPath $databasePath)) {
          throw "Scenario5 modified the plaintext fixture."
        }

        if (Test-Path -LiteralPath $envelopePath) {
          throw "Scenario5 created a key envelope for a plaintext legacy fixture."
        }
      }
      "Scenario6A" {
        Write-Host "Expected behavior: active shift recovered, custody absent, cash blocked."
        Write-Host "Rendered UI verification remains manual: top summary must show the recovered active shift and must not show No active shift."
        Invoke-PublishedStartup
        $localState = Invoke-StateInspector
        if ($localState.ActiveShiftCount -ne 1 -or $localState.ActiveCustodyCount -ne 0) {
          throw "Scenario6A did not recover active-shift-only state."
        }

        Assert-Scenario6ADiagnostic
      }
      "Scenario6B" {
        Write-Host "Expected behavior: active shift and custody recovered without duplicates."
        Invoke-PublishedStartup
        $localState = Invoke-StateInspector
        if ($localState.ActiveShiftCount -ne 1 -or $localState.ActiveCustodyCount -ne 1) {
          throw "Scenario6B did not recover one active shift and one active custody session."
        }
      }
      "Scenario6C" {
        Write-Host "Expected behavior: closed shift is not recovered as active and custody is not treated as active."
        Invoke-PublishedStartup
        $localState = Invoke-StateInspector
        if ($localState.ActiveShiftCount -ne 0 -or $localState.ActiveCustodyCount -ne 0) {
          throw "Scenario6C recovered closed-shift state as active."
        }
      }
      "Scenario6D" {
        Write-Host "Expected behavior: repeated restart does not duplicate shift, custody, database, or envelope."
        Invoke-PublishedStartup
        $firstState = Invoke-StateInspector
        $firstDatabaseHash = Get-HashOrMissing -LiteralPath $databasePath
        $firstEnvelopeHash = Get-HashOrMissing -LiteralPath $envelopePath
        Stop-IsolatedAptProcess
        Invoke-PublishedStartup
        $secondState = Invoke-StateInspector
        if ($firstState.ActiveShiftCount -ne 1 -or $firstState.ActiveCustodyCount -ne 1 -or $secondState.ActiveShiftCount -ne 1 -or $secondState.ActiveCustodyCount -ne 1) {
          throw "Scenario6D did not preserve stable active shift/custody counts."
        }

        if ($firstDatabaseHash -ne (Get-HashOrMissing -LiteralPath $databasePath) -or $firstEnvelopeHash -ne (Get-HashOrMissing -LiteralPath $envelopePath)) {
          throw "Scenario6D changed the database or envelope hash during repeated restart."
        }
      }
    }

    Write-Host "Database exists: $(Test-Path -LiteralPath $databasePath)"
    Write-Host "Envelope exists: $(Test-Path -LiteralPath $envelopePath)"
    Write-Host "Raw SQLite plaintext header present: $(Test-PlainSqliteHeader -LiteralPath $databasePath)"
    Assert-NormalDatabaseUnchanged -State $state
    if ($Scenario -eq "Scenario6A") {
      Write-Host "Scenario6A database verification: PASS"
      Write-Host "Scenario6A bridge/React diagnostic verification: PASS"
      Write-Host "Scenario6A rendered UI visual verification: PENDING_MANUAL"
      Write-Host "Scenario6A overall: PENDING_MANUAL"
    } else {
      Write-Host "$Scenario verification: PASS"
    }
    return
  }

  if ($Restore) {
    Stop-IsolatedAptProcess
    switch ($Scenario) {
      "Scenario3" {
        $backupPath = Join-Path $localOperationsRoot "cash-journal.key.scenario3-backup"
        if (Test-Path -LiteralPath $backupPath) {
          Move-Item -LiteralPath $backupPath -Destination $envelopePath -Force
        }
      }
      "Scenario4" {
        $backupPath = Join-Path $localOperationsRoot "cash-journal.key.scenario4-valid-backup"
        if (Test-Path -LiteralPath $backupPath) {
          Copy-Item -LiteralPath $backupPath -Destination $envelopePath -Force
        }
      }
    }

    Write-Host "$Scenario restore: PASS"
    return
  }

  throw "Specify -Setup, -Launch, -Verify, or -Restore with -Scenario."
}

if ($Launch) {
  if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Published desktop executable not found. Rerun with -PreparePublish first."
  }

  $normalHashBefore = if (Test-Path -LiteralPath $normalDatabasePath) {
    (Get-FileHash -LiteralPath $normalDatabasePath -Algorithm SHA256).Hash
  } else {
    "MISSING"
  }

  New-Item -ItemType Directory -Force -Path $localOperationsRoot | Out-Null
  $env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
  $env:APT_LOCAL_DB_PATH = $databasePath
  $process = Start-Process -FilePath $applicationPath -ArgumentList "--local-db-path=$databasePath" -PassThru
  Write-Host "Started APT process id: $($process.Id)"

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ((Test-Path -LiteralPath $databasePath) -and (Test-Path -LiteralPath $envelopePath)) {
      break
    }

    Start-Sleep -Milliseconds 500
  }

  if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "Isolated encrypted database was not created within the startup wait window: $databasePath"
  }

  if (-not (Test-Path -LiteralPath $envelopePath)) {
    throw "Isolated protected key envelope was not created within the startup wait window: $envelopePath"
  }

  Write-Host "Isolated encrypted database created: $databasePath"
  Write-Host "Isolated protected key envelope created: $envelopePath"
  $process.Refresh()
  if ($process.HasExited) {
    throw "Published APT process exited during startup validation. Exit code: $($process.ExitCode)"
  }

  Write-Host "Published APT process remains running: $($process.Id)"

  $headerBytes = $null
  $headerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
  while ([DateTimeOffset]::UtcNow -lt $headerDeadline) {
    $candidateBytes = Read-SharedFileBytes -LiteralPath $databasePath
    if ($candidateBytes.Length -gt 0) {
      $headerBytes = $candidateBytes
      break
    }

    Start-Sleep -Milliseconds 250
  }

  if ($null -eq $headerBytes -or $headerBytes.Length -eq 0) {
    throw "Isolated encrypted database remained empty during the startup validation window."
  }

  $headerLength = [Math]::Min(16, $headerBytes.Length)
  $headerText = [System.Text.Encoding]::ASCII.GetString($headerBytes, 0, $headerLength)
  $hasPlainHeader = $headerText -eq ("SQLite format 3" + [char]0)
  Write-Host "Raw SQLite plaintext header present: $hasPlainHeader"
  if ($hasPlainHeader) {
    throw "Isolated database exposes the standard SQLite plaintext header."
  }

  $stateOutput = & dotnet run --project $stateInspectorProject -- --database-path $databasePath --inspect-state
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  $stateOutput | ForEach-Object { Write-Host $_ }
  $activeShiftCountLine = $stateOutput | Where-Object { $_ -like "active shift record count:*" } | Select-Object -First 1
  $activeCustodyCountLine = $stateOutput | Where-Object { $_ -like "active cash-custody record count:*" } | Select-Object -First 1
  $activeShiftCount = [int](($activeShiftCountLine -split ":", 2)[1].Trim())
  $activeCustodyCount = [int](($activeCustodyCountLine -split ":", 2)[1].Trim())
  Write-Host "Expected fresh-install state: no active shift and no active cash-custody session."
  if (-not $AllowActiveLocalState -and $activeShiftCount -ne 0) {
    throw "Fresh isolated database unexpectedly has active shift records: $activeShiftCount"
  }

  if (-not $AllowActiveLocalState -and $activeCustodyCount -ne 0) {
    throw "Fresh isolated database unexpectedly has active cash-custody records: $activeCustodyCount"
  }

  if (Test-Path -LiteralPath $normalDatabasePath) {
    $normalHashAfter = (Get-FileHash -LiteralPath $normalDatabasePath -Algorithm SHA256).Hash
    Write-Host "Normal database path: $normalDatabasePath"
    Write-Host "Normal database hash before launch: $normalHashBefore"
    Write-Host "Normal database hash after launch:  $normalHashAfter"
    if ($normalHashBefore -ne $normalHashAfter) {
      throw "Normal APT LocalOperations database hash changed during isolated manual launch."
    }
  } else {
    Write-Host "Normal database path: $normalDatabasePath"
    Write-Host "Normal database was not present before launch."
  }
}
