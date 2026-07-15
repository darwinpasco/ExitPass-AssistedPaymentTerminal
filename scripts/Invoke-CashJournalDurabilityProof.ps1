Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ExitPass.APT.CashJournalProof\' + [System.Guid]::NewGuid().ToString('N'))
$databasePath = Join-Path $proofRoot 'cash-journal-proof.db'

try {
    New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null

    $resolvedDatabasePath = [System.IO.Path]::GetFullPath($databasePath)
    $resolvedRepositoryPath = [System.IO.Path]::GetFullPath($repositoryRoot.Path)

    if ($resolvedDatabasePath.StartsWith($resolvedRepositoryPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Proof database path must not be inside the repository. Path: $resolvedDatabasePath"
    }

    Write-Host "Using temporary proof database: $resolvedDatabasePath"

    dotnet run --no-restore --project (Join-Path $repositoryRoot 'tools\AssistedPaymentTerminal.LocalOperations.Proof\AssistedPaymentTerminal.LocalOperations.Proof.csproj') -- --database-path $resolvedDatabasePath
    if ($LASTEXITCODE -ne 0) {
        throw "Cash journal proof harness failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $resolvedDatabasePath)) {
        throw "Proof database was not created at $resolvedDatabasePath"
    }

    $repositoryDatabaseFiles = @(Get-ChildItem -Path $repositoryRoot -Recurse -File -Include *.db,*.sqlite,*.sqlite3 -ErrorAction SilentlyContinue)
    if ($repositoryDatabaseFiles.Count -gt 0) {
        $paths = ($repositoryDatabaseFiles | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Database files were found inside the repository:$([Environment]::NewLine)$paths"
    }

    Write-Host "No database files were left inside the Git repository."
    Write-Host "Cash journal durability proof succeeded."
}
finally {
    if (Test-Path $proofRoot) {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force
    }
}
