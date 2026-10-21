# Shared helpers for eng\build.ps1, eng\publish.ps1 and eng\installer.ps1.
# Dot-source only:  . (Join-Path $PSScriptRoot 'common.ps1')
# Windows PowerShell 5.1 compatible (no &&, ??, ternary).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:NexPaneDryRun = $false

function Set-NexPaneDryRun {
    param([bool]$Enabled)
    $script:NexPaneDryRun = $Enabled
    if ($Enabled) {
        Write-Host 'DRY RUN: commands are printed, nothing is executed.' -ForegroundColor Yellow
    }
}

function Write-NexPaneStep {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Stop-NexPaneScript {
    # Prints a clear error and terminates the calling script with a non-zero exit code.
    param(
        [Parameter(Mandatory)][string]$Message,
        [int]$ExitCode = 1
    )
    if ($ExitCode -eq 0) { $ExitCode = 1 }
    Write-Host ''
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit $ExitCode
}

function Format-NexPaneCommand {
    param([string]$FilePath, [string[]]$Arguments)
    $parts = @($FilePath) + @($Arguments)
    $quoted = foreach ($p in $parts) {
        if ($p -match '[\s;&|<>()]' -or $p -eq '') { '"' + $p + '"' } else { $p }
    }
    return ($quoted -join ' ')
}

function Update-NexPanePath {
    # Tool shells do not pick up PATH changes made by installers; reload Machine + User PATH.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Resolve-NexPaneTool {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$InstallHint
    )
    $cmd = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $cmd) {
        Update-NexPanePath
        $cmd = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($null -eq $cmd) {
        Stop-NexPaneScript "'$Name' was not found on PATH. $InstallHint"
    }
    return $cmd.Source
}

function Invoke-NexPaneNative {
    # Runs a native command (or prints it in dry-run mode) and terminates the script on a non-zero exit code.
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    $display = Format-NexPaneCommand -FilePath $FilePath -Arguments $Arguments
    Write-NexPaneStep $Title
    Write-Host "    $display" -ForegroundColor DarkGray
    if ($script:NexPaneDryRun) { return }

    # Windows PowerShell 5.1 can surface native stderr lines as error records in some hosts; the exit code,
    # not stderr, decides success, so do not let 'Stop' abort on them.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        & $FilePath @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($code -ne 0) {
        Stop-NexPaneScript -Message "Step '$Title' failed with exit code $code.`n       Command: $display" -ExitCode $code
    }
}

function Get-NexPaneVersion {
    # Single source of truth for the product version: <Version> in Directory.Build.props.
    $propsPath = Join-Path $script:RepoRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $node = $props.SelectSingleNode('//Version')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        Stop-NexPaneScript "No <Version> element found in $propsPath."
    }
    $version = $node.InnerText.Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Stop-NexPaneScript "Version '$version' in $propsPath is not numeric major.minor.patch[.build] (required by Inno Setup VersionInfoVersion)."
    }
    return $version
}

function Find-NexPaneSignTool {
    $cmd = Get-Command 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $cmd) { return $cmd.Source }
    $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsBin) {
        $candidate = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate }
    }
    return $null
}

function Invoke-NexPaneOptionalSigning {
    # Decision D2: v1 ships unsigned. Signing runs only when NEXPANE_SIGN_CERT_THUMBPRINT is set
    # (certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My). Optional NEXPANE_SIGN_TIMESTAMP_URL
    # overrides the RFC 3161 timestamp server.
    param([Parameter(Mandatory)][string[]]$Files)

    $thumbprint = $env:NEXPANE_SIGN_CERT_THUMBPRINT
    if ([string]::IsNullOrWhiteSpace($thumbprint)) {
        Write-NexPaneStep 'Code signing'
        Write-Host '    signing skipped (NEXPANE_SIGN_CERT_THUMBPRINT not set)' -ForegroundColor Yellow
        return
    }

    $timestampUrl = $env:NEXPANE_SIGN_TIMESTAMP_URL
    if ([string]::IsNullOrWhiteSpace($timestampUrl)) { $timestampUrl = 'http://timestamp.digicert.com' }

    $signtool = Find-NexPaneSignTool
    if ($null -eq $signtool) {
        if ($script:NexPaneDryRun) {
            $signtool = 'signtool.exe'
        }
        else {
            Stop-NexPaneScript 'NEXPANE_SIGN_CERT_THUMBPRINT is set but signtool.exe was not found (install the Windows SDK "Signing Tools for Desktop Apps", or unset the variable to ship unsigned).'
        }
    }

    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $timestampUrl, '/td', 'SHA256', '/sha1', $thumbprint) + $Files
    Invoke-NexPaneNative -Title "Code signing ($($Files.Count) file(s))" -FilePath $signtool -Arguments $signArgs
}
