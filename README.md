# PR Monitor

A lightweight Windows system-tray app that keeps an eye on your GitHub pull requests so you don't have to constantly check GitHub.

## What it does

PR Bot polls GitHub every two minutes and shows a floating window with:

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

Click the tray icon to toggle the window. Right-click for a context menu with totals and a manual refresh option.

The window can be **snapped to any corner** of any monitor by dragging it near a corner — the border turns blue to preview the snap, and the window locks into position on release. When a monitor is disconnected the window recovers to the same corner on the primary display.

## Requirements

- Windows 10 or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated with `gh auth login`

## Getting started

### 1. Authenticate with GitHub

```powershell
gh auth login
```

Follow the prompts. PR Bot uses the `gh` CLI for all API calls, so no tokens or secrets need to be stored.

### 2. Clone and run

```powershell
git clone https://github.com/your-username/pr-bot.git
cd pr-bot
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

The output is placed in `src\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\`.

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
