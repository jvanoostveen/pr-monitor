# PR Monitor

[![CI Build](https://github.com/jvanoostveen/pr-monitor/actions/workflows/ci-build.yml/badge.svg?branch=main)](https://github.com/jvanoostveen/pr-monitor/actions/workflows/ci-build.yml)

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

Each PR row keeps its CI status circle and also shows a message icon when unresolved review comments exist; hovering the icon shows the unresolved comment count.

Empty sections are hidden automatically. The tray icon badge changes colour to reflect the worst state:

- 🔴 Red — one or more CI failures
- 🟡 Amber — reviews pending or unresolved review comments on your own PRs, no CI failures
- 🟣 Purple — pipeline still running (pending CI), no failures or review actions needed
- 🟢 Green — everything is fine
- 🔵 Blue — only "Later" items, nothing active
- ⚫ Gray — not yet polled

Click the tray icon to toggle the window. Right-click for a context menu with **Open PR Monitor**, **About…**, **Settings…**, totals, and a non-clickable app version line (for example, `Version 1.0.0`).

Windows toast notifications are shown under the app name **PR Monitor**.

The app automatically checks for a new release ~30 seconds after startup and again every 24 hours. When a newer version is available, a green clickable banner appears at the bottom of the PR window — click it to open the latest release page. A manual check is also available from **About… → Check for updates…**.

Right-clicking a PR row shows a context menu with:
- **Copy branch name** — copies the head branch name to the clipboard
- **Move to later** / **Restore** — moves the PR to the Later section and back

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

### 2. Download and run the latest release

Download the latest `win-x64` package from:

- https://github.com/jvanoostveen/pr-monitor/releases/latest

Extract the zip and run `PrMonitor.exe`.

The app starts minimised to the system tray. Click the tray icon to open the PR window.

### 3. Configure

Right-click the tray icon and choose **Settings** to:

- Add the GitHub **organisations** to include in search results (leave empty for personal repos only)
- Adjust the **polling interval** (default: 120 seconds)
- Enable **auto-start with Windows**

Settings are stored in `%APPDATA%\pr-monitor\settings.json`.

You can also run a manual update check at any time from **About…** in the tray menu.

## Running from source (development)

```powershell
git clone https://github.com/jvanoostveen/pr-monitor.git
cd pr-monitor
dotnet run --project .\src\PrMonitor.csproj
```

This is the recommended path for contributors and local development.

## Building a release executable

```powershell
dotnet publish .\src\PrMonitor.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The output is placed in `src\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\` as a single runnable `PrMonitor.exe`.

## Version history

See [CHANGELOG.md](CHANGELOG.md) for the versioned release history and notable changes.

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
- Publishes a single-file Windows `win-x64` executable and uploads `PrMonitor-<version>-win-x64.zip` (containing only `PrMonitor.exe`) to the GitHub Release

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

### Troubleshooting

For diagnostics when polling or GitHub API calls intermittently return no data, check the local log file:

- `%APPDATA%\pr-monitor\logs\pr-monitor.log`

The file contains timestamped `INFO`, `WARN`, and `ERROR` entries.

Update-check failures are logged there as well (including HTTP status or exception details), and the manual **Check for updates…** action in **About…** shows the concrete error reason.

If Windows SmartScreen shows "Windows protected your PC" for `PrMonitor.exe`:

1. Right-click `PrMonitor.exe` and choose **Properties**.
2. In the **General** tab, check **Unblock** (under Security) and click **Apply**.
3. Start the app again.

You may still need to click **More info** → **Run anyway** the first time because the app is unsigned (`Unknown publisher`).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
