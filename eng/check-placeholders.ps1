<#
.SYNOPSIS
    Zero-placeholder scanner (TODO.md item P1.12, HANDOFF.md section 7 "Zero-placeholder rule").

.DESCRIPTION
    Scans the shipped-code folders (default: src, tools, installer, eng) for stubs, placeholders and fake work.
    bin\ and obj\ folders and binary files are skipped. Test code lives under tests\ and is never scanned.
    This script and its allowlist file are excluded from the scan because they necessarily contain the patterns.

    Rules
      PH001  Marker words: TODO / FIXME (any case), HACK / XXX (upper case), as whole words.
      PH002  NotImplementedException (any reference).
      PH003  Placeholder text: "coming soon", "not implemented", "not yet implemented", "lorem ipsum",
             and the whole word "placeholder" (any case; identifiers such as PlaceholderText do not match).
      PH004  throw new NotSupportedException() without a message (C#).
      PH005  Empty catch block, or a catch block that contains only comments
             (C#, JavaScript, PowerShell: catch {...}; Inno Setup Pascal: except end;).
      PH006  Task.Delay / Thread.Sleep in non-test C# code (fake work, artificial latency, polling loops).
             Legitimate uses (retry backoff, debounce) need an allowlist entry with a justification.
      PH007  Event handler whose whole body is Console/Debug/Trace.Write(Line) calls (C#), as a method with an
             (object sender, XxxEventArgs e) signature or as a lambda attached with +=.
      PH008  Mock / fake / dummy / stub identifiers or text, "sample data"-style phrases, and placeholder
             people (John Doe, Jane Doe) in non-test code.

    Allowlist (eng\placeholder-allowlist.json) - keep it narrow:
      {
        "entries": [
          {
            "path": "src/NexPane.Network/RetryPolicy.cs",       // repository-relative, forward slashes
            "rule": "PH006",                                     // exactly one rule id
            "match": "await Task.Delay(backoff, ct)",            // literal text that must occur on the hit's line (>= 10 chars)
            "justification": "Exponential backoff between FTP reconnect attempts (ARCHITECTURE section 12.6).",
            "owner": "S5"                                        // optional
          }
        ]
      }
      An entry suppresses only hits of that rule, in that file, on lines containing "match".
      Every entry needs a justification of at least 20 characters. An entry that suppresses nothing is
      reported as stale. Allowlist errors exit with code 2.

    Exit codes: 0 = clean, 1 = violations found, 2 = configuration / allowlist error.

.PARAMETER Root
    Repository root to scan. Default: the parent of this script's folder.

.PARAMETER Paths
    Folders (relative to Root) to scan. Default: src, tools, installer, eng. Missing folders are skipped.

.PARAMETER AllowlistPath
    Allowlist JSON file. Default: <Root>\eng\placeholder-allowlist.json (optional; missing file = empty allowlist).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File eng\check-placeholders.ps1
#>
[CmdletBinding()]
param(
    [string]$Root,
    [string[]]$Paths = @('src', 'tools', 'installer', 'eng'),
    [string]$AllowlistPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Exit-WithConfigError([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 2
}

if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot '..' }
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { Exit-WithConfigError "Root folder not found: $Root" }
$Root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($AllowlistPath)) { $AllowlistPath = Join-Path $Root 'eng\placeholder-allowlist.json' }

$selfExcluded = @('eng/check-placeholders.ps1', 'eng/placeholder-allowlist.json')
$textExtensions = @('.cs', '.xaml', '.csproj', '.props', '.targets', '.ps1', '.psm1', '.psd1', '.iss', '.isl', '.json',
    '.xml', '.resx', '.config', '.manifest', '.sql', '.js', '.ts', '.md', '.txt', '.html', '.htm', '.css', '.cmd', '.bat',
    '.yml', '.yaml', '.editorconfig', '.svg')
$scriptLike = @('.cs', '.js', '.ts', '.ps1', '.psm1')

# --- Rules -------------------------------------------------------------------------------------------------
$ic = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
$none = [System.Text.RegularExpressions.RegexOptions]::None
$sl = [System.Text.RegularExpressions.RegexOptions]::Singleline

$writeCall = '(?:System\.)?(?:Diagnostics\.)?(?:Console|Debug|Trace)\.Write(?:Line)?\s*\([^;]*\)\s*;'
$catchBody = '\{(?:\s|//[^\n]*\n|/\*.*?\*/|#[^\n]*\n)*\}'

$rules = @(
    @{ Id = 'PH001'; Message = 'TODO/FIXME marker'; Pattern = '\b(?:TODO|FIXME)\b'; Options = $ic; Ext = $null },
    @{ Id = 'PH001'; Message = 'HACK/XXX marker'; Pattern = '\b(?:HACK|XXX)\b'; Options = $none; Ext = $null },
    @{ Id = 'PH002'; Message = 'NotImplementedException'; Pattern = 'NotImplementedException'; Options = $none; Ext = $null },
    @{ Id = 'PH003'; Message = 'placeholder text'; Pattern = '\b(?:coming\s+soon|not\s+(?:yet\s+)?implemented|lorem\s+ipsum|placeholder)\b'; Options = $ic; Ext = $null },
    @{ Id = 'PH004'; Message = 'NotSupportedException without a message'; Pattern = 'new\s+(?:System\.)?NotSupportedException\s*\(\s*\)'; Options = $none; Ext = @('.cs') },
    @{ Id = 'PH005'; Message = 'empty catch block'; Pattern = ('\bcatch\b[^{;]*' + $catchBody); Options = $sl; Ext = $scriptLike },
    @{ Id = 'PH005'; Message = 'empty except block (Pascal)'; Pattern = '\bexcept\s*(?:\{[^}]*\}\s*|//[^\n]*\n\s*)*end\s*;'; Options = $ic; Ext = @('.iss') },
    @{ Id = 'PH006'; Message = 'Task.Delay/Thread.Sleep in non-test code (fake work?)'; Pattern = '\b(?:Task\.Delay|Thread\.Sleep)\s*\('; Options = $none; Ext = @('.cs') },
    @{ Id = 'PH007'; Message = 'event handler that only writes to Console/Debug/Trace'; Pattern = ('\(\s*object\??\s+\w+\s*,\s*[\w.]*EventArgs\s+\w+\s*\)\s*(?:\{\s*(?:' + $writeCall + '\s*)+\}|=>\s*' + $writeCall + ')'); Options = $sl; Ext = @('.cs') },
    @{ Id = 'PH007'; Message = 'event handler lambda that only writes to Console/Debug/Trace'; Pattern = ('\+=\s*(?:\([^)]*\)|\w+)\s*=>\s*(?:\{\s*(?:' + $writeCall + '\s*)+\}|' + $writeCall + ')'); Options = $sl; Ext = @('.cs') },
    @{ Id = 'PH008'; Message = 'mock/fake/dummy/stub data in non-test code'; Pattern = '\b(?:mock|fake|dummy|stub)\w*'; Options = $ic; Ext = $null },
    @{ Id = 'PH008'; Message = 'sample/placeholder data in non-test code'; Pattern = '\b(?:sample|example|test)[\s_-]?(?:data|users?|items?|entries|records|files?|text|content|response|results?|values?)\b|\b(?:john|jane)\s+doe\b'; Options = $ic; Ext = $null }
)
foreach ($rule in $rules) {
    $rule.Regex = New-Object System.Text.RegularExpressions.Regex($rule.Pattern, ($rule.Options -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant))
}

# --- Allowlist ---------------------------------------------------------------------------------------------
$allow = @()
if (Test-Path -LiteralPath $AllowlistPath) {
    try { $doc = Get-Content -LiteralPath $AllowlistPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Exit-WithConfigError "Allowlist is not valid JSON: $AllowlistPath ($($_.Exception.Message))" }
    if ($null -eq $doc -or -not ($doc.PSObject.Properties.Name -contains 'entries')) {
        Exit-WithConfigError "Allowlist must be an object with an 'entries' array: $AllowlistPath"
    }
    $validRuleIds = @($rules | ForEach-Object { $_.Id } | Select-Object -Unique)
    $index = 0
    foreach ($e in @($doc.entries)) {
        $names = @($e.PSObject.Properties.Name)
        foreach ($required in @('path', 'rule', 'match', 'justification')) {
            if (-not ($names -contains $required) -or [string]::IsNullOrWhiteSpace([string]$e.$required)) {
                Exit-WithConfigError "Allowlist entry #$index is missing '$required' (every entry needs path, rule, match and justification)."
            }
        }
        if ($validRuleIds -notcontains [string]$e.rule) { Exit-WithConfigError "Allowlist entry #$index has unknown rule '$($e.rule)'." }
        if (([string]$e.justification).Trim().Length -lt 20) { Exit-WithConfigError "Allowlist entry #$index justification is too short (minimum 20 characters)." }
        if (([string]$e.match).Length -lt 10) { Exit-WithConfigError "Allowlist entry #$index 'match' is too broad (minimum 10 characters of literal line text)." }
        $allow += [pscustomobject]@{
            Index = $index; Path = ([string]$e.path).Replace('\', '/').TrimStart('/'); Rule = [string]$e.rule
            Match = [string]$e.match; Used = $false
        }
        $index++
    }
}

# --- Scan --------------------------------------------------------------------------------------------------
function Get-LineStarts([string]$text) {
    $starts = New-Object System.Collections.Generic.List[int]
    $starts.Add(0)
    for ($i = 0; $i -lt $text.Length; $i++) { if ($text[$i] -eq "`n") { $starts.Add($i + 1) } }
    return , $starts.ToArray()
}

function Get-LineNumber([int[]]$starts, [int]$offset) {
    $lo = 0; $hi = $starts.Length - 1
    while ($lo -lt $hi) {
        $mid = [int][math]::Floor(($lo + $hi + 1) / 2)
        if ($starts[$mid] -le $offset) { $lo = $mid } else { $hi = $mid - 1 }
    }
    return $lo
}

$violations = New-Object System.Collections.Generic.List[object]
$suppressed = 0
$fileCount = 0

foreach ($rel in $Paths) {
    $dir = Join-Path $Root $rel
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
    $files = Get-ChildItem -LiteralPath $dir -Recurse -File -Force | Where-Object {
        $_.FullName -notmatch '\\(bin|obj|node_modules|\.vs)\\' -and
        ($textExtensions -contains $_.Extension.ToLowerInvariant())
    }
    foreach ($file in $files) {
        $relPath = $file.FullName.Substring($Root.Length).TrimStart('\').Replace('\', '/')
        if ($selfExcluded -contains $relPath.ToLowerInvariant()) { continue }
        $fileCount++
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $text = $text.Replace("`r`n", "`n")
        $starts = Get-LineStarts $text
        $lines = $text.Split("`n")
        $ext = $file.Extension.ToLowerInvariant()
        foreach ($rule in $rules) {
            if ($null -ne $rule.Ext -and $rule.Ext -notcontains $ext) { continue }
            foreach ($m in $rule.Regex.Matches($text)) {
                $lineIdx = Get-LineNumber $starts $m.Index
                $lineText = $lines[$lineIdx]
                $hit = $null
                foreach ($a in $allow) {
                    if ($a.Rule -eq $rule.Id -and [string]::Equals($a.Path, $relPath, [StringComparison]::OrdinalIgnoreCase) -and
                        $lineText.IndexOf($a.Match, [StringComparison]::Ordinal) -ge 0) { $hit = $a; break }
                }
                if ($null -ne $hit) { $hit.Used = $true; $suppressed++; continue }
                $snippet = $m.Value -replace '\s+', ' '
                if ($snippet.Length -gt 80) { $snippet = $snippet.Substring(0, 77) + '...' }
                $violations.Add([pscustomobject]@{
                    Path = $relPath; Line = $lineIdx + 1; Column = $m.Index - $starts[$lineIdx] + 1
                    Rule = $rule.Id; Message = $rule.Message; Snippet = $snippet
                })
            }
        }
    }
}

$stale = @($allow | Where-Object { -not $_.Used })
foreach ($v in ($violations | Sort-Object Path, Line, Column)) {
    Write-Host ('{0}:{1}:{2}: {3} {4}: "{5}"' -f $v.Path, $v.Line, $v.Column, $v.Rule, $v.Message, $v.Snippet)
}
foreach ($s in $stale) {
    Write-Host ('STALE allowlist entry #{0} ({1} {2} "{3}") suppresses nothing; remove it.' -f $s.Index, $s.Rule, $s.Path, $s.Match)
}

Write-Host ('check-placeholders: {0} file(s) scanned in [{1}], {2} violation(s), {3} allowlisted, {4} stale allowlist entr(ies).' -f `
        $fileCount, ($Paths -join ', '), $violations.Count, $suppressed, $stale.Count)
if ($stale.Count -gt 0) { exit 2 }
if ($violations.Count -gt 0) { exit 1 }
exit 0
