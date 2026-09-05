[CmdletBinding()]
param(
    [switch]$PreflightOnly,
    [switch]$WebViewSmokeCheck
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$configPath = Join-Path $repoRoot "src\AssistedPaymentTerminal.App\public\apt-config.json"
$uiRoot = Join-Path $repoRoot "src\AssistedPaymentTerminal.App"
$viteEntryPoint = Join-Path $repoRoot "node_modules\vite\bin\vite.js"
$desktopProject = Join-Path $repoRoot "src\AssistedPaymentTerminal.Desktop\AssistedPaymentTerminal.Desktop.csproj"
$centralPmsUrl = "https://localhost:56064"
$webUiUrl = "http://localhost:5173"

try {
    $response = Invoke-WebRequest -Uri "$centralPmsUrl/health/ready" -UseBasicParsing -TimeoutSec 5
    if ($response.StatusCode -ne 200) {
        throw "HTTP $($response.StatusCode)"
    }
} catch {
    throw "Central PMS is not running at $centralPmsUrl. Start it from the ExitPass repository with: powershell -ExecutionPolicy Bypass -File .\scripts\v1.3\local-runtime\Start-CentralPms.ps1"
}

$runtimeConfig = [ordered]@{
    APT_PROFILE = "CASHIER_ASSISTED_TERMINAL"
    APT_TERMINAL_ID = "apt-pitx-level-3-01"
    APT_TERMINAL_DISPLAY_NAME = "PITX Level 3 APT"
    APT_SITE_ID = "2d1dcdf8-f563-537c-8542-0bde7cc9da97"
    APT_SITE_NAME = "PITX Level 3"
    APT_SITE_GROUP_ID = "a6dbadf6-68b5-5bed-a7e0-a75faee70841"
    APT_POS_SERVER_ID = "3a138565-1b88-55f8-c83d-5380db6edccc"
    CENTRAL_PMS_BASE_URL = $centralPmsUrl
    USE_MOCK_CENTRAL_PMS = "false"
    APT_WEB_UI_URL = $webUiUrl
    CENTRAL_PMS_VENDOR_SYSTEM_ID = "HIKCENTRAL"
}

$env:APT_PROFILE = $runtimeConfig.APT_PROFILE
$env:APT_TERMINAL_ID = $runtimeConfig.APT_TERMINAL_ID
$env:APT_TERMINAL_DISPLAY_NAME = $runtimeConfig.APT_TERMINAL_DISPLAY_NAME
$env:APT_SITE_ID = $runtimeConfig.APT_SITE_ID
$env:APT_SITE_NAME = $runtimeConfig.APT_SITE_NAME
$env:APT_SITE_GROUP_ID = $runtimeConfig.APT_SITE_GROUP_ID
$env:APT_POS_SERVER_ID = $runtimeConfig.APT_POS_SERVER_ID
$env:CENTRAL_PMS_BASE_URL = $runtimeConfig.CENTRAL_PMS_BASE_URL
$env:USE_MOCK_CENTRAL_PMS = $runtimeConfig.USE_MOCK_CENTRAL_PMS
$env:APT_WEB_UI_URL = $runtimeConfig.APT_WEB_UI_URL
$env:APT_CENTRAL_PMS_SERVICE_IDENTITY_ID = "be31c0c2-7fdb-4029-a61e-50fd5bbf87ce"

Write-Host "Central PMS readiness: PASS ($centralPmsUrl)"
Write-Host "APT PITX context: $($runtimeConfig.APT_TERMINAL_ID) / $($runtimeConfig.APT_SITE_NAME)"
if ($PreflightOnly) {
    exit 0
}

if (-not (Test-Path -LiteralPath $viteEntryPoint -PathType Leaf)) {
    throw "APT frontend dependencies are missing. Run npm.cmd ci from $repoRoot."
}

$originalConfig = [System.IO.File]::ReadAllBytes($configPath)
$viteProcess = $null
$desktopExitCode = 0

try {
    $json = $runtimeConfig | ConvertTo-Json
    [System.IO.File]::WriteAllText($configPath, "$json`r`n", [System.Text.UTF8Encoding]::new($false))

    $viteProcess = Start-Process -FilePath "node.exe" `
        -ArgumentList @($viteEntryPoint, "--host", "localhost", "--port", "5173", "--strictPort") `
        -WorkingDirectory $uiRoot `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        if ($viteProcess.HasExited) {
            throw "APT Vite runtime exited before $webUiUrl became ready."
        }

        try {
            $uiResponse = Invoke-WebRequest -Uri "$webUiUrl/apt-config.json" -UseBasicParsing -TimeoutSec 2
            $servedConfig = $uiResponse.Content | ConvertFrom-Json
            if ($uiResponse.StatusCode -eq 200 -and $servedConfig.APT_SITE_ID -eq $runtimeConfig.APT_SITE_ID) {
                break
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        throw "APT Vite runtime did not become ready at $webUiUrl within 45 seconds."
    }

    Write-Host "APT Web UI readiness: PASS ($webUiUrl)"
    if ($WebViewSmokeCheck) {
        & dotnet run --project $desktopProject -- --webview-smoke-check
    } else {
        & dotnet run --project $desktopProject
    }
    $desktopExitCode = $LASTEXITCODE
} finally {
    if ($null -ne $viteProcess -and -not $viteProcess.HasExited) {
        Stop-Process -Id $viteProcess.Id -Force -ErrorAction SilentlyContinue
        $viteProcess.WaitForExit(5000)
    }

    [System.IO.File]::WriteAllBytes($configPath, $originalConfig)
}

exit $desktopExitCode
