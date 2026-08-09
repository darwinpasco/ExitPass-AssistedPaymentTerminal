$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$desktopTests = Join-Path $repoRoot "tests\AssistedPaymentTerminal.Desktop.Tests\AssistedPaymentTerminal.Desktop.Tests.csproj"
$appRoot = Join-Path $repoRoot "src\AssistedPaymentTerminal.App"

dotnet test $desktopTests --configuration Release --filter "FullyQualifiedName~HumanSessionRuntimeTests|FullyQualifiedName~ProductionBridge" --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "APT human-session host proof failed." }

Push-Location $appRoot
try {
    npm.cmd test -- --run src/humanSessionBridge.test.tsx
    if ($LASTEXITCODE -ne 0) { throw "APT human-session presentation proof failed." }

    npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "APT production frontend build failed." }
}
finally {
    Pop-Location
}

$desktopProject = Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\AssistedPaymentTerminal.Desktop.csproj"
dotnet build $desktopProject --configuration Release -m:1 /p:UseSharedCompilation=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "APT desktop host build failed." }

$smokeResultPath = Join-Path $env:TEMP "exitpass-apt-webview-smoke-result.txt"
$desktopExecutable = Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\bin\Release\net8.0-windows\win-x64\AssistedPaymentTerminal.Desktop.exe"
$priorProfile = $env:APT_PROFILE
try {
    Remove-Item -LiteralPath $smokeResultPath -Force -ErrorAction SilentlyContinue
    $env:APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
    $process = Start-Process -FilePath $desktopExecutable -ArgumentList "--webview-smoke-check", "--packaged-assets" -PassThru -Wait
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $smokeResultPath)) {
        throw "APT actual WebView2 credential-exclusion smoke did not complete successfully. Exit code: $($process.ExitCode)"
    }
    $smokeResult = Get-Content -LiteralPath $smokeResultPath -Raw
    if ($smokeResult.Trim() -ne "PASSED") {
        throw "APT actual WebView2 credential-exclusion smoke failed: $smokeResult"
    }
}
finally {
    $env:APT_PROFILE = $priorProfile
    Remove-Item -LiteralPath $smokeResultPath -Force -ErrorAction SilentlyContinue
}

$productionConfig = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.App\public\apt-config.json") -Raw
foreach ($name in @("APT_CASHIER_ID", "APT_CASHIER_DISPLAY_NAME", "APT_SHIFT_ID", "APT_SHIFT_STATUS")) {
    if ($productionConfig.IndexOf($name, [StringComparison]::Ordinal) -ge 0) { throw "Production config contains development authority: $name" }
}

$cashPanel = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.App\src\CashCapturePanel.tsx") -Raw
if ($cashPanel.IndexOf("createOrGetDevelopmentSession", [StringComparison]::Ordinal) -ge 0) { throw "Production cash capture creates a development session." }

$humanRuntime = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\HumanSessionRuntime.cs") -Raw
foreach ($permission in @("apt.access", "cashier-shifts.operate", "cash-custody.operate", "terminal-cash.receive")) {
    if ($humanRuntime.IndexOf($permission, [StringComparison]::Ordinal) -lt 0) { throw "Human-session runtime is missing the canonical I-021 permission boundary: $permission" }
}
if ($humanRuntime.IndexOf("terminal-cash.payable-basis.read", [StringComparison]::Ordinal) -ge 0) {
    throw "Human-session runtime improperly treats payable-basis read permission as human operational authority."
}

$mainWindow = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\MainWindow.xaml.cs") -Raw
foreach ($required in @(
    "IsPasswordAutosaveEnabled = false",
    "IsGeneralAutofillEnabled = false",
    "CoreWebView2BrowsingDataKinds.PasswordAutosave",
    "CoreWebView2BrowsingDataKinds.GeneralAutofill")) {
    if ($mainWindow.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows host password replay protection is missing: $required"
    }
}

$credentialPrompt = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\HumanCredentialPrompt.cs") -Raw
foreach ($required in @(
    "WpfHumanCredentialPrompt",
    "HumanCredentialAttemptGate",
    "ExplicitHumanCredentialSubmission",
    "NATIVE_EXPLICIT_SUBMIT",
    "TryBegin",
    "TryConsume",
    "InvalidateAll",
    "CancelActive")) {
    if ($credentialPrompt.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Native one-shot credential boundary is missing: $required"
    }
}

$humanBridgeHandler = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\HumanSessionBridgeHandler.cs") -Raw
foreach ($required in @(
    'EnsureExactPayload(request.Payload, "username")',
    "EnsureExactPayload(request.Payload)",
    "GetExplicitCredentialAsync",
    "CREDENTIAL_ENTRY_IN_PROGRESS",
    "InvalidateCredentialFlow")) {
    if ($humanBridgeHandler.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Host credential bridge validation is missing: $required"
    }
}
if ($humanBridgeHandler.IndexOf('record LoginPayload(string Username, string Password)', [StringComparison]::Ordinal) -ge 0 -or
    $humanBridgeHandler.IndexOf('record ReauthenticatePayload(string Password)', [StringComparison]::Ordinal) -ge 0) {
    throw "Browser bridge DTO still accepts password material."
}

$frontend = (Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.App\src\humanSessionBridge.ts") -Raw) +
    (Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.App\src\App.tsx") -Raw)
foreach ($value in @("localStorage.setItem", "sessionStorage.setItem", "X-ExitPass-Permissions", "X-ExitPass-Service-Identity-Id", "Authorization:")) {
    if ($frontend.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Frontend contains prohibited authority material: $value" }
}
if ($frontend.IndexOf('type="password"', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $frontend.IndexOf('passwordRef', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $frontend.IndexOf('{ username, password }', [StringComparison]::Ordinal) -ge 0 -or
    $frontend.IndexOf('{ password }', [StringComparison]::Ordinal) -ge 0) {
    throw "Frontend still owns password credential state."
}
if ($frontend.IndexOf("loginInFlightRef", [StringComparison]::Ordinal) -lt 0) {
    throw "Frontend login submission is missing its synchronous single-flight boundary."
}

$singleInstance = Get-Content -LiteralPath (Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\DesktopSingleInstanceLease.cs") -Raw
if ($singleInstance.IndexOf("Semaphore(1, 1", [StringComparison]::Ordinal) -lt 0) {
    throw "Normal desktop runtime is missing the one-host-per-terminal lease."
}

Write-Host "APT human-session proof passed."
