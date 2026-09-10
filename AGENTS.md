# PR Monitor — Agent Guide

> Detailed architecture notes are in [ARCHITECTURE.md](ARCHITECTURE.md).  
> Claude Code loads [CLAUDE.md](CLAUDE.md) — the workflow it must follow lives there.  
> This file is a quick-reference for other agents (e.g. Codex) that look for AGENTS.md.

## What this project is

Windows system-tray app (C# 12 / WPF / .NET 10) that monitors GitHub pull requests via the `gh` CLI. Single solution: `pr-monitor.slnx`.

## Build & run

```powershell
# Stop any running instance first (build overwrites the exe)
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue

# Build
dotnet build .\src\PrMonitor.csproj -v q

# Run (dev)
dotnet run --project .\src\PrMonitor.csproj

# Tests
dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj
```

Always stop the running instance before building. The app is single-instance (mutex `PrMonitor_SingleInstance`).

## Project layout

```
src/             C# source — App, MainWindow, Models, Services, ViewModels, Views, Converters
tests/           xUnit test project (PrMonitor.Tests)
ARCHITECTURE.md  Canonical technical reference (architecture, settings schema, pitfalls)
CLAUDE.md        Workflow + conventions for Claude Code (auto-loaded)
.github/         copilot-instructions.md (Copilot workflow) + workflow YAML files
CHANGELOG.md     Keep a Changelog format — update [Unreleased] on every src/ change
README.md        User-facing documentation
```

See [src/AGENTS.md](src/AGENTS.md) for source-folder structure details.

## Key conventions

- All `src/` changes must pass `dotnet build .\src\PrMonitor.csproj -v q` before committing.
- All `src/` changes must also pass `dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj` before committing.
- Update `CHANGELOG.md` (`[Unreleased]`) for every `src/` change — mandatory, not optional.
- Agents must execute commit commands after completing requested edits; do not leave tasks uncommitted.
- For commits that include `src/` changes, agents must restart the app process immediately after the commit.
- Commit message format: `feat:`, `fix:`, `refactor:`, `docs:`, `test:` etc.
- UI text must be in **English**.
- Settings are JSON-backed in `%APPDATA%\pr-monitor\settings.json` — see full schema in `ARCHITECTURE.md`.
- No secrets stored anywhere; all GitHub API calls shell out to `gh`.
- Claude Code enforces the build/test/CHANGELOG/commit/restart sequence with hooks in `.claude/` (see [CLAUDE.md](CLAUDE.md) section 7). Other agents must follow the same sequence by hand.
