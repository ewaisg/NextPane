<#
.SYNOPSIS
    Publishes NexPane (App + IndexService) self-contained for win-x64 into one folder (tasks P1.11 / P7.07, ARCHITECTURE section 19.2).

.DESCRIPTION
    - Deletes the output folder (default build\publish\win-x64) so no stale files reach the installer.
    - dotnet publish src\NexPane.App\NexPane.App.csproj and src\NexPane.IndexService\NexPane.IndexService.csproj
      with -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true into the SAME folder.
      NOT single-file (native libraries) and NOT trimmed (WPF/COM are not trim-safe).
    - Verifies NexPane.exe and NexPane.IndexService.exe exist.
    - Optional Authenticode signing (decision D2: v1 ships unsigned): runs only when
      NEXPANE_SIGN_CERT_THUMBPRINT is set, otherwise prints "signing skipped".
    Stops at the first failing step and exits with that step's exit code.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputDir
    Publish folder, relative to the repository root or absolute. Default: build\publish\win-x64.

.PARAMETER DryRun
    Print the exact commands without running them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputDir = 'build\publish\win-x64',
    [switch]$DryRun
)

. (Join-Path $PSScriptRoot 'common.ps1')
Set-NexPaneDryRun $DryRun.IsPresent
Set-Location -LiteralPath $RepoRoot

$dotnet = Resolve-NexPaneTool -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK: winget install --id Microsoft.DotNet.SDK.10 -e'
$runtime = 'win-x64'

if ([IO.Path]::IsPathRooted($OutputDir)) { $publishDir = $OutputDir } else { $publishDir = Join-Path $RepoRoot $OutputDir }
$publishDir = [IO.Path]::GetFullPath($publishDir)

Write-NexPaneStep "Clean publish folder $publishDir"
if (-not $DryRun -and (Test-Path -LiteralPath $publishDir)) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$projects = @(
    'src\NexPane.App\NexPane.App.csproj',
    'src\NexPane.IndexService\NexPane.IndexService.csproj'
)
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    Invoke-NexPaneNative -Title "Publish $name ($runtime, self-contained, ReadyToRun)" -FilePath $dotnet -Arguments @(
        'publish', $project,
        '-c', $Configuration,
        '-r', $runtime,
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-o', $publishDir)
}

$requiredFiles = @('NexPane.exe', 'NexPane.IndexService.exe')
$exePaths = @($requiredFiles | ForEach-Object { Join-Path $publishDir $_ })

if (-not $DryRun) {
    Write-NexPaneStep 'Verify publish output'
    foreach ($path in $exePaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            Stop-NexPaneScript "Expected publish output is missing: $path"
        }
        Write-Host "    OK  $path"
    }
    $fileCount = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
    $sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Host "    $fileCount files, $sizeMb MB"
}

Invoke-NexPaneOptionalSigning -Files $exePaths

Write-Host ''
Write-Host "publish.ps1 completed successfully: $publishDir" -ForegroundColor Green
exit 0
