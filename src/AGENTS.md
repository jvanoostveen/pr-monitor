# src/ — Source Structure

This folder contains the entire WPF application (`PrMonitor.csproj`, targeting `net10.0-windows10.0.17763.0`).

## Entry points

| File | Role |
|---|---|
| `App.xaml / App.xaml.cs` | WPF application entry; single-instance mutex; wires all services; owns `DiagnosticsLogger` |
| `MainWindow.xaml / .xaml.cs` | Floating PR list window (borderless, topmost, `MaxHeight=700`, `SizeToContent=Height`) |

## Folder map

```
Assets/           icon.ico
Converters/       WPF value converters (registered in App.xaml resources)
Models/           Plain data models (CIState, PullRequestInfo, FlakinessRule, ...)
Services/         All business logic (GitHub API, polling, notifications, flakiness, updates)
Settings/         AppSettings — JSON persistence in %APPDATA%\pr-monitor\settings.json
ViewModels/       MainViewModel (+ inner PrItemViewModel), SettingsViewModel
Views/            TrayIconManager, IconGenerator, AboutWindow, SettingsWindow, FlakinessRulesWindow
```

## Services at a glance

| Service | Responsibility |
|---|---|
| `DiagnosticsLogger` | Thread-safe log to `%APPDATA%\pr-monitor\logs\pr-monitor.log`; size rotation |
| `GitHubService` | All `gh api graphql` calls; PR queries; workflow run helpers; mention notifications |
| `PollingService` | Timer (120 s default); emits `PrChanged`, `Polled`, `PollFailed`, `MentionDetected` |
| `NotificationService` | Windows toast via `Microsoft.Toolkit.Uwp.Notifications`; honours `NotificationMode` |
| `UpdateService` | Checks latest GitHub release; timer (30 s after startup, then 24 h) |
| `CopilotService` | GitHub Models API (`gpt-4o-mini`) — flakiness classification |
| `FlakinessService` | Orchestrates CI failure → local rule check → Copilot → auto-rerun |

## Naming conventions

- ViewModels → `*ViewModel.cs` in `ViewModels/`
- Services → `*Service.cs` in `Services/`
- Converters → `*Converter.cs`; registered in `App.xaml` resources as camelCase keys
- XAML event handlers → `ElementName_EventName`

## Window sections (in order)

1. Hotfixes (shown only when count > 0)
2. My Auto-Merge PRs
3. Awaiting My Review
4. My PRs
5. Team Review Requests (shown only when count > 0 and feature enabled)
6. Later

Each section is collapsible; state persisted in `AppSettings`.

## PR row context menu (native Win32)

Copy PR URL · Copy branch name · Rerun failed jobs · Request Copilot review ·  
Move to later ▶ (1 h / 4 h / Tomorrow / Next week (Monday 09:00) / Indefinitely) · Hide · Restore · Mark as ready · Convert to draft

Note: the WPF `ContextMenu` blocks in XAML are dead code — the `PreviewMouseRightButtonUp` handler always shows the native Win32 menu instead.

## Tray context menu (native Win32)

Open/Close PR Monitor · About… · Settings… · *(separator)* · Hotfixes (N) · My PRs (N) · Awaiting Review (N) · *(separator)* · Exit
