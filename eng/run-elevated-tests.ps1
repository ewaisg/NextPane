<#
.SYNOPSIS
    Runs the Category=Elevated test suites (NTFS USN/MFT, IndexService pipe, SMB crawler) and writes a log
    that QA reads back.

.DESCRIPTION
    Must be started from an elevated (Run as Administrator) PowerShell. What the tests touch:
      - a throwaway 256 MB NTFS VHDX in %TEMP%, created/attached/formatted with diskpart and detached/deleted afterwards
        (the `fsutil usn deletejournal` test runs only against that VHDX);
      - read-only USN journal / MFT reads of the system volume;
      - a temporary SMB share over a temp folder, removed in a finally block.

    The log records UTC timestamps, the commit, power state and the measurements the tests report (system-volume USN
    enumeration and raw $MFT fill times with record counts, VHDX size/mtime comparison counts).
    The run refuses to start when the working tree has uncommitted changes, so the logged commit is what was tested;
    -AllowDirty runs anyway and logs DIRTY with `git status --porcelain`.

.PARAMETER LogPath
    Where to write the log. Default: build\elevated-tests.log under the repository root.

.PARAMETER AllowDirty
    Run on a working tree with uncommitted changes (logged as DIRTY together with git status --porcelain).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File eng\run-elevated-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$LogPath,
    [switch]$AllowDirty
)

function Get-UtcStamp { (Get-Date).ToUniversalTime().ToString('u') }

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $LogPath) { $LogPath = Join-Path $repoRoot 'build\elevated-tests.log' }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: run this script from an elevated PowerShell (Run as Administrator).' -ForegroundColor Red
    exit 1
}

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
Set-Location -LiteralPath $repoRoot
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null

$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0 -and -not $AllowDirty) {
    Write-Host 'ERROR: the working tree has uncommitted changes; commit them first so the logged commit is what was tested (or pass -AllowDirty).' -ForegroundColor Red
    $dirty | ForEach-Object { Write-Host "  $_" }
    exit 1
}

$metricsPath = Join-Path (Split-Path -Parent $LogPath) 'elevated-metrics.txt'
Remove-Item -LiteralPath $metricsPath -ErrorAction SilentlyContinue
$env:NEXPANE_ELEVATED_METRICS = $metricsPath

$projects = @('tests\NexPane.Indexing.Tests\NexPane.Indexing.Tests.csproj', 'tests\NexPane.IndexService.Tests\NexPane.IndexService.Tests.csproj')
$exitCode = 0
& {
    "NexPane elevated test run"
    "Started:  $(Get-UtcStamp) (UTC)"
    "Commit:   $(git rev-parse --short HEAD)$(if ($dirty.Count -gt 0) { ' DIRTY' } else { ' (clean working tree)' })"
    if ($dirty.Count -gt 0) {
        'git status --porcelain:'
        $dirty | ForEach-Object { "  $_" }
    }
    "User:     $([Security.Principal.WindowsIdentity]::GetCurrent().Name) (elevated)"
    "Power:    $(if ((Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1).BatteryStatus -eq 2) { 'AC' } else { 'battery or unknown' })"
    ''
    dotnet build NexPane.sln -c Release -nologo -v q
    "build exit: $LASTEXITCODE"
} *>&1 | Tee-Object -FilePath $LogPath
if ($LASTEXITCODE -ne 0) { $exitCode = 1 }

if ($exitCode -eq 0) {
    foreach ($project in $projects) {
        & {
            ''
            "=== $project ==="
            dotnet test --project $project -c Release --no-build --filter-trait 'Category=Elevated' --output Detailed
            "test exit: $LASTEXITCODE"
        } *>&1 | Tee-Object -FilePath $LogPath -Append
        if ($LASTEXITCODE -ne 0) { $exitCode = 1 }
    }
}

& {
    ''
    '=== Measurements reported by the tests ==='
    if (Test-Path -LiteralPath $metricsPath) { Get-Content -LiteralPath $metricsPath } else { '(none reported)' }
} *>&1 | Tee-Object -FilePath $LogPath -Append

"`nFinished: $(Get-UtcStamp) (UTC) - overall exit $exitCode" | Tee-Object -FilePath $LogPath -Append
Write-Host "`nLog written to $LogPath" -ForegroundColor Green
exit $exitCode
