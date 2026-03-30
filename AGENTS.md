# PR Monitor — Agent Guide

> Detailed instructions for GitHub Copilot are in [.github/copilot-instructions.md](.github/copilot-instructions.md).  
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
src/          C# source — App, MainWindow, Models, Services, ViewModels, Views, Converters
tests/        xUnit test project (PrMonitor.Tests)
.github/      copilot-instructions.md (full architecture notes) + workflow YAML files
CHANGELOG.md  Keep a Changelog format — update [Unreleased] on every src/ change
README.md     User-facing documentation
```

See [src/AGENTS.md](src/AGENTS.md) for source-folder structure details.

## Key conventions

- All `src/` changes must pass `dotnet build .\src\PrMonitor.csproj -v q` before committing.
- Update `CHANGELOG.md` (`[Unreleased]`) for every `src/` change — mandatory, not optional.
- Commit message format: `feat:`, `fix:`, `refactor:`, `docs:`, `test:` etc.
- UI text must be in **English**.
- Settings are JSON-backed in `%APPDATA%\pr-monitor\settings.json` — see full schema in `.github/copilot-instructions.md`.
- No secrets stored anywhere; all GitHub API calls shell out to `gh`.
