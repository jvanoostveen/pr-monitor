# PostToolUse: record that tests passed, so gate-commit.ps1 can require a
# test run newer than the newest src/ edit. PostToolUse only fires for a
# successful tool call; the output scan is a second line of defence.
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

$cmd = $payload.tool_input.command
if (-not $cmd) { exit 0 }
if ($cmd -notmatch 'dotnet\s+test') { exit 0 }
if ($raw -match 'Failed!|failed:\s*[1-9]|error\s+(MSB|CS)\d|Build FAILED') { exit 0 }

$repo = (& git rev-parse --show-toplevel 2>$null)
if (-not $repo) { exit 0 }

$dir = Join-Path $repo '.claude'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Set-Content -Path (Join-Path $dir '.last-test-pass') -Value (Get-Date -Format o) -NoNewline
