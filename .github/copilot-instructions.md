# PR Monitor – Copilot Instructions

## Project Overview
Windows system-tray app that monitors GitHub PRs: own auto-merge PRs (with CI status) and PRs awaiting your review. Built with C# / WPF / .NET 10 and the `gh` CLI for authentication and API access.

Repository license: MIT (see `LICENSE`).

**Language**: The entire UI (labels, menu items, tooltips, messages) must be in **English**.

---

## Tech Stack

| Concern | Choice |
|---|---|
| Language | C# 12 |
| Framework | .NET 10 WPF (`net10.0-windows10.0.17763.0`) |
| UI extras | WinForms (`UseWindowsForms=true`) for `NotifyIcon` |
| Auth / API | `gh` CLI → `gh api graphql` subprocess |
| Notifications | `Microsoft.Toolkit.Uwp.Notifications` v7.1.3 |
| Settings | JSON in `%APPDATA%/pr-monitor/settings.json` |
| Diagnostics log | `%APPDATA%/pr-monitor/logs/pr-monitor.log` |
| Version source | `<Version>` in `src/PrMonitor.csproj` |

---

## Project Structure

```
pr-bot/
├── pr-bot.slnx
└── src/
    ├── PrMonitor.csproj
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
    │   ├── PullRequestInfo.cs          # PR data model (includes HeadCommitSha)
    │   ├── FailureContext.cs           # Context passed to flakiness AI analysis
    │   ├── FlakinessAnalysisResult.cs  # AI analysis result + suggested rules
    │   ├── FlakinessRule.cs            # Persisted flakiness regex rule
    │   └── RerunRecord.cs              # Per-PR rerun count + timestamp
    ├── Services/
    │   ├── GitHubService.cs            # GraphQL via `gh api graphql` + workflow run helpers
    │   ├── PollingService.cs           # Timer polling + delta events (incl. MyPrs CI changes)
    │   ├── NotificationService.cs      # Windows toast on PR changes + Notify() helper
    │   ├── UpdateService.cs            # GitHub latest release check + version compare
    │   ├── CopilotService.cs           # GitHub Models API (gpt-5-mini) flakiness analysis
    │   └── FlakinessService.cs         # CI failure analysis orchestrator + auto-rerun
    ├── Settings/
    │   └── AppSettings.cs              # JSON-backed settings
    ├── ViewModels/
    │   ├── MainViewModel.cs            # Main window VM + PrItemViewModel (inner)
    │   └── SettingsViewModel.cs        # Settings window VM
    └── Views/
        ├── TrayIconManager.cs          # NotifyIcon + context menu
        ├── IconGenerator.cs            # Generates 16×16 icon with colored badge
      ├── AboutWindow.xaml / .cs      # About dialog (version/repo/update check)
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
When a change includes files under `src/`, run `dotnet build .\src\PrMonitor.csproj -v q` and confirm `ExitCode: 0` before committing.

### 4. Update documentation
After completing any user-facing change, update **both**:
- `README.md` — reflect new sections, behaviours, or settings in the feature table and prose
- `.github/copilot-instructions.md` — update architecture notes, settings schema, and section descriptions to match the current state of the code

For **every commit that touches `src/`**, also update `CHANGELOG.md`:
- Add an entry under `[Unreleased]` in the appropriate Keep a Changelog category (`Added`, `Changed`, `Fixed`, etc.).
- This is **mandatory**, not optional — even for small bug fixes or cosmetic changes.

Commit the documentation in the same commit as the code change.

### 5. Commit every completed step
Do not batch unrelated work into one commit. After each completed implementation step:
- If `src/` files changed: stop the running app, build, and confirm success (`ExitCode: 0`)
- Commit only the files for that step
- If `src/` files changed: restart the app so changes are visible
- Continue with the next step in a new commit

When a request contains multiple deliverables (for example framework migration + UI polish), create separate commits per deliverable.

---



When a change includes files under `src/`, stop the running instance before building, because the build tries to overwrite the exe and will retry 5 times if it is locked.

```powershell
# Stop any running instance
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue

# Build
dotnet build .\src\PrMonitor.csproj

# Run (development)
dotnet run --project .\src\PrMonitor.csproj
```

Or combined before a build/commit:
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue; dotnet build .\src\PrMonitor.csproj
```

The app is **single-instance** (Mutex `PrMonitor_SingleInstance`). Launching a second instance shows a message box and exits immediately.

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

`PollingService` also writes lightweight diagnostics log entries for poll start/end and poll exceptions.

### Diagnostics logging
- `DiagnosticsLogger` writes thread-safe entries to `%APPDATA%/pr-monitor/logs/pr-monitor.log` with automatic size-based rotation.
- Log format includes timestamp + level (`INFO`, `WARN`, `ERROR`).
- `GitHubService` logs GraphQL/`gh` failures (non-zero exit with stderr, GraphQL errors, JSON parse failures).
- `PollingService` logs poll lifecycle and exceptions for intermittent "no data" investigations.

### Flakiness analysis and auto-rerun
- `FlakinessService` subscribes to `PollingService.PrChanged` and handles `CIStatusChanged` events for the current user's own non-draft PRs with `CIState.Failure`.
- **Local rule check first**: enabled `FlakinessRules` (regex patterns) are matched against the log excerpt. If a rule matches, the CI run is immediately retried without calling the AI.
- **Copilot fallback**: if no rule matches, `CopilotService` calls the GitHub Models API (`gpt-5-mini`, endpoint `https://models.inference.ai.azure.com`) with a compact `FailureContext` object (PR metadata + failed check names + ≤4000 char log excerpt). The Bearer token is obtained via `gh auth token`.
- **Auto-rerun**: `gh run rerun {runId} --failed --repo {owner}/{repo}` is invoked. Max 3 reruns per PR, counter persisted in `settings.json` and pruned after 30 days.
- **Suggested rules**: after each Copilot analysis, any suggested `.NET regex` patterns are persisted to `FlakinessRules` (auto-enabled) and reused in future without calling the AI.
- **Real failure toast**: when Copilot concludes the failure is not flaky, a toast is shown with the one-sentence rationale.
- The feature is disabled by default (`flakinessAnalysisEnabled: false`) and can be enabled in Settings → Flakiness tab.
- `NotificationService.Notify(title, body)` is a public helper for ad-hoc toasts outside the poll cycle.
- `PollingService` also tracks CI changes on "My PRs" (non-auto-merge) via `DetectMyPrsChanges`, so flakiness analysis covers both auto-merge and regular own PRs.
- `PullRequestInfo.HeadCommitSha` is populated from the GraphQL `oid` field and used to resolve the correct workflow run ID.

### Notification app name
- Windows toast notifications should display the app name as **PR Monitor** (configured via project metadata in `src/PrMonitor.csproj`).

### Main Window behavior
- Borderless, transparent, `Topmost=True`, `SizeToContent=Height`, `MaxHeight=600`
- **No auto-hide on deactivate** — stays visible until user clicks X or tray icon
- **Tray left-click** toggles window visibility
- Tray context menu order starts with **Open PR Monitor**, then **About…**, then **Settings…**
- **Draggable** by the title/timestamp area in the header (cursor: SizeAll)
- **Buttons** (Refresh, Close) use `MouseLeftButtonUp` — NOT inside the drag zone — to avoid `DragMove()` hijacking mouse capture
- Default position: bottom-right of primary monitor work area (6 px inset)
- `_userMoved` flag: once user drags, window stays put; otherwise re-aligns on resize/expand/collapse
- **Corner snapping**: while dragging, `DetectNearCorner()` checks the current monitor's work area via `Screen.FromHandle`. When the window is within 80 px of a corner the border turns blue (snap indicator). On mouse-up the window snaps into that corner. The snapped corner is remembered so expand/collapse re-applies it. `EnsureOnScreen()` recovers the window to the primary monitor if its monitor is disconnected.
- **Window restore persistence**: window visibility and `Left`/`Top` are persisted in settings. On startup, if it was visible last session, it opens automatically and restores the saved position. Restored/shown positions are clamped to monitor work areas with minimal displacement so the full window stays visible after monitor changes.
- All screen coordinates go through `ScreenRectToWpf()` (device → WPF units via `PresentationSource.TransformFromDevice`) to handle mixed-DPI setups.

### Collapsible sections
Six collapsible sections in order: Hotfixes, My Auto-Merge PRs, Awaiting My Review, My PRs, Team Review Requests, Later. State persisted in `AppSettings` (`HotfixExpanded`, `AutoMergeExpanded`, `ReviewExpanded`, `MyPrsExpanded`, `TeamReviewExpanded`, `LaterExpanded`). `BoolToAngleConverter` rotates chevron (0° = expanded, -90° = collapsed). Hotfixes is only shown when `HotfixCount > 0`; Team Review Requests only when `TeamReviewCount > 0` (which is 0 when `ShowTeamReviewSection` is false); all other sections likewise hide when their count is zero.

### CI status display
Each PR row shows a colored 10×10 `Ellipse`:
- `#3FB950` green — Success
- `#F85149` red — Failure  
- `#D29922` amber — Pending
- `#F0883E` orange — Error
- `#484F58` gray — Unknown

For **My PRs** rows, `PrItemViewModel.EffectiveCIState` is used instead of `CIState` — draft PRs always return `CIState.Unknown` so their indicator is grey regardless of actual build state.

### Unresolved review comments indicator
- PR rows keep the CI circle unchanged and can show an additional message icon (`Segoe MDL2 Assets`, `E8BD`) when unresolved review comments are present.
- Tooltip text is in English and includes the unresolved comment count (for example: `3 unresolved review comments`).
- Data is sourced from GraphQL `reviewThreads` per PR by counting unresolved threads (`isResolved == false`) and summing their `comments.totalCount`.

### Tray icon colors
- Red `#F85149` — CI failures present
- Amber `#D29922` — reviews pending or unresolved comments on My PRs, no CI failures
- Purple `#8957E5` — pipeline running (Pending CI on visible PRs), no failures or review actions
- Green `#3FB950` — all clear
- Blue `#005FAA` — only Later-items, nothing active
- Gray `#8B949E` — idle / not polled yet

### Version display
- App version is defined once in `src/PrMonitor.csproj` via `<Version>`.
- Runtime reads the assembly informational/file version and shows it in tray context menu as a disabled item: `Version x.y.z`.

### Update checks
- `UpdateService` calls `gh api repos/jvanoostveen/pr-monitor/releases/latest` first (authenticated), with HTTP `GET https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest` as fallback, and parses `tag_name` + `html_url`.
- Current version is read from assembly metadata; tags like `v1.2.3` are normalized before semantic comparison.
- A `System.Threading.Timer` fires 30 seconds after startup then every 24 hours; it calls `RunAutoUpdateCheckAsync()` which silently calls `MainViewModel.SetUpdateAvailable()` when a newer version is found.
- When an update is available, the PR window footer shows a green clickable banner ("Update available: vX.Y.Z — click to download"); clicking it opens the release URL. The normal hint text is shown when no update is pending.
- Manual checks are triggered from the **Check for updates…** button in the About dialog; the manual check still shows a MessageBox for immediate feedback and also updates the banner.
- Update-check failures are logged to diagnostics (`pr-monitor.log`), and manual checks show the concrete error message instead of a generic failure.

### Release automation
- Build validation workflow: `.github/workflows/ci-build.yml`
- Triggers:
  - `pull_request` to `main`
  - `push` to `main`
- Behavior:
  - Restores and builds `src/PrMonitor.csproj` in `Release` with .NET 10 on `windows-latest`
  - Build validation only (no tag/release/upload steps)

- Release workflow: `.github/workflows/release-on-version-change.yml`
- Triggers:
  - `push` to `main` when `src/PrMonitor.csproj` changes
  - `workflow_dispatch` (manual)
- Behavior:
  - Reads version from `src/PrMonitor.csproj`
  - For push events, releases only when version changed from previous commit
  - Skips if tag `v<version>` already exists
  - Publishes a single-file `win-x64` executable (including native libraries for self-extract at runtime) and attaches `PrMonitor-<version>-win-x64.zip` (only `PrMonitor.exe`) to GitHub Release

### README onboarding
- `README.md` uses a release-first onboarding flow in **Getting started** (download latest release + run executable).
- Clone/run-from-source instructions are documented under a separate development-focused section.

### Changelog
- `CHANGELOG.md` in the repository root is the canonical version history for user-facing and versioned changes.
- Keep entries concise and grouped by Keep a Changelog categories under `[Unreleased]` and released versions.

### Version change checklist
When preparing a new version, complete these steps in the same implementation sequence:
1. Update `<Version>` in `src/PrMonitor.csproj` (single source of truth).
2. Update `CHANGELOG.md`:
  - Move relevant entries from `[Unreleased]` into a new version section (for example `[1.2.0]`).
  - Keep an `[Unreleased]` section at the top for future changes.
3. Update `README.md` only if version-related behavior or release packaging changed.
4. If `src/` files changed as part of the version prep: run build validation (`dotnet build .\src\PrMonitor.csproj -v q`).
5. Commit version + changelog updates together.

Note: release automation is triggered by changes to `src/PrMonitor.csproj`, so a version bump commit is what starts the release workflow on `main`.

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
  "hotfixExpanded": true,
  "autoMergeExpanded": true,
  "reviewExpanded": true,
  "myPrsExpanded": false,
  "teamReviewExpanded": false,
  "showTeamReviewSection": true,
  "laterExpanded": false,
  "mainWindowVisible": false,
  "mainWindowLeft": 1440.0,
  "mainWindowTop": 120.0,
  "flakinessAnalysisEnabled": false,
  "flakinessRules": [
    {
      "id": "guid",
      "pattern": ".NET regex pattern",
      "description": "Human-readable label",
      "isEnabled": true,
      "createdAt": "2026-03-24T00:00:00Z",
      "matchCount": 0
    }
  ],
  "flakinessRerunCounts": {
    "owner/repo#123": { "count": 1, "lastAttempt": "2026-03-24T00:00:00Z" }
  }
}
```

Serialized as camelCase. `AppSettings.Load()` / `settings.Save()` handle file I/O.
On `Load()`, rerun records older than 30 days are automatically pruned.

---

## Git Workflow

Commits follow conventional commits: `feat:`, `fix:`, `refactor:` etc.

Before every commit with `src/` file changes:
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrMonitor.csproj -v q
git add -A
git commit -m "type: description"
```

After every commit with `src/` file changes, restart the app so changes are visible:
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue
Start-Process dotnet -ArgumentList "run --project .\src\PrMonitor.csproj" -WorkingDirectory "d:\Private\pr-bot" -WindowStyle Hidden
```

Full iteration sequence for `src/` changes (stop → build → commit → restart):
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrMonitor.csproj -v q
git add -A
git commit -m "type: description"
Start-Process dotnet -ArgumentList "run --project .\src\PrMonitor.csproj" -WorkingDirectory "d:\Private\pr-bot" -WindowStyle Hidden
```

For docs/workflow-only changes (no `src/` files), commit directly without build/restart.
