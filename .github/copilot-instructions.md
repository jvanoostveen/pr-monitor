# PR Bot – Copilot Instructions

## Project Overview
Windows system-tray app that monitors GitHub PRs: own auto-merge PRs (with CI status) and PRs awaiting your review. Built with C# / WPF / .NET 8 and the `gh` CLI for authentication and API access.

**Language**: The entire UI (labels, menu items, tooltips, messages) must be in **English**.

---

## Tech Stack

| Concern | Choice |
|---|---|
| Language | C# 12 |
| Framework | .NET 8 WPF (`net8.0-windows10.0.17763.0`) |
| UI extras | WinForms (`UseWindowsForms=true`) for `NotifyIcon` |
| Auth / API | `gh` CLI → `gh api graphql` subprocess |
| Notifications | `Microsoft.Toolkit.Uwp.Notifications` v7.1.3 |
| Settings | JSON in `%APPDATA%/pr-bot/settings.json` |

---

## Project Structure

```
pr-bot/
├── pr-bot.slnx
└── src/
    ├── PrBot.csproj
    ├── App.xaml / App.xaml.cs          # Entry point, wiring, single-instance
    ├── MainWindow.xaml / .xaml.cs      # Floating PR list window
    ├── AssemblyInfo.cs
    ├── Assets/
    │   └── icon.ico
    ├── Converters/
    │   ├── ZeroToVisibleConverter.cs   # int == 0 → Visible
    │   ├── BoolToAngleConverter.cs     # true → 0°, false → -90° (chevron)
    │   └── CIStateToBrushConverter.cs  # CIState → hex color brush
    ├── Models/
    │   ├── CIState.cs                  # Enum: Unknown/Pending/Success/Failure/Error
    │   └── PullRequestInfo.cs          # PR data model
    ├── Services/
    │   ├── GitHubService.cs            # GraphQL via `gh api graphql`
    │   ├── PollingService.cs           # Timer polling + delta events
    │   └── NotificationService.cs      # Windows toast on PR changes
    ├── Settings/
    │   └── AppSettings.cs              # JSON-backed settings
    ├── ViewModels/
    │   ├── MainViewModel.cs            # Main window VM + PrItemViewModel (inner)
    │   └── SettingsViewModel.cs        # Settings window VM
    └── Views/
        ├── TrayIconManager.cs          # NotifyIcon + context menu
        ├── IconGenerator.cs            # Generates 16×16 icon with colored badge
        └── SettingsWindow.xaml / .cs   # Settings dialog
```

---

## Planning & Execution Workflow

For **every user request** — no matter how small — follow this process:

### 1. Create a todo list first
Before writing any code, break the work into concrete, actionable steps and track them with the `manage_todo_list` tool. Mark each item as `in-progress` when starting it, and `completed` immediately when done.

Example for "add a new section to the window":
- [ ] Read relevant files (ViewModel, XAML, service)
- [ ] Update model/service layer
- [ ] Update ViewModel
- [ ] Update XAML
- [ ] Build and verify no errors
- [ ] Commit and restart

### 2. Use subagents for multi-file changes
When a task touches **3 or more files** or requires independent research alongside implementation, delegate to a subagent using the `runSubagent` tool with the **"Beast Mode"** agent. Provide the subagent with:
- The full task description
- Relevant file paths to read
- Expected output (which files to change and how)
- The build/commit/restart commands to run at the end

Use the main agent for: simple single-file edits, quick investigations, running terminal commands, and reviewing subagent output.

### 3. Validate before committing
Always run `dotnet build .\src\PrBot.csproj -v q` and confirm `ExitCode: 0` before committing.

---



**Always stop the running instance before building**, because the build tries to overwrite the exe and will retry 5 times if it is locked.

```powershell
# Stop any running instance
Stop-Process -Name PrBot -Force -ErrorAction SilentlyContinue

# Build
dotnet build .\src\PrBot.csproj

# Run (development)
dotnet run --project .\src\PrBot.csproj
```

Or combined before a build/commit:
```powershell
Stop-Process -Name PrBot -Force -ErrorAction SilentlyContinue; dotnet build .\src\PrBot.csproj
```

The app is **single-instance** (Mutex `PrBot_SingleInstance`). Launching a second instance shows a message box and exits immediately.

---

## Key Architecture Notes

### Authentication
No secrets stored. All GitHub API calls shell out to:
```csharp
Process.Start("gh", "api graphql -f query=... -f q=...")
```
User runs `gh auth login` once. Username is auto-detected via `gh api user` and cached in settings.

### Polling
`PollingService` runs a timer (default 120 s). `RefreshAsync()` is public for manual trigger. Events:
- `PrChanged(PrChangeEvent)` — per individual change (for toast)
- `Polled(PollSnapshot)` — full snapshot after each poll cycle

### Main Window behavior
- Borderless, transparent, `Topmost=True`, `SizeToContent=Height`, `MaxHeight=600`
- **No auto-hide on deactivate** — stays visible until user clicks X or tray icon
- **Tray left-click** toggles window visibility
- **Draggable** by the title/timestamp area in the header (cursor: SizeAll)
- **Buttons** (Refresh, Close) use `MouseLeftButtonUp` — NOT inside the drag zone — to avoid `DragMove()` hijacking mouse capture
- Default position: bottom-right of `SystemParameters.WorkArea` (12 px inset)
- `_userMoved` flag: once user drags, window stays put; otherwise re-aligns on resize/expand/collapse
- `EnsureFullyVisible()` applies minimum translation to keep window fully on-screen after resize

### Collapsible sections
Two sections: "Auto-Merge PRs" and "Awaiting Review". State persisted in `AppSettings` (`AutoMergeExpanded`, `ReviewExpanded`). `BoolToAngleConverter` rotates chevron (0° = expanded, -90° = collapsed).

### CI status display
Each PR row shows a colored 10×10 `Ellipse`:
- `#3FB950` green — Success
- `#F85149` red — Failure  
- `#D29922` amber — Pending
- `#F0883E` orange — Error
- `#484F58` gray — Unknown

### Tray icon colors
- Red `#F85149` — CI failures present
- Amber `#D29922` — reviews pending, no CI failures
- Green `#3FB950` — all clear
- Gray `#8B949E` — idle / not polled yet

---

## Common WPF Pitfalls in This Project

1. **`DragMove()` breaks button hover and click events**: only call it from a dedicated drag element (StackPanel on title), NOT from the whole header border. Buttons must not be children of the drag element.

2. **`DataTrigger` with `TargetName` in a `Style`**: not allowed outside `ControlTemplate`. Drive animations from code-behind instead (see `UpdateRefreshIcon` in `MainWindow.xaml.cs`).

3. **WPF + WinForms type ambiguity**: always use fully-qualified names:
   - `System.Windows.Application` (not `Application`)
   - `System.Windows.MessageBox`
   - `System.Windows.Media.Color`

4. **`ActualHeight` is 0 before first render**: use the `Loaded` event or check `ActualHeight > 0` before positioning.

5. **`JsonElement?` nullable**: use `is not { } jsonValue` pattern, not `.HasValue`.

---

## Naming Conventions

- ViewModels: `*ViewModel.cs` in `ViewModels/`
- Services: `*Service.cs` in `Services/`
- Converters: `*Converter.cs` registered in `App.xaml` resources as `CamelCase` keys
- XAML event handlers: `ElementName_EventName` (e.g. `RefreshButton_Click`, `AutoMergeHeader_Click`)

---

## Settings Schema (`%APPDATA%/pr-bot/settings.json`)

```json
{
  "organizations": ["org1", "org2"],
  "pollingIntervalSeconds": 120,
  "autoStartWithWindows": true,
  "gitHubUsername": "your-username",
  "autoMergeExpanded": true,
  "reviewExpanded": true
}
```

Serialized as camelCase. `AppSettings.Load()` / `settings.Save()` handle file I/O.

---

## Git Workflow

Commits follow conventional commits: `feat:`, `fix:`, `refactor:` etc.

Before every commit:
```powershell
Stop-Process -Name PrBot -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrBot.csproj -v q
git add -A
git commit -m "type: description"
```

After every commit, restart the app so changes are visible:
```powershell
Stop-Process -Name PrBot -Force -ErrorAction SilentlyContinue
Start-Process dotnet -ArgumentList "run --project .\src\PrBot.csproj" -WorkingDirectory "d:\Private\pr-bot" -WindowStyle Hidden
```

Full iteration sequence (stop → build → commit → restart):
```powershell
Stop-Process -Name PrBot -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrBot.csproj -v q
git add -A
git commit -m "type: description"
Start-Process dotnet -ArgumentList "run --project .\src\PrBot.csproj" -WorkingDirectory "d:\Private\pr-bot" -WindowStyle Hidden
```
