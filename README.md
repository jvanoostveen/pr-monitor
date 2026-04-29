# PR Monitor

[![CI Build](https://github.com/jvanoostveen/pr-monitor/actions/workflows/ci-build.yml/badge.svg?branch=main)](https://github.com/jvanoostveen/pr-monitor/actions/workflows/ci-build.yml)

A lightweight Windows system-tray app that keeps an eye on your GitHub pull requests so you don't have to constantly check GitHub.

## What it does

PR Monitor polls GitHub every two minutes and shows a floating window with:

| Section | What's in it |
|---|---|
| **Hotfixes** | Open PRs targeting a `release/*` branch that are yours or explicitly assigned to you |
| **My Auto-Merge PRs** | Your own PRs with auto-merge enabled, including their CI status |
| **Awaiting My Review** | PRs where your review has been requested directly (including assignee-only PRs) |
| **My PRs** | Your own open non-draft PRs without auto-merge (collapsed by default) |
| **Dependabot** | Dependabot PRs awaiting your review (collapsed by default) |
| **Team Review Requests** | PRs where a review was requested from a team you belong to (collapsed by default; can be disabled in Settings) |
| **My Draft PRs** | Your own draft PRs (collapsed by default) |
| **Later** | PRs you've snoozed with "Move to later" |

Each PR row keeps its CI status circle and also shows a message icon when unresolved review comments exist; hovering the icon shows the unresolved comment count.

Empty sections are hidden automatically. The tray icon badge changes colour to reflect the worst state:

- 🔴 Red — one or more CI failures
- 🟡 Amber — reviews pending or unresolved review comments on your own PRs, no CI failures
- 🟣 Purple — pipeline still running (pending CI), no failures or review actions needed
- 🟢 Green — everything is fine
- 🔵 Blue — only "Later" items, nothing active
- ⚫ Gray — not yet polled

Click the tray icon to toggle the window. Right-click for a context menu with **Open PR Monitor**, **About…**, **Settings…**, and totals per section.

Windows toast notifications are shown under the app name **PR Monitor**.

The app automatically checks for a new release ~30 seconds after startup and again every 24 hours. When a newer version is available, a green clickable banner appears at the bottom of the PR window showing the target version. Click the banner to download the update, and once it is ready the banner switches to a restart action. The adjacent **What's new?** link opens an in-app changelog view sourced from `CHANGELOG.md`, filtered to the versions between your current build and the latest release. A manual check is also available from **About… → Check for updates…**.

Right-clicking a PR row shows a context menu with:
- **Copy PR URL** — copies the PR URL to the clipboard
- **Copy branch name** — copies the head branch name to the clipboard
- **Rerun failed jobs** — retriggers failed CI runs for the PR (enabled for failed, non-draft PRs)
- **Request Copilot review** — requests (or re-requests) a Copilot review for the PR
- **Assign reviewer** (submenu, shown for own non-draft PRs) — currently assigned reviewers appear with a checkmark (click to remove); up to 10 recently used reviewers are listed in alphabetical order for one-click assignment, showing full names when known (fallback to handle); **Search…** opens a dialog to find any org member by login or display name
- **Move to later** (submenu: 1 hour / 4 hours / Tomorrow morning / Next week (Monday 09:00) / Indefinitely) — snoozes the PR into the Later section
- **Hide** — hides the PR completely from the main window (no dedicated window section)
- **Restore** — moves the PR back from the Later section
- **Mark as ready** / **Convert to draft** — toggles the PR’s draft state (shown when applicable)

Hidden PRs can be managed from **Settings → Hidden PRs**. Removing an entry there makes that PR visible in the main window again.

PR row right-click actions now use a native Win32 popup menu (same rendering path as the tray icon menu), so visuals and dark/light behavior stay aligned with Windows.

The window can be **snapped to any corner** of any monitor by dragging it near a corner — the border turns blue to preview the snap, and the window locks into position on release. Snapped placement uses a compact 6 px edge inset. When a monitor is disconnected the window recovers to the same corner on the primary display.

PR Monitor now also remembers whether the window was open, plus its last position. If it was visible when you last used the app, it opens automatically on startup and restores the previous location. If monitor layout changed, the window is moved by the smallest possible amount so it is fully visible.
On first show after startup, the saved position is applied before any fallback corner alignment, so secondary-monitor placement is retained across restarts.
Startup ignores pre-restore size-driven auto-alignment, and snapped windows keep a monitor anchor derived from restored coordinates so early layout passes cannot drift them to another screen.
After a user drag ends, the final location and snapped corner are persisted immediately, so a restart does not depend on a later tray-hide or app-exit save.

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
- Configure **Notifications** — choose when to show toasts: **Always** (default), **Only when window is closed**, or **Never**, with per-event-type toggles for CI failures, review requests, and more
- Configure **Flakiness** options, including limiting AI flakiness analysis to **My Auto-Merge PRs** only and setting **Maximum automatic reruns** (1-10, default 3)

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
- Runs `dotnet test` on `tests/PrMonitor.Tests` (xUnit)
- Build + test validation only (no tags, releases, or uploaded artifacts)

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

# Run tests
dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj

# Run
dotnet run --project .\src\PrMonitor.csproj
```

The app enforces a single instance via a named mutex (`PrMonitor_SingleInstance`). Launching a second instance shows a message and exits.

### Troubleshooting

For diagnostics when polling or GitHub API calls intermittently return no data, check the local log file:

- `%APPDATA%\pr-monitor\logs\pr-monitor.log`

The file contains timestamped `INFO`, `WARN`, and `ERROR` entries.

Window restore and restart-placement troubleshooting now also writes structured `MainWindowPlacement` entries to the same log, including saved coordinates, snap corner, chosen monitor, deferred resize branches, and final persisted state.
Those traces are also used to diagnose drag-time snap issues; while the window is being dragged, deferred resize auto-positioning is now suppressed so an old snapped corner cannot pull it back across the screen.

Update-check failures are logged there as well (including HTTP status or exception details), and the manual **Check for updates…** action in **About…** shows the concrete error reason.

Shutdown reliability note: app exit now guards single-instance mutex release, so a tray-menu exit/right-click shutdown path will not crash if the current thread does not own the mutex.

For flakiness analysis, PR Monitor uses `gpt-4o-mini` through GitHub Models.

If Windows SmartScreen shows "Windows protected your PC" for `PrMonitor.exe`:

1. Right-click `PrMonitor.exe` and choose **Properties**.
2. In the **General** tab, check **Unblock** (under Security) and click **Apply**.
3. Start the app again.

You may still need to click **More info** → **Run anyway** the first time because the app is unsigned (`Unknown publisher`).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
