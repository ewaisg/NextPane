<#
.SYNOPSIS
    Restores, builds and tests NexPane (task P1.11, ARCHITECTURE section 19.2).

.DESCRIPTION
    1. dotnet restore NexPane.sln
    2. dotnet build NexPane.sln -c <Configuration> --no-restore
    3. dotnet test --solution NexPane.sln -c <Configuration> --no-build
         --filter-not-trait "Category=Elevated" --filter-not-trait "Category=Live"
       (Microsoft.Testing.Platform syntax; the Elevated and Live suites are opt-in, see HANDOFF section 5.)
    Stops at the first failing step and exits with that step's exit code.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SkipTests
    Restore and build only.

.PARAMETER DryRun
    Print the exact commands without running them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File eng\build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$DryRun
)

. (Join-Path $PSScriptRoot 'common.ps1')
Set-NexPaneDryRun $DryRun.IsPresent
Set-Location -LiteralPath $RepoRoot

$dotnet = Resolve-NexPaneTool -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK: winget install --id Microsoft.DotNet.SDK.10 -e'
$solution = 'NexPane.sln'

Invoke-NexPaneNative -Title 'Restore' -FilePath $dotnet -Arguments @('restore', $solution)
Invoke-NexPaneNative -Title "Build ($Configuration)" -FilePath $dotnet -Arguments @('build', $solution, '-c', $Configuration, '--no-restore')

if ($SkipTests) {
    Write-NexPaneStep 'Tests skipped (-SkipTests)'
}
else {
    Invoke-NexPaneNative -Title 'Test (default suite: Elevated and Live excluded)' -FilePath $dotnet -Arguments @(
        'test', '--solution', $solution, '-c', $Configuration, '--no-build',
        '--filter-not-trait', 'Category=Elevated',
        '--filter-not-trait', 'Category=Live')
}

Write-Host ''
Write-Host 'build.ps1 completed successfully.' -ForegroundColor Green
exit 0
