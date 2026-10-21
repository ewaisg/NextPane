<#
.SYNOPSIS
    Compiles installer\NexPane.iss with Inno Setup 6 into dist\NexPane-Setup.exe
    (TODO P1.11 / P7.08, ARCHITECTURE section 19.3).

.DESCRIPTION
    - Locates ISCC.exe (per-user install first, then Program Files (x86), then Program Files, then PATH).
    - Requires a publish folder produced by eng\publish.ps1 (NexPane.exe + NexPane.IndexService.exe).
    - Requires the Phase 2 branding assets (assets\icons\NexPane.ico, assets\installer\wizard-*.bmp);
      fails with a clear error when they are missing.
    - Passes /DAppVersion (from Directory.Build.props) and /DPublishDir to ISCC.
    - Treats any ISCC warning as an error (P7.08 requires a warning-free compile).
    - Optional Authenticode signing of the setup exe (decision D2: skipped unless NEXPANE_SIGN_CERT_THUMBPRINT is set).

.PARAMETER PublishDir
    Publish folder, relative to the repository root or absolute. Default: build\publish\win-x64.

.PARAMETER AllowMissingBranding
    PRE-PHASE-2 PIPELINE VERIFICATION ONLY. Passes /DNoBranding to ISCC, which omits SetupIconFile,
    WizardImageFile and WizardSmallImageFile so the pipeline can be verified before P2.04/P2.06 create the
    branding assets. Phase 7 release builds (P7.08) MUST NOT use this switch.

.PARAMETER DryRun
    Print the exact commands without running them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File eng\installer.ps1
#>
[CmdletBinding()]
param(
    [string]$PublishDir = 'build\publish\win-x64',
    [switch]$AllowMissingBranding,
    [switch]$DryRun
)

. (Join-Path $PSScriptRoot 'common.ps1')
Set-NexPaneDryRun $DryRun.IsPresent
Set-Location -LiteralPath $RepoRoot

# --- Locate ISCC.exe -------------------------------------------------------------------------------
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($null -eq $iscc) {
    $onPath = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $onPath) { $iscc = $onPath.Source }
}
if ($null -eq $iscc) {
    Stop-NexPaneScript ("Inno Setup 6 compiler (ISCC.exe) not found. Probed:`n       " + ($isccCandidates -join "`n       ") +
        "`n       and PATH. Install it with: winget install --id JRSoftware.InnoSetup -e")
}
Write-NexPaneStep 'Inno Setup compiler'
Write-Host "    $iscc"

# --- Inputs ------------------------------------------------------------------------------------------
$version = Get-NexPaneVersion
if ([IO.Path]::IsPathRooted($PublishDir)) { $publishFull = $PublishDir } else { $publishFull = Join-Path $RepoRoot $PublishDir }
$publishFull = [IO.Path]::GetFullPath($publishFull)
$issPath = Join-Path $RepoRoot 'installer\NexPane.iss'
# Product spec mandates the unversioned name; the version lives in the exe's version resource.
$setupPath = Join-Path $RepoRoot 'dist\NexPane-Setup.exe'

Write-NexPaneStep 'Check installer inputs'
Write-Host "    Version:     $version"
Write-Host "    Publish dir: $publishFull"
if (-not $DryRun) {
    foreach ($required in @('NexPane.exe', 'NexPane.IndexService.exe')) {
        $p = Join-Path $publishFull $required
        if (-not (Test-Path -LiteralPath $p)) {
            Stop-NexPaneScript "Publish output missing: $p`n       Run eng\publish.ps1 first."
        }
    }
}
foreach ($required in @($issPath, (Join-Path $RepoRoot 'LICENSE.txt'), (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        Stop-NexPaneScript "Required installer input missing: $required"
    }
}

# --- Branding (Phase 2: P2.04 icon, P2.06 wizard bitmaps) ---------------------------------------
$brandingFiles = @('assets\icons\NexPane.ico') +
    @('164x314', '205x393', '246x471', '287x550', '328x628' | ForEach-Object { "assets\installer\wizard-$_.bmp" }) +
    @('55', '69', '83', '97', '110' | ForEach-Object { "assets\installer\wizard-small-$_.bmp" })
$missingBranding = @($brandingFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $RepoRoot $_)) })

$isccArgs = @("/DAppVersion=$version", "/DPublishDir=$publishFull")
if ($missingBranding.Count -gt 0) {
    if ($AllowMissingBranding) {
        Write-Host ''
        Write-Host ('WARNING: -AllowMissingBranding: {0} branding file(s) missing; compiling WITHOUT SetupIconFile/WizardImageFile/WizardSmallImageFile (/DNoBranding).' -f $missingBranding.Count) -ForegroundColor Yellow
        Write-Host '         This mode exists only for pre-Phase-2 pipeline verification. Release builds (P7.08) must not use it.' -ForegroundColor Yellow
        $isccArgs += '/DNoBranding'
    }
    else {
        Stop-NexPaneScript ("Branding assets missing (created in Phase 2, tasks P2.04 and P2.06):`n       " + ($missingBranding -join "`n       ") +
            "`n       Release installers require them. For pre-Phase-2 pipeline verification only, re-run with -AllowMissingBranding.")
    }
}
elseif ($AllowMissingBranding) {
    Write-Host '    All branding assets present; -AllowMissingBranding has no effect.' -ForegroundColor DarkGray
}
$isccArgs += $issPath

# --- Compile -----------------------------------------------------------------------------------------
$display = Format-NexPaneCommand -FilePath $iscc -Arguments $isccArgs
Write-NexPaneStep 'Compile installer'
Write-Host "    $display" -ForegroundColor DarkGray

if (-not $DryRun) {
    if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        # 2>&1 so ISCC's compile errors (written to stderr) are shown and captured together with stdout.
        $isccOutput = @(& $iscc @isccArgs 2>&1 | ForEach-Object { $line = "$_"; Write-Host $line; $line })
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($code -ne 0) {
        Stop-NexPaneScript -Message "ISCC failed with exit code $code.`n       Command: $display" -ExitCode $code
    }
    $warnings = @($isccOutput | Where-Object { $_ -match '\bWarning:' })
    if ($warnings.Count -gt 0) {
        Stop-NexPaneScript ("ISCC reported {0} warning(s); the installer script must compile warning-free:`n       {1}" -f $warnings.Count, ($warnings -join "`n       "))
    }
    if (-not (Test-Path -LiteralPath $setupPath)) {
        Stop-NexPaneScript "ISCC succeeded but the expected output was not found: $setupPath"
    }
}

Invoke-NexPaneOptionalSigning -Files @($setupPath)

if (-not $DryRun) {
    $size = (Get-Item -LiteralPath $setupPath).Length
    Write-Host ''
    Write-Host ('installer.ps1 completed successfully: {0} ({1:N0} bytes, {2:N1} MB)' -f $setupPath, $size, ($size / 1MB)) -ForegroundColor Green
}
else {
    Write-Host ''
    Write-Host "installer.ps1 dry run complete (would produce $setupPath)." -ForegroundColor Green
}
exit 0
