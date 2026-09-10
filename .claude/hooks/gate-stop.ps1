# Stop gate: do not let a turn end with src/ work left half-done.
# Blocks when src/ changes are uncommitted (step 4), or when HEAD touched
# src/ but the app is not running again (step 5).
# Escape hatch: create .claude/.skip-workflow-gate to disable.
$ErrorActionPreference = 'SilentlyContinue'
[Console]::In.ReadToEnd() | Out-Null

$repo = (& git rev-parse --show-toplevel 2>$null)
if (-not $repo) { exit 0 }
if (Test-Path (Join-Path $repo '.claude/.skip-workflow-gate')) { exit 0 }

$countFile = Join-Path $repo '.claude/.workflow-gate-count'
$reason = $null

$srcDirty = @(& git -C $repo status --porcelain |
    Where-Object { $_.Length -gt 3 } |
    ForEach-Object { ($_.Substring(3) -split ' -> ')[-1].Trim('"') } |
    Where-Object { $_ -like 'src/*' })

if ($srcDirty.Count -gt 0) {
    $shown = ($srcDirty | Select-Object -First 5) -join ', '
    $reason = "Workflow gate: $($srcDirty.Count) file(s) under src/ are uncommitted ($shown). Finish CLAUDE.md steps 2-5: build, test, update CHANGELOG.md, commit, restart the app. If the user asked you not to commit, say so and create .claude/.skip-workflow-gate."
}
else {
    $headSrc = @(& git -C $repo show --name-only --format= HEAD | Where-Object { $_ -like 'src/*' })
    if ($headSrc.Count -gt 0 -and -not (Get-Process -Name PrMonitor -ErrorAction SilentlyContinue)) {
        $reason = "Workflow gate: HEAD changed src/ but PrMonitor is not running - step 5 (restart) was skipped. Run: Start-Process dotnet -ArgumentList 'run --project .\src\PrMonitor.csproj' -WorkingDirectory '$repo' -WindowStyle Hidden"
    }
}

if (-not $reason) { Remove-Item $countFile -ErrorAction SilentlyContinue; exit 0 }

$n = 0
if (Test-Path $countFile) { $n = [int](Get-Content $countFile -Raw) }
$n++
if ($n -gt 3) {
    Remove-Item $countFile -ErrorAction SilentlyContinue
    @{ systemMessage = "Workflow gate unsatisfied after 3 attempts; letting the turn end. Check git status and whether the app is running." } | ConvertTo-Json -Compress
    exit 0
}
Set-Content -Path $countFile -Value $n -NoNewline
@{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
