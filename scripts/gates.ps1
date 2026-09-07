# Runs every deterministic CI gate for TravelEar on a host that has Big Walk installed.
# Usage: pwsh scripts/gates.ps1 [-SkipBuild] [-SkipDocs]
# Exit code is non-zero if any gate fails. See docs/CI.md.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipDocs
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$failed = @()

function Invoke-Gate {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    FAIL: $Name (exit $LASTEXITCODE)"
        $script:failed += $Name
    } else {
        Write-Host "    ok"
    }
}

if (-not $SkipBuild) {
    Invoke-Gate 'build' { dotnet build TravelEar.sln -c Release --nologo -v quiet }
    if (Test-Path 'tests') {
        Invoke-Gate 'unit tests' { dotnet test TravelEar.sln -c Release --no-build --nologo -v quiet }
    } else {
        Write-Host "==> unit tests: no tests/ directory yet, skipped"
    }
}

Invoke-Gate 'traceable-reqs check' { traceable-reqs check }

if (-not $SkipDocs) {
    Invoke-Gate 'docs-site build' { mdbook build docs-site }
}

if ($failed.Count -gt 0) {
    Write-Host "Gates failed: $($failed -join ', ')"
    exit 1
}
Write-Host "All gates green."
exit 0
