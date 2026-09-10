# PreToolUse hook for Bash / PowerShell tool calls. Two guards, one process
# (pwsh startup is ~380ms, so both live here rather than in separate hooks):
#
#   1. Before `dotnet build` / `dotnet test`: stop a running PrMonitor, which
#      locks PrMonitor.exe and makes the build retry 5 times.
#   2. Before `git commit`: refuse a commit touching src/ that has no
#      CHANGELOG.md entry, or no passing test run since the last src/ edit.
#
# Enforces steps 2, 3 and 4 of the workflow in CLAUDE.md.
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

$cmd = $payload.tool_input.command
if (-not $cmd) { exit 0 }

$repo = (& git rev-parse --show-toplevel 2>$null)
if (-not $repo) { exit 0 }

# --- Guard 1: free the exe before a build ------------------------------------
if ($cmd -match 'dotnet\s+(build|test)') {
    $proc = Get-Process -Name PrMonitor -ErrorAction SilentlyContinue
    if ($proc) {
        $proc | Stop-Process -Force -ErrorAction SilentlyContinue
        @{ systemMessage = "Workflow hook: stopped the running PrMonitor before the build (it locks PrMonitor.exe). Restart it after the commit." } |
            ConvertTo-Json -Compress
    }
    exit 0
}

# --- Guard 2: gate the commit -----------------------------------------------
if ($cmd -notmatch 'git\s+(-\S+\s+)*commit') { exit 0 }

# Every path git reports as changed, staged or not, so `git commit -a` is covered.
$changed = @(& git -C $repo status --porcelain |
    Where-Object { $_.Length -gt 3 } |
    ForEach-Object { ($_.Substring(3) -split ' -> ')[-1].Trim('"') })

if (-not ($changed | Where-Object { $_ -like 'src/*' })) { exit 0 }

$reasons = @()

if (-not ($changed | Where-Object { $_ -eq 'CHANGELOG.md' })) {
    $reasons += "CHANGELOG.md is not part of this commit. Every src/ change needs an entry under [Unreleased] in the right Keep a Changelog category (CLAUDE.md step 3) - mandatory, including for small fixes."
}

$marker = Join-Path $repo '.claude/.last-test-pass'
$newestSrc = Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not (Test-Path $marker)) {
    $reasons += "No passing 'dotnet test' has been recorded in this working copy. Run: dotnet build .\src\PrMonitor.csproj -v q; dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj (CLAUDE.md step 2)."
}
elseif ($newestSrc -and (Get-Item $marker).LastWriteTimeUtc -lt $newestSrc.LastWriteTimeUtc) {
    $reasons += "src/ was modified after the last passing test run ($($newestSrc.Name) is newer than the marker). Re-run build and tests before committing (CLAUDE.md step 2)."
}

if ($reasons.Count -eq 0) { exit 0 }

@{
    hookSpecificOutput = @{
        hookEventName            = 'PreToolUse'
        permissionDecision       = 'deny'
        permissionDecisionReason = ($reasons -join ' ')
    }
} | ConvertTo-Json -Depth 5 -Compress
