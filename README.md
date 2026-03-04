# PR Monitor

A lightweight Windows system-tray app that keeps an eye on your GitHub pull requests so you don't have to constantly check GitHub.

## What it does

PR Monitor polls GitHub every two minutes and shows a floating window with:

| Section | What's in it |
|---|---|
| **Hotfixes** | Open PRs targeting a `release/*` branch that you are involved in |
| **My Auto-Merge PRs** | Your own PRs with auto-merge enabled, including their CI status |
| **Awaiting My Review** | PRs where your review has been requested |
| **My PRs** | Your own open PRs without auto-merge (collapsed by default); draft PRs show a grey CI indicator |
| **Later** | PRs you've snoozed with "Move to later" |

Empty sections are hidden automatically. The tray icon badge changes colour to reflect the worst state:

- 🔴 Red — one or more CI failures
- 🟡 Amber — reviews pending, no CI failures
- 🟢 Green — everything is fine
- ⚫ Gray — not yet polled

Click the tray icon to toggle the window. Right-click for a context menu with totals, settings, and a non-clickable app version line (for example, `Version 1.0.0`).

The window can be **snapped to any corner** of any monitor by dragging it near a corner — the border turns blue to preview the snap, and the window locks into position on release. When a monitor is disconnected the window recovers to the same corner on the primary display.

## Requirements

- Windows 10 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated with `gh auth login`

## Getting started

### 1. Authenticate with GitHub

```powershell
gh auth login
```

Follow the prompts. PR Monitor uses the `gh` CLI for all API calls, so no tokens or secrets need to be stored.

### 2. Clone and run

```powershell
git clone https://github.com/jvanoostveen/pr-monitor.git
cd pr-monitor
dotnet run --project .\src\PrMonitor.csproj
```

The app starts minimised to the system tray. Click the tray icon to open the PR window.

### 3. Configure

Right-click the tray icon and choose **Settings** to:

- Add the GitHub **organisations** to include in search results (leave empty for personal repos only)
- Adjust the **polling interval** (default: 120 seconds)
- Enable **auto-start with Windows**

Settings are stored in `%APPDATA%\pr-monitor\settings.json`.

## Building a release executable

```powershell
dotnet publish .\src\PrMonitor.csproj -c Release -r win-x64 --self-contained
```

The output is placed in `src\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\`.

## GitHub Actions workflows

The repository includes two separate GitHub Actions workflows:

### Build validation (CI)

Workflow: `.github/workflows/ci-build.yml`

- Triggered on `pull_request` to `main`
- Triggered on `push` to `main`
- Runs restore + build for `src/PrMonitor.csproj` using .NET 10 on `windows-latest`
- Build only (no tags, releases, or uploaded artifacts)

### Release automation

Workflow: `.github/workflows/release-on-version-change.yml`

- Triggered on pushes to `main` when `src/PrMonitor.csproj` changes, or manually via `workflow_dispatch`
- Reads the app version from `<Version>` in `src/PrMonitor.csproj`
- On push, creates a release only if the version changed compared to the previous commit
- Skips release creation if tag `v<version>` already exists
- Publishes a Windows `win-x64` build and uploads `PrMonitor-<version>-win-x64.zip` to the GitHub Release

## Development

```powershell
# Stop any running instance before rebuilding (the build overwrites the exe)
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue

# Build
dotnet build .\src\PrMonitor.csproj

# Run
dotnet run --project .\src\PrMonitor.csproj
```

The app enforces a single instance via a named mutex (`PrMonitor_SingleInstance`). Launching a second instance shows a message and exits.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
