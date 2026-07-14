$ErrorActionPreference = "Stop"

$forbiddenExtensions = @("*.pfx", "*.p12", "*.pem", "*.key", "*.crt", "*.cer")
$hits = foreach ($pattern in $forbiddenExtensions) {
    Get-ChildItem -Path . -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "\\node_modules\\" -and $_.FullName -notmatch "\\.git\\" }
}

if ($hits) {
    $hits | ForEach-Object { Write-Error "Forbidden certificate/key-like file committed or staged: $($_.FullName)" }
    exit 1
}

$contentHits = Get-ChildItem -Path . -Recurse -File -Include *.json,*.ts,*.tsx,*.cs,*.config,*.yml,*.yaml,*.md,*.ps1 -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch "\\node_modules\\" -and $_.FullName -notmatch "\\.git\\" -and $_.Name -ne "check-no-secrets.ps1" } |
    Select-String -Pattern "BEGIN (RSA |EC |OPENSSH |PRIVATE )?PRIVATE KEY|password\s*=|client_secret|api[_-]?key\s*[:=]" -CaseSensitive:$false

if ($contentHits) {
    $contentHits | ForEach-Object { Write-Error "Potential secret pattern found: $($_.Path):$($_.LineNumber)" }
    exit 1
}

Write-Host "No forbidden secret/certificate patterns found."
