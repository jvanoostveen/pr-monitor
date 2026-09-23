# PR Monitor — Architecture

Canonical technical reference for this repository: tech stack, folder layout, per-feature
behaviour, WPF constraints, and the settings schema.

Windows system-tray app that monitors GitHub PRs — own auto-merge PRs (with CI status) and PRs
awaiting your review. Built with C# / WPF / .NET 10 and the `gh` CLI for authentication and API
access. Repository license: MIT (see [LICENSE](LICENSE)).

The entire UI (labels, menu items, tooltips, messages) is in **English**.

**Keep this file current.** Any change to services, sections, settings, or window behaviour must be
reflected here in the same commit — see the documentation step in
[.github/copilot-instructions.md](.github/copilot-instructions.md).

Agent workflow lives elsewhere: [CLAUDE.md](CLAUDE.md) (Claude Code),
[.github/copilot-instructions.md](.github/copilot-instructions.md) (GitHub Copilot),
[AGENTS.md](AGENTS.md) (other agents).

---

## Contents

- [Tech stack](#tech-stack)
- [Project structure](#project-structure)
- [Process model](#process-model)
- [Subsystems and behaviour](#subsystems-and-behaviour) — authentication, polling, failure
  handling, diagnostics, flakiness, statistics, memory, window behaviour, sections, CI status, checks panel,
  stacks, reviewers, tray icon, updates, release automation
- [WPF pitfalls](#wpf-pitfalls)
- [Naming conventions](#naming-conventions)
- [Settings schema](#settings-schema-appdatapr-monitorsettingsjson)

---

## Tech stack

| Concern | Choice |
|---|---|
| Language | C# 12 |
| Framework | .NET 10 WPF (`net10.0-windows10.0.17763.0`) |
| UI extras | WinForms (`UseWindowsForms=true`) for `NotifyIcon` |
| Auth / API | `gh` CLI → `gh api graphql` subprocess |
| Notifications | `Microsoft.Toolkit.Uwp.Notifications` v7.1.3 |
| Settings | JSON in `%APPDATA%/pr-monitor/settings.json` |
| Statistics | JSON in `%APPDATA%/pr-monitor/statistics.json` |
| Diagnostics log | `%APPDATA%/pr-monitor/logs/pr-monitor.log` |
| Version source | `<Version>` in `src/PrMonitor.csproj` |

---

## Project structure

```
pr-monitor/
├── ARCHITECTURE.md                        # This file — canonical technical reference
├── CLAUDE.md                              # Workflow for Claude Code (auto-loaded)
├── AGENTS.md                              # Workflow for other agents (Codex etc.)
├── CHANGELOG.md                           # Keep a Changelog version history
├── .github/
│   ├── copilot-instructions.md            # Workflow for GitHub Copilot
│   └── workflows/                         # ci-build.yml, release-on-version-change.yml
├── pr-monitor.slnx
├── src/
│   ├── PrMonitor.csproj
│   ├── App.xaml / App.xaml.cs          # Entry point, wiring, single-instance
│   ├── MainWindow.xaml / .xaml.cs      # Floating PR list window
│   ├── AssemblyInfo.cs
│   ├── Assets/
│   │   └── icon.ico
│   ├── Converters/
│   │   ├── ZeroToVisibleConverter.cs      # int == 0 → Visible
│   │   ├── NonZeroToVisibleConverter.cs   # int != 0 → Visible
│   │   ├── BoolToAngleConverter.cs        # true → 0°, false → -90° (chevron)
│   │   ├── CIStateToBrushConverter.cs     # CIState → hex color brush
│   │   └── CheckRunStateToBrushConverter.cs  # CheckRunState → job icon / job name brush
│   ├── Models/
│   │   ├── CIState.cs                  # Enum: Unknown/Pending/Success/Failure/Error
│   │   ├── CheckRunInfo.cs             # One CI check (workflow, name, state, duration, log URL)
│   │   ├── CheckFetchResult.cs         # Checks + Ok/Failed/RateLimited + remaining GraphQL budget
│   │   ├── PullRequestInfo.cs          # PR data model (includes HeadCommitSha, HasConflicts)
│   │   ├── FailureContext.cs           # Context passed to flakiness AI analysis
│   │   ├── FlakinessAnalysisResult.cs  # AI analysis result + suggested rules
│   │   ├── FlakinessRule.cs            # Persisted flakiness regex rule
│   │   ├── OrgMemberEntry.cs           # Cached org member (login + display name)
│   │   └── RerunRecord.cs              # Per-PR rerun count + timestamp
│   ├── Services/
│   │   ├── GitHubService.cs            # GraphQL via `gh api graphql` + workflow run helpers
│   │   ├── PollingService.cs           # Timer polling + delta events (incl. MyPrs CI changes)
│   │   ├── NotificationService.cs      # Windows toast on PR changes + Notify() helper
│   │   ├── UpdateService.cs            # GitHub latest release check + version compare
│   │   ├── CopilotService.cs           # GitHub Models API (gpt-4o-mini) flakiness analysis
│   │   ├── StatisticsService.cs        # Event/snapshot-based activity statistics collector
│   │   ├── MemoryDiagnostics.cs        # Heap/handle/GDI counters + idle memory trim
│   │   ├── AutoRefreshScheduler.cs     # One pending delayed callback; drives the checks panel
│   │   └── FlakinessService.cs         # CI failure analysis orchestrator + auto-rerun
│   ├── Settings/
│   │   ├── AppSettings.cs              # JSON-backed settings
│   │   └── StatisticsStore.cs          # JSON-backed daily statistics buckets (statistics.json)
│   ├── ViewModels/
│   │   ├── MainViewModel.cs            # Main window VM + PrItemViewModel (inner)
│   │   ├── ChecksViewModel.cs          # CI checks overlay VM + CheckItemViewModel
│   │   ├── SettingsViewModel.cs        # Settings window VM
│   │   └── StatsViewModel.cs           # Statistics window VM (per-period rows)
│   └── Views/
│       ├── TrayIconManager.cs          # NotifyIcon + context menu
│       ├── IconGenerator.cs            # Generates 16×16 icon with colored badge
│       ├── AboutWindow.xaml / .cs      # About dialog (version/repo/update check/copy diagnostics)
│       ├── ChangelogWindow.xaml / .cs  # In-app changelog view (filtered version range)
│       ├── AssignReviewerSearchWindow.xaml / .cs  # Org-member search dialog for reviewer assignment
│       ├── FlakinessRulesWindow.xaml / .cs  # Resizable/scrollable window for managing flakiness rules
│       ├── StatsWindow.xaml / .cs       # Resizable statistics table window (per-period columns)
│       └── SettingsWindow.xaml / .cs   # Settings dialog
└── tests/
    └── PrMonitor.Tests/
        └── PrMonitor.Tests.csproj  # xUnit test project (converters, services, ViewModels, settings)
```

---

## Process model

The app is **single-instance** (Mutex `PrMonitor_SingleInstance`). Launching a second instance shows a message box and exits immediately.

`App.OnExit` guards `ReleaseMutex()` and always disposes the mutex, logging a warning if release is attempted without ownership (or after disposal), to avoid shutdown-time crashes on exit paths.

---

## Subsystems and behaviour

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

`PollingService` also writes lightweight diagnostics log entries for poll start/end and poll exceptions. Additional events: `PollFailed` (on exception) and `MentionDetected` (bypasses `PrChanged` and the initial-batch suppression).

### Failure handling (never show "no PRs" for a failed call)
- `GitHubService.RunGraphQlAsync` either returns a usable response or throws `GitHubApiException` — it never returns `null`/empty. It fails on non-zero `gh` exit, empty output, unparseable JSON, and on a GraphQL `errors` array without usable `data.search.nodes`. Each page is retried up to `GraphQlMaxAttempts` (3) with 2 s / 5 s backoff before throwing.
- Because any fetch failure throws, `PollingService.PollAsync` aborts the whole cycle: `Polled` is not raised, `LatestSnapshot` and the `_previous*` delta dictionaries are untouched, and the previous PR list stays on screen. `PollFailed` sets `MainViewModel.IsOffline` and clears `IsRefreshing`.
- `PollingService.ShouldWithholdSnapshot` / `EmptiedSections` (both `internal`, unit-tested) add defence in depth against GitHub's search API intermittently returning zero hits for a valid request (`gh` exit 0, well-formed JSON, empty `nodes`). Any section that drops from at least one PR to zero in a single poll is withheld until `ConfirmationPollThreshold` (3) consecutive polls confirm it; `_withheldPollStreak` resets as soon as a poll no longer empties a section. Delta detection (`DetectAutoMergeChanges` / `DetectReviewChanges` / `DetectMyPrsChanges`) runs only when the snapshot is actually published, so a withheld poll cannot fire "PR merged/closed" toasts or inflate statistics.
- PRs in `AppSettings.HiddenPrKeys` (Later/snoozed plus manually hidden) are skipped in `NotificationService.Subscribe` and `FlakinessService.Subscribe`, so parked PRs never produce a toast or trigger flakiness analysis.
- `MainViewModel.IsEmptyState` is `HasLoadedOnce && !IsOffline && TotalPrCount == 0`, so the playful empty-state overlay is suppressed while offline.
- `CopilotService.AnalyzeFlakiness` logs `HttpRequestException`/`TaskCanceledException` as a warning (transient connectivity) instead of a full error stack; all error paths remain indeterminate.

### Diagnostics logging
- `DiagnosticsLogger` writes thread-safe entries to `%APPDATA%/pr-monitor/logs/pr-monitor.log` with automatic size-based rotation.
- Log format includes timestamp + level (`INFO`, `WARN`, `ERROR`).
- `DiagnosticsLogger.Null` is a static no-op instance used in all test classes so tests do not write to the production log.
- `GitHubService` logs GraphQL/`gh` failures (non-zero exit with stderr, GraphQL errors, JSON parse failures).
- `PollingService` logs poll lifecycle and exceptions for intermittent "no data" investigations.
- `MainWindow` writes structured `MainWindowPlacement` traces for startup restore, snap application, deferred `SizeChanged` branches, filtered `LocationChanged` events, display-change recovery, and persisted placement state so restart-position bugs can be reconstructed from one ordered timeline. These traces are only written when `VerboseLogging` is enabled in settings.
- `DiagnosticsLogger.Info()` is silently dropped unless `VerboseLogging` is `true`. `Warn` and `Error` are always written. The flag is set at startup and updated live when settings are saved.

### Flakiness analysis and auto-rerun
- `FlakinessService` subscribes to `PollingService.PrChanged` and handles `CIStatusChanged` events for the current user's own non-draft PRs with `CIState.Failure`.
- **Local rule check first**: enabled `FlakinessRules` (regex patterns) are matched against the log excerpt. If a rule matches, the CI run is immediately retried without calling the AI.
- **Copilot analysis**: if no rule matches, `CopilotService` calls the GitHub Models API (`gpt-4o-mini`, endpoint `https://models.inference.ai.azure.com`) with a compact `FailureContext` object (PR metadata + failed check names + ≤4000 char log excerpt). The CI log is sanitized before sending: GitHub Actions ISO timestamps, ANSI escape codes, XSS payloads, base64 blobs, and HTML injection strings are stripped/redacted. Truncation keeps the tail of the log (where test errors appear). The Bearer token is obtained via `gh auth token`. The system prompt treats E2E/browser tests (Playwright, Cypress, Selenium) as flaky by default. If `FlakinessCustomHints` is non-empty, it is appended to the system prompt as additional project-specific context to guide the AI classification.
- **Content-filter retry**: if the Azure OpenAI content filter blocks the request (jailbreak detection triggered by CI log content), the analysis is retried once with an error-lines-only excerpt (lines containing `error|fail|exception|assert|timeout`). If the retry also fails, the result is treated as indeterminate (no toast, no rerun consumed).
- **Auto-rerun**: `gh run rerun {runId} --failed --repo {owner}/{repo}` is invoked. Max reruns per PR is configurable in Settings (default 3), counter persisted in `settings.json` and pruned after 30 days.
- **Suggested rules**: after each Copilot analysis, any suggested `.NET regex` patterns are persisted to `FlakinessRules` (auto-enabled) and reused in future without calling the AI.
- **Real failure toast**: when Copilot concludes the failure is not flaky, a toast is shown with the one-sentence rationale.
- The feature is **enabled by default** (`flakinessAnalysisEnabled: true`) and can be disabled in Settings → Flakiness tab.
- Optional scope filter: `flakinessAutoMergeOnly` limits AI flakiness analysis to PRs in **My Auto-Merge PRs**; non-auto-merge PR failures are skipped when this is enabled.
- `NotificationService.Notify(title, body)` is a public helper for ad-hoc toasts outside the poll cycle.
- **Manage rules window**: the Flakiness tab in Settings shows a rule count and a **Manage rules…** button that opens `FlakinessRulesWindow` — a resizable, scrollable window (`CanResizeWithGrip`) owned by SettingsWindow. Rules can be enabled/disabled and deleted there; changes persist when Settings is saved.
- **Settings window width**: the main Settings dialog uses a wider fixed width so all tab headers (including **Hidden PRs**) remain on a single row without wrapping.
- **Manual rerun action**: PR row context menus include **Rerun failed jobs** (enabled only for failed, non-draft PRs with known head SHA). It resolves failed workflow runs for that commit and triggers `gh run rerun --failed`.
- **Copilot review action**: PR row context menus include **Request Copilot review** (enabled for non-draft PRs). It requests/re-requests Copilot review via the REST API (`gh api repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers --method POST -f reviewers[]=copilot-pull-request-reviewer[bot]`). The REST API is used because the Copilot reviewer is a GitHub App bot that cannot be resolved by GraphQL's `requestReviewsByLogin` (which is what `gh pr edit --add-reviewer` uses).
- **Copy actions**: PR row context menus include **Copy PR URL** and **Copy branch name** for quick clipboard actions from any PR section.
- **Snooze actions**: the **Move to later** submenu includes **1 hour**, **4 hours**, **Tomorrow morning (09:00)**, **Next week (Monday 09:00)**, and **Indefinitely**.
- Moving a PR to **Later** does not auto-expand the Later section when the first item is added; the user's current collapsed/expanded preference is preserved.
- **Hide action**: PR row context menus include **Hide**, which removes a PR from all main-window sections without placing it in a dedicated in-window hidden category.
- **Hidden PR settings**: Settings includes a **Hidden PRs** tab where manually hidden entries show a readable PR label, an **Open** action to jump to GitHub, and a remove action so those PRs appear again.
- **Legacy Later migration**: on settings load, hidden keys from older builds that have no snooze timestamp and are not marked as manual hides are automatically migrated to indefinite snoozes so they remain visible in the **Later** section after upgrade.
- **Notification mode compatibility**: settings load accepts legacy/unknown `notificationMode` string values and falls back safely, preventing full settings resets and preserving fields like `flakinessCustomHints` and `flakinessRules` during upgrades.
- `PollingService` also tracks CI changes on "My PRs" (non-auto-merge) via `DetectMyPrsChanges`, so flakiness analysis covers both auto-merge and regular own PRs.
- `PullRequestInfo.HeadCommitSha` is populated from the GraphQL `oid` field and used to resolve the correct workflow run ID.

### Statistics
- `StatisticsStore` ([src/Settings/StatisticsStore.cs](src/Settings/StatisticsStore.cs)) persists activity counters as daily buckets (`Dictionary<yyyy-MM-dd, DayStat>`) to `%APPDATA%/pr-monitor/statistics.json`. It mirrors `AppSettings`'s persistence pattern exactly: atomic write (`.tmp → .bak → primary`), camelCase JSON, an `AsyncLocal` path override (`UseStatisticsPathOverride`) and `internal LoadFrom`/`SaveTo(path)` so the test suite never touches the real file. The store also remembers the path it was loaded from so `Save()` writes back there even outside an override scope. Buckets older than ~18 months are pruned on load. Aggregation helpers: `ForDay`, `ForWeekOf` (ISO Monday–Sunday), `ForMonthOf`, `ForRange`, `Total`. `Reset()` clears all buckets and saves.
- `StatMetric` enum: `ReviewsRequested`, `ReviewsCompleted`, `OwnPrsOpened`, `OwnPrsMerged`, `CiFailures`, `FlakyReruns`, `RealFailures`. `DayStat` holds one int per metric plus `Dictionary<string,int>? ReviewsRequestedByAuthor` for the per-author breakdown; `Accumulate` merges author dicts.
- `StatisticsService` ([src/Services/StatisticsService.cs](src/Services/StatisticsService.cs)) subscribes to `PollingService.Polled` and computes its own deltas between successive snapshots (the first snapshot is a baseline that counts nothing, so pre-existing PRs at startup don't inflate numbers). It also subscribes to two `FlakinessService` events.
  - **ReviewsRequested**: a new review-request key appearing after baseline; also calls `IncrementReviewRequested(day, pr.Author)` to track per-author counts. Team review requests (`TeamReviewRequestedPrs`) are excluded unless `AppSettings.TeamReviewCountsForStatistics` is enabled (default off, toggle in Settings → Statistics).
  - **OwnPrsOpened**: a new own-PR key (authored by `GitHubUsername`) whose `CreatedAt >= service start time`.
  - **OwnPrsMerged** *(heuristic)*: an own-PR key that disappeared from every snapshot section (counts closed-not-merged too).
  - **CiFailures**: an own PR transitioning into `CIState.Failure`.
  - **ReviewsCompleted** *(heuristic)*: a review-request key that disappeared (also fires on withdrawn requests / PRs closed by others).
  - **FlakyReruns** / **RealFailures**: from `FlakinessService.FlakyRerunTriggered` / `RealFailureClassified` events.
  - The store is saved only when a counter actually changes, and `StatsChanged` is raised so an open stats window can live-refresh. Collection happens only while the app runs (no GitHub backfill). `ProcessSnapshot` and `Record` are `internal` for testing.
- `FlakinessService` raises `FlakyRerunTriggered(prKey)` when an automatic rerun fires and `RealFailureClassified(prKey)` when Copilot concludes a failure is genuine.
- `StatsViewModel` ([src/ViewModels/StatsViewModel.cs](src/ViewModels/StatsViewModel.cs)) builds one `StatRowViewModel` per metric with Today/Week/Month/Total columns. `StatRowViewModel` also carries an optional `AuthorBreakdown: IReadOnlyList<AuthorBreakdownRow>`, `HasBreakdown`, and `IsExpanded` (INPC) for the inline per-author expander. `StatsWindow` is a resizable dark-themed table opened from the tray menu (**Statistics…**) and the chart button in the main window header. `App.ShowStatsWindow()` is a create-or-focus singleton that refreshes on reopen and is wired to both entry points; `MainWindow.OpenStatisticsRequested` is the header-button callback. `StatsWindow` persists its last position and size (`StatsWindowLeft/Top/Width/Height` in settings), saved in `OnClosing` and restored in `OnSourceInitialized`; restored coordinates are clamped to the nearest available monitor work area (`ClampToBestWorkArea`) so a window from a disconnected screen is recovered onto a visible monitor.
- The **Reviews requested** row has an inline per-author expander: clicking the label text + `›` chevron toggles an indented sub-table (same SharedSizeGroup columns, `Padding="20,0,0,0"` indent). Expansion state survives auto-refreshes.
- The maximize button fits the window to content height (via temporary `SizeToContent=Height` + cap to 90% work area) instead of going full-screen.
- **Settings → Statistics tab** provides a **Reset statistics…** button (with `DarkMessageBox` confirmation) that calls `StatisticsStore.Reset()` and refreshes an open stats window. Wired via `onResetStatistics` callback in `SettingsWindow` constructor.

### Notification app name
- Windows toast notifications should display the app name as **PR Monitor** (configured via project metadata in `src/PrMonitor.csproj`).

### Memory management
The app runs for days in the tray, so allocation *retention* matters more than throughput. Relevant pieces:
- `MemoryDiagnostics` ([src/Services/MemoryDiagnostics.cs](src/Services/MemoryDiagnostics.cs)) — `Capture()` returns a `Snapshot` record struct with managed heap / committed / fragmented / LOH / POH / working-set / private bytes, gen0-2 collection counts, handle + thread counts, and GDI/USER object counts (`GetGuiResources`). `Log(logger, context)` writes it as INFO, or WARN when the working set exceeds 500 MB or GDI objects exceed 2000. `TrimMemory(logger, context)` does an LOH-compacting blocking gen2 collection, waits for finalizers, collects again, then calls `EmptyWorkingSet` and logs the after-state.
- **When trimming happens**: `MainWindow.HideToTray()` queues a trim at `DispatcherPriority.ApplicationIdle`, and `PollingService.MaybeTrimMemory()` trims at most every 10 minutes. The periodic trim is gated by `PollingService.CanTrimMemory` (wired in `App.xaml.cs` to `!_mainWindow.IsVisible`) so a blocking gen2 collection never freezes a visible window.
- **GC configuration** in `src/PrMonitor.csproj`: `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`, `RetainVMGarbageCollection=false`, `TieredPGO=true`, plus `<RuntimeHostConfigurationOption Include="System.GC.Conserve" Value="5" />`.
- **No GPU stack at all**: `App.OnStartup` sets `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` before calling `base.OnStartup`, i.e. before WPF's `MediaContext` initializes. Without it WPF creates a D3D9 device at startup and the graphics driver maps itself into the process — on Intel hardware ~150 MB of DLLs (`igc1464.dll` alone is 66 MB) plus private heaps and ~20 driver worker threads. That buys nothing here: the PR window is a layered window (`AllowsTransparency="True"`) and is always rasterized in software anyway, and the only `DropShadowEffect`s in the app live in that same window. Measured effect: working set ~346 MB → ~157 MB, private bytes ~244 MB → ~63 MB, threads ~41 → ~19. Do not remove this line to "restore hardware acceleration" — there is none to restore.
- **No always-on animations**: the PR window uses `AllowsTransparency="True"`, which makes it a software-rendered layered window. Any running WPF animation keeps the render loop redrawing the whole window every frame for as long as it runs — even when the animated element is `Collapsed` or the window is hidden — which burns CPU and leaks native surface memory. Both spinners are therefore driven from code-behind and explicitly stopped with `BeginAnimation(..., null)`: `UpdateRefreshIcon` for the header refresh glyph, and `UpdateLoadingSpinner` (gated on `IsVisible && ViewModel.IsInitialLoading`) for the initial-loading overlay. Never start a `RepeatBehavior="Forever"` storyboard from a XAML `EventTrigger` in this window.
- **No per-poll UI rebuild**: `MainViewModel.UpdateFromSnapshot` (`internal` for testing) builds rows into local `List<PrItemViewModel>`s, computes a `BuildDisplaySignature(...)` over `PrItemViewModel.DisplaySignature` (every rendered value: text, times, icons, tooltip, stack badge/indent), and only clears and refills the nine `ObservableCollection`s when the signature changed. This keeps relative `TimeAgo` text correct — a changed time string changes the signature — while skipping visual-tree regeneration of all non-virtualizing `ItemsControl`s. `_lastDisplaySignature` is reset to `null` in `HideItem`, `RestoreItem` and `RefreshFromSnapshot` (settings changes can alter rendering in ways the row signature does not capture).
- **Bounded `gh` output**: `GitHubService.RunGhStreamingTailAsync(tailBudgetChars, args)` streams stdout line by line, sanitizes each line inline, and keeps only a 64 KB tail queue (`LogTailBudgetChars`), killing the process past a 32 MB hard read cap (`StreamingReadCapChars`). `FetchFailedLogAsync` uses it, so a multi-hundred-MB CI log never lands on the LOH. `RunGhAsync` enforces a 2-minute `GhTimeout` and calls `KillProcessTree(process, args)` on timeout.
- **Hoisted regexes**: all log-sanitization patterns in `GitHubService` are `static readonly` — `RegexOptions.Compiled` emits dynamic IL, so constructing them per call permanently grows the code heaps.
- **GDI handles**: `IconGenerator.CreateTrayIcon` must `DestroyIcon` the `GetHicon()` handle. `Icon.FromHandle` does not own the handle, so the icon is cloned into a self-contained one and the raw handle released in a `finally`.
- **Bounded caches/buffers**: `PollingService.PruneConflictCache` drops `{Key}:{HeadCommitSha}` entries no longer in the latest poll; `NotificationService._pending` is capped at `MaxPendingNotifications` (200) and always drained in a `finally`.
- **About → Copy diagnostics** copies version, uptime and a `MemoryDiagnostics.Capture()` line to the clipboard for user-submitted reports.

### Main Window behavior
- Borderless, transparent, `SizeToContent=Height`, `MaxHeight=700`
- **No auto-hide on deactivate** — stays visible until user clicks X or tray icon
- **Always-on-top is configurable**: `AppSettings.AlwaysOnTop` (default `true`) controls `MainWindow.Topmost` only — all other floating traits (borderless, transparent, no taskbar icon, corner-snapping) are unaffected by this setting in either state. Toggle from **Settings → General → "Always on top (floating window)"** or the pin icon in the window header (leftmost header icon, `PinButton`/`PinIcon`, glyphs `\uF10D` pinned / `\uE6F9` unpinned). Both apply live via `MainWindow.ApplyAlwaysOnTop(bool)` — no restart required. `MainWindow.AlwaysOnTopChanged` is wired in `App.xaml.cs` to refresh an open Settings window's checkbox (`SettingsViewModel.NotifyAlwaysOnTopChanged()`) when the header icon is toggled directly. Missing `alwaysOnTop` in older `settings.json` files defaults to `true`, preserving the previous always-on-top behavior after upgrading.
- **Tray left-click** toggles window visibility
- Tray context menu order starts with **Open PR Monitor**, then **About…**, then **Settings…**, then **Statistics…**.
- PR row right-click actions use a native Win32 popup menu from `MainWindow` (not WPF `ContextMenu`) to match tray-menu rendering and Windows dark/light behavior.
- **Draggable** by the title/timestamp area in the header (cursor: SizeAll)
- **Buttons** (Refresh, Close) use `MouseLeftButtonUp` — NOT inside the drag zone — to avoid `DragMove()` hijacking mouse capture
- Default position: bottom-right of primary monitor work area (6 px inset)
- `_userMoved` flag: once user drags, window stays put; otherwise re-aligns on resize/expand/collapse
- **Corner snapping**: while dragging, `DetectNearCorner()` checks the current monitor's work area via `Screen.FromHandle`. When the window is within 80 px of a corner the border turns blue (snap indicator). On mouse-up the window snaps into that corner. The snapped corner is remembered so expand/collapse re-applies it. `EnsureOnScreen()` recovers the window to the primary monitor if its monitor is disconnected.
- **Window restore persistence**: window visibility and `Left`/`Top` are persisted in settings. On startup, if it was visible last session, it opens automatically and restores the saved position. Restored/shown positions are clamped to monitor work areas with minimal displacement so the full window stays visible after monitor changes.
- Startup placement ordering: on first show, restore saved coordinates before any fallback bottom-right alignment to avoid overwriting in-memory placement for secondary-monitor windows.
- Startup/resize positioning ignores pre-restore `SizeChanged` auto-alignment; snapped windows keep an anchor monitor derived from restored coordinates so early layout passes cannot drift a restored secondary-monitor position to another screen.
- Deferred `SizeChanged` auto-positioning is also skipped while the user is actively dragging the window, preventing the previous snapped corner from being re-applied mid-drag and corrupting the eventually persisted restart position.
- When a drag finishes, `MainWindow` immediately persists the final position and snapped corner to settings, rather than waiting for tray hide or app shutdown.
- All screen coordinates go through `ScreenRectToWpf()` (device → WPF units via `PresentationSource.TransformFromDevice`) to handle mixed-DPI setups.

### Loading / empty states
- `MainViewModel.HasLoadedOnce` flips to `true` at the end of the first `UpdateFromSnapshot` call and never resets; `IsInitialLoading` is `!HasLoadedOnce`.
- **Initial loading overlay**: shown in the content area (`MainWindow.xaml`, `Grid.Row="1"`) while `IsInitialLoading` is `true` — a slowly-rotating (2s/rotation) refresh glyph (`&#xE5D5;`, same icon as the header refresh button) plus a dimmed "Loading pull requests…" caption. Deliberately slower than the header's active-refresh spin (800ms, see `UpdateRefreshIcon` in `MainWindow.xaml.cs`) so it reads as ambient/idle rather than "refreshing now".
- **Empty state overlay**: `MainViewModel.TotalPrCount` sums every section's count **including `HiddenCount`** (Later/snoozed). `IsEmptyState` is `HasLoadedOnce && !IsOffline && TotalPrCount == 0` — the playful overlay only appears when there is truly nothing anywhere, not just when the active sections are empty. Shows a small green check icon (`&#xF0BE;`), a random headline from `MainViewModel.EmptyStateHeadlines` (`EmptyStateHeadline` property), and a fixed subheading "No PRs need your attention right now.". A new headline is only picked on the transition into the empty state (tracked via a private `_wasEmpty` guard in `UpdateFromSnapshot`), so it doesn't change while the user is looking at it, but varies across separate empty periods.
- Both overlays live as sibling `StackPanel`s alongside the existing PR-list `ScrollViewer`, inside a wrapping `Grid Grid.Row="1"`, toggled with the standard `BoolToVisibility` (`BooleanToVisibilityConverter`) — no new converters needed.

### Collapsible sections
Nine collapsible sections in order: Hotfixes, My Auto-Merge PRs, Awaiting My Review, Stacks, My PRs, Dependabot, Team Review Requests, My Draft PRs, Later. State persisted in `AppSettings` (`HotfixExpanded`, `AutoMergeExpanded`, `ReviewExpanded`, `StacksExpanded`, `DependabotExpanded`, `MyPrsExpanded`, `TeamReviewExpanded`, `DraftExpanded`, `LaterExpanded`). `BoolToAngleConverter` rotates chevron (0° = expanded, -90° = collapsed). Hotfixes is only shown when `HotfixCount > 0`; Dependabot only when `DependabotCount > 0`; Team Review Requests only when `TeamReviewCount > 0` (which is 0 when `ShowTeamReviewSection` is false — the Settings checkbox is its inverse, **Hide team review requests**, off by default, and hiding drops team-only PRs from the window instead of moving them to Awaiting My Review); My Draft PRs only when `DraftPrsCount > 0`; Stacks only when `StackedCount > 0`; all other sections likewise hide when their count is zero.

Hotfixes include only open `release/*` PRs that are either authored by the current user or explicitly assigned to the current user. PRs where the user is merely involved (for example by reviewing/commenting) are excluded from Hotfixes.

Draft PRs (own, non-hotfix, non-auto-merge) are carved out from "My PRs" in `PollingService.PollAsync` and placed in a separate `DraftPrs` list in `PollSnapshot`. `MyPrs` contains only non-draft own PRs. `PrItemViewModel.IsDraftSectionPr` flags items belonging to this section; `IsOwnPr` includes `IsDraftSectionPr`.

Dependabot PRs are identified by `Author` being `"dependabot[bot]"` or `"dependabot"` (case-insensitive). They are split out of the "Awaiting My Review" list during polling and placed in their own `DependabotPrs` collection.

### CI status display
Each PR row shows a colored 10×10 `Ellipse`:
- `#3FB950` green — Success
- `#F85149` red — Failure  
- `#D29922` amber — Pending
- `#F0883E` orange — Error
- `#484F58` gray — Unknown

For **My PRs** rows, `PrItemViewModel.EffectiveCIState` is used instead of `CIState` — draft PRs always return `CIState.Unknown` so their indicator is grey regardless of actual build state. When `HasConflicts` is true, `EffectiveCIState` returns `CIState.Failure` regardless of the actual CI state. The tray icon also counts `HasConflicts` PRs as failed CI. The PR tooltip preserves the real CI state (e.g. `CI: Success`) and appends a separate `Merge conflicts` line when `HasConflicts` is true.

### CI checks panel
An overlay inside `MainWindow` (`ChecksOverlay` in [src/MainWindow.xaml](src/MainWindow.xaml), last child of the outer grid with `Grid.RowSpan="3"`) that lists every check on a PR's head commit. It is **not** a separate window: the dimmed backdrop has to cover the PR list, and a second `AllowsTransparency` window would need its own placement, DPI and topmost handling.

- **Opening**: the status `Ellipse` of every PR row is wrapped in a transparent `Border` with `MouseLeftButtonUp="CiStatus_Click"` and negative margins that cancel its padding, so the hit area grows without moving the dot. The handler sets `e.Handled = true`, which is what keeps the row's own `PrItem_Click` (open PR in browser) from firing. The same action is available as **Show CI checks**, the first entry of the native row context menu (`ID_PR_SHOW_CHECKS`).
- **Closing**: `ChecksClose_Click` (✕ above the panel), `ChecksOverlayBackdrop_Click` (click next to the panel), or `Esc` — `Window_KeyDown` closes the overlay first and only hides to the tray when it is already closed. `ChecksPanel_Click` swallows clicks inside the panel so they never reach the backdrop.
- **Data**: `GitHubService.FetchPrChecksAsync(owner, repo, prNumber)` runs `PrChecksQuery` (`statusCheckRollup.contexts`, first 100) and `ParsePrChecks` projects both `CheckRun` and legacy `StatusContext` nodes onto `CheckRunInfo` ([src/Models/CheckRunInfo.cs](src/Models/CheckRunInfo.cs)). A `CheckRun` conclusion only counts once `status == COMPLETED`, so a re-queued job with a stale conclusion is reported as running, not failed. The method never throws: it returns a `CheckFetchResult` ([src/Models/CheckFetchResult.cs](src/Models/CheckFetchResult.cs)) whose `Status` is `Ok`, `Failed` or `RateLimited`.
- **On demand only**: polling does not fetch checks. One extra GraphQL call per PR per poll would cost more than the entire poll, so the panel loads when it opens, on its refresh button, and on its auto-refresh tick.

#### Auto-refresh and rate-limit etiquette
- **One-shot, self-rescheduling**: there is no repeating timer. `AutoRefreshScheduler` ([src/Services/AutoRefreshScheduler.cs](src/Services/AutoRefreshScheduler.cs)) runs a single `Task.Delay(interval)` (30 s, `ChecksViewModel.AutoRefreshInterval`) guarded by a `CancellationTokenSource`, holds at most one pending tick, and the decision is taken again after every load. A timer therefore cannot outlive the panel, stack up, or keep hammering a throttled API. `Cancel()` is idempotent and safe to call from inside a tick (which `RefreshAsync` does); `Dispose()` prevents a pending tick from firing, and a throwing callback is logged rather than surfacing as an unobserved task exception. `ChecksViewModel` has an `internal` constructor overload taking the interval, so tests drive the real cycle in milliseconds.
- **`ShouldAutoRefresh(result, remaining)`** requires all three: `Status == Ok`, at least one check with `IsInProgress` (running or queued), and `remaining ?? int.MaxValue >= MinRateLimitRemaining` (100). A finished PR, a failed call, a rate-limited call or a thin budget each simply end the cycle.
- **Budget-aware pacing**: `ComputeInterval(remaining, resetAt, now)` returns `AutoRefreshInterval` (30 s) whenever the budget is healthy or unknown. When it is thin, the panel spreads the share it may spend (`BudgetShare`, 20% of what is left) evenly across the time remaining in the window — `untilReset / (remaining * 0.2)` — clamped to `[AutoRefreshInterval, MaxAutoRefreshInterval]` (30 s … 5 min). So it degrades gracefully instead of holding a fixed pace until GitHub cuts it off. A slowdown is logged at INFO. The interval is passed per call to `AutoRefreshScheduler.Schedule(delay)`, which is why the scheduler holds no interval of its own.
- **`EffectiveBudget(result)`** prefers what this call reported and falls back to `GitHubService.LastRateLimit` (ignoring a snapshot whose own `resetAt` has passed), so a response that omits the field still paces correctly.
- **Throttling detection**: `LooksRateLimited(text)` matches GitHub's wording (`rate limit exceeded`, `secondary rate limit`, `RATE_LIMITED`, `abuse detection`, `Retry-After`) on both `gh` streams, and `HasRateLimitError(root)` catches a GraphQL `errors[].type == RATE_LIMITED` in an otherwise well-formed 200 response.
- **Cancellation points**: `Close()` (✕, backdrop, `Esc`), `OpenAsync` for another PR, and every `RefreshAsync` call cancel any pending tick first. `MainWindow.HideToTray()` closes the overlay, so no tick survives into the tray where nobody could see the result.
- **A failed reload keeps its rows**: `Finish` only clears the list when the fetch succeeded or when there was nothing on screen yet; otherwise the rows stay and `NoticeMessage` explains the failure below them (`ErrorMessage` remains for the "nothing loaded at all" case). `IsLoading` is likewise only set when the list is empty, so an auto-refresh never blanks a panel the user is reading.
- **Button state** mirrors the header's pin button: `RefreshIcon` swaps the glyph (U+E863 autorenew to U+E5D5 refresh), a `DataTrigger` on `Checks.IsAutoRefreshing` swaps the colour (#58A6FF ↔ #8B949E), `AutoRefreshLabel` puts the current interval next to it, and `RefreshTooltip` states which mode is active. It is an **indicator, not a toggle** — auto-refresh follows whether jobs are running, so clicking always just reloads now.
- **Deliberate limitation**: the cycle keys on jobs that are *currently* unfinished. A PR whose checks have all finished does not poll for checks of a future commit — that is what the refresh button is for.

### Rate-limit budget
Every GraphQL query in `GitHubService` — the three search queries used by polling as well as `PrChecksQuery` — asks for `rateLimit { limit remaining resetAt }`. The field is free (it does not count against the budget) and does not disturb the `data.search.nodes` validation in `RunGraphQlOnceAsync`.

- `RecordRateLimit(root)` runs on every parsed response (both `RunGraphQlOnceAsync` and `FetchPrChecksAsync`) and stores a `RateLimitSnapshot` ([src/Models/RateLimitSnapshot.cs](src/Models/RateLimitSnapshot.cs)) in a `volatile` field exposed as `GitHubService.LastRateLimit`. Polling therefore keeps it current at no cost, and the checks panel reads it without spending a call.
- `RateLimitSnapshot` carries `Remaining`, `Limit`, `ResetAt` and `ObservedAt`, plus `RemainingFraction`, `TimeUntilReset(now)` and `IsStale(now)` (its own reset time has passed, so it describes a previous window).
- A budget below `LowBudgetWarningThreshold` (500) is logged as a WARN **once per reset window** — a thin budget stays thin for a while, and repeating it every poll would bury the rest of the log.
- Budget maths: the GraphQL limit is 5000 points/hour. A poll costs one point per query page, and one checks reload costs one point, so a continuously watched running build adds ~120 points/hour. The panel only ever changes pace when something outside PR Monitor has eaten the budget.
- **`ChecksViewModel`** ([src/ViewModels/ChecksViewModel.cs](src/ViewModels/ChecksViewModel.cs)) is exposed as `MainViewModel.Checks` and is always non-null — a null source would leave the overlay's `Visibility` binding unresolved, which renders as visible. `MainViewModel`'s `GitHubService`/`DiagnosticsLogger` parameters are optional so existing test call sites keep working; `App.xaml.cs` passes the real ones. A `_loadGeneration` counter drops the response of a previously opened PR when the user has already opened another.
- **Rows are collapsed, not listed raw**: `ChecksViewModel.Collapse(checks)` groups by `(WorkflowName, Name, State)`, keeps the newest run of each group (highest `WorkflowRunId`, then latest `StartedAt`) and returns it with a repeat count. GitHub returns one check run per *check suite*, and a workflow triggered by `pull_request_review` gets a fresh suite per review — one real PR returned 31 runs of which 27 were exact repeats (9 × `Claude Code / claude`, etc.), which is why GitHub's own UI collapses them too. State is part of the key on purpose: merging a failed run with its successful rerun would hide the failure. The summary is computed from the collapsed representatives, so the counter matches what is on screen.
- **Skipped rows are filtered out**: `Collapse` returns everything, `VisibleRows(rows, showSkipped)` decides what the list binds to, and `_allRows` keeps the unfiltered set so toggling costs no API call. `ShowSkipped` defaults to false and lives on the `ChecksViewModel` (a single long-lived instance on `MainViewModel`), so it survives closing and reopening the panel but resets on restart — deliberately not persisted in `AppSettings`, since it is a per-session curiosity rather than a preference. `ShowEmptyState` keys on `_allRows`, not on the filtered collection, so a PR whose checks are *all* skipped shows the toggle line instead of "no checks have run".
- **Rows name their workflow**: a bare job name is not identifiable (`Components / Test` and `Main PR / Test` are different jobs), so the row renders `WorkflowPrefix` dimmed (#6E7681) in front of the job name via two `Run`s in one `TextBlock`, matching GitHub's own "Workflow / Job" notation. `CheckRunInfo.Event` (from `workflowRun.event`) is not shown in the row but appears in the tooltip, which also names the workflow and the collapsed run count.
- **Ordering and summary**: rows are sorted by `CheckRunInfo.SortRank` (failure → cancelled → running → queued → success → neutral → skipped), then by workflow, then by name. `ChecksViewModel.BuildSummary` produces the headline (`2 CHECKS FAILED` / `CHECKS RUNNING` / `ALL CHECKS PASSED` / `CHECKS COMPLETED` / `NO CHECKS`), the `CIState` that colors it, and a `done/total` counter that **excludes skipped checks** — they never run, so counting them makes a finished PR look unfinished.
- **Clicking a job** opens `detailsUrl` (check run) or `targetUrl` (status context) via `CheckRow_Click`; rows without a URL keep the default cursor and do nothing.
- **Per-job rerun**: the rerun button and the duration share one grid cell, and a `MultiDataTrigger` (`CanRerun` **and** the row `Border`'s `IsMouseOver` via `RelativeSource AncestorType=Border`) swaps which of the two is visible. Sharing a cell means the button needs no column of its own, so nothing shifts horizontally; its `Height="18"` stays under the row's natural height (set by the 15px status glyph), so revealing it cannot grow the row — and with `SizeToContent="Height"`, the window. `CheckRerun_Click` calls `GitHubService.RerunJobAsync(owner, repo, jobId)` → `POST repos/{o}/{r}/actions/jobs/{id}/rerun`, which restarts that job and its dependents only, unlike `RerunFailedJobsAsync` (whole run) behind the row context menu. `MainWindow._rerunningJobs` holds in-flight job ids so a second click is ignored while the panel still shows the old state. Success notifies and calls `RefreshAfterRerunAsync`; a refusal surfaces GitHub's own stderr in a `DarkMessageBox`.
- **The rerun row survives the transition**: rerunning makes GitHub build a new attempt, during which the job is briefly absent from `statusCheckRollup` or comes back as `SKIPPED` (its previous check run superseded) — which the skipped filter then hides, so the row disappeared the moment the user clicked. `RerunRequestedAsync` therefore records the job in `_pendingReruns` (keyed by workflow + name, since the rerun mints a new check run id) as `CheckItemViewModel.AsQueued()`, and `ApplyPendingReruns` substitutes that optimistic row for whatever the reload reports. `PrunePendingReruns` drops an entry once `ReflectsRerun(state)` holds (queued/running/success/neutral) or the grace period expires, so the panel falls back to GitHub's truth and a rerun that never lands cannot pin a stale row forever. `Close()` and `OpenAsync` clear the set.
- **Rerun grace period**: GitHub does not flip a restarted job to queued immediately, so the reload straight after a rerun would still see only finished checks and end the auto-refresh cycle. `RefreshAfterRerunAsync` sets `_rerunGraceUntil = now + RerunGracePeriod` (2 min) and `ShouldAutoRefresh(result, remaining, withinRerunGrace)` treats that as "something is running". The grace is bounded so a rerun that never materialises cannot poll forever, is dropped as soon as a job really is in progress, and never overrides the `Status == Ok` or rate-limit conditions. `Close()` and `OpenAsync` clear it, so another PR does not inherit it.
- **Column alignment**: the job list `ItemsControl` sets `Grid.IsSharedSizeScope="True"` and the badge and duration columns carry `SharedSizeGroup`s, so every row agrees on their widths and the durations line up regardless of which rows carry a `×N` badge.
- **`CheckRunInfo.JobId`** comes from the CheckRun's own `databaseId`, which for GitHub Actions *is* the job id — the same number as the `/job/<id>` segment of `detailsUrl` (verified against the live API). `CanRerun` requires `IsFailure && JobId > 0 && WorkflowRunId > 0`, so status contexts and third-party check runs are excluded.
- **No spinner**: a running check gets a static `pending` glyph. A `RepeatBehavior="Forever"` animation in this window would redraw the whole layered window for the process lifetime — see the WPF pitfalls section.
- `CheckRunStateToBrushConverter` ([src/Converters/CheckRunStateToBrushConverter.cs](src/Converters/CheckRunStateToBrushConverter.cs), key `CheckStateToBrush`) returns the status-icon brush, or the job-name brush with `ConverterParameter=Name` (dimmed for skipped/neutral). All brushes are `static readonly` and frozen.

### Stacked PRs
- A PR is "stacked" when its `BaseRefName` equals another open PR's `HeadRefName` in the same repository (the gh-stack model). Detection is entirely local — `PollingService.ApplyStackRelations(IReadOnlyList<PullRequestInfo>)` builds a `(repository, headRef) → PR` lookup over every section's PRs and links children to parents. Costs **no extra API calls**; `MyPrsQuery` was extended with `baseRefName` (the review queries already had it).
- `ApplyStackRelations` resets and then fills `StackParentKey`, `StackParentNumber`, `StackParentUrl`, `StackRootKey`, `StackDepth` (0 = bottom PR) and `StackSize` on **every** supplied instance, because the same PR key can appear as separate object instances in different sections. Walking up the parent chain uses a visited set so cyclic base/head combinations cannot loop forever.
- `PullRequestInfo.IsStacked` (`StackSize > 1`) and `IsBlockedByStack` (`StackParentKey` set) are derived properties.
- `PollingService.OrderByStack` regroups a section so stack members are consecutive, ordered by `StackDepth` then `Number`, while preserving the original relative order of unrelated PRs. Applied to all seven section lists when `ShowStackRelations` is enabled (mostly a fallback — the ViewModel pulls stacked PRs into their own section).
- **Separate Stacks section**: when `ShowStackRelations` is enabled, `MainViewModel.UpdateFromSnapshot` routes every `IsStacked` PR out of its regular section into `StackedPrs` (`StackedCount`, `StacksExpanded`, `ToggleStacksExpanded`, persisted as `stacksExpanded`). `MainViewModel.OrderStackSection` (internal, unit-tested) groups rows by `StackRootKey`, orders each group by `StackDepth` then `Number`, keeps the first-seen group order, and sets `IsStackGroupStart` on the first row of every group plus `ShowStackGroupSeparator` on all but the very first group, so a thin divider renders between stacks and every non-first row is indented one level via `StackIndentMargin` (`Thickness(14, 2, 0, 2)`, bound on the row `Border` in the Stacks template only). Gaps are allowed — stack members that aren't in any polled section are simply absent. The section header uses the Material Symbols `stacks` glyph (`\uF500`). The section is hidden when empty and lives between **Awaiting My Review** and **My PRs** in `MainWindow.xaml`.
- `MainViewModel.BuildStackChainTooltip(pr, members)` (internal, unit-tested) renders the full chain, e.g. `Stack (3 PRs):` followed by one ` 1/3  #41 alice — Success` line per member, with `▸` marking the current PR and status resolved as `Draft` / `Conflicts` / the CI state. It is passed to `PrItemViewModel.From(..., stackChainTooltip:, stackParentIsMine:)` and replaces the older single-line stack tooltip.
- `PrItemViewModel` exposes `StackPosition` (`StackDepth + 1`), `ShowStackIndicator`, `StackBadgeText` (` · stack 2/3 · waits on #8632 (you)` — the `waits on` suffix only when a parent exists, `(you)` only when the parent is authored by the current user), `ShowStackGroupSeparator` / `IsStackGroupStart` / `StackIndentMargin` (settable pair assigned by `OrderStackSection`) and `CanOpenStackParent`. Each section template adds the badge `<Run>` to its repository line (no explicit `Foreground`, so it inherits the grey `RepoText` colour). Indentation is per stack group, not per depth level — cumulative depth indentation was removed because a stack starting mid-list made unrelated PRs look nested.
- `EffectiveCIState` order: conflicts → Failure, draft → Unknown, otherwise the real CI state. Stack-blocked PRs deliberately keep their own colour — a green PR waiting on its parent stays green.
- `TrayIconManager` excludes stack-blocked PRs from the purple pending count unless `StackBlockedCountsForTrayIcon` is enabled.
- PR row context menus gain **Open parent PR** and **Open whole stack** for stacked PRs; `MainViewModel.AllPrs` enumerates every visible row so siblings can be resolved by `StackRootKey`.

### Unresolved review comments indicator
- PR rows keep the CI circle unchanged and can show an additional message icon (`Segoe MDL2 Assets`, `E8BD`) when unresolved review comments are present.
- Each PR row has a combined `PrTooltip` (bound to the row `Border`) showing CI state, reviewer info (for own PRs), unresolved comment count, and approved state. Individual icons carry no separate tooltips.
- Data is sourced from GraphQL `reviewThreads` per PR by counting unresolved threads (`isResolved == false`) and summing their `comments.totalCount`.

### Reviewer indicator on own PRs
- Own PR rows (My Auto-Merge PRs, My PRs, Hotfixes, and own PRs in Later) show a `E748` (SwitchUser) icon from **Segoe Fluent Icons** (`FontSize="11"`, amber `#D29922`) when `ShowNoReviewerWarning` is true (i.e., `IsOwnPr && !HasNonCopilotReviewer`).
- No icon is shown when a non-Copilot reviewer has been assigned — reviewer names and their latest review state appear in `PrTooltip` instead.
- `ReviewerLogins` is populated from GraphQL `reviewRequests(first: 10)` in `MyPrsQuery` and `ReviewRequestedQuery`, filtering out logins that start with `"copilot"` (case-insensitive, covers both `copilot` and `copilot-pull-request-reviewer[bot]`). Team slugs are included.
- `TeamReviewerSlugs` (parsed by `GitHubService.ParseTeamReviewerSlugs`) is the subset of `ReviewerLogins` that are teams. `PrItemViewModel.EffectiveReviewerLogins` drops those unless `AppSettings.TeamReviewCountsAsReviewer` (default `false`, **Settings → Sections → Review requests**) is enabled — with CODEOWNERS a team is auto-requested on every PR, so a team request alone must not clear the "no reviewer assigned" warning. `HasNonCopilotReviewer`, `HasChangesRequested`, `IsReviewPending`, `HasCommentedOnly` and `ReviewerTooltip` all use `EffectiveReviewerLogins`; `PrTooltip` falls back to `No individual reviewer assigned (team: …)` when only teams are requested. The Assign-reviewer context menu keeps using the unfiltered `ReviewerLogins`.
- `PrTooltip` (computed property on `PrItemViewModel`) shows: `CI: {state}` + reviewer info (if `IsOwnPr`) + unresolved comments + approved state, joined by newlines.

### Reviewer-state icons on own PRs
- `ReviewState` ([src/Models/ReviewState.cs](src/Models/ReviewState.cs)) mirrors GitHub's `PullRequestReviewState` enum: `Pending`, `Commented`, `Approved`, `ChangesRequested` (`DISMISSED` and draft `PENDING` reviews are excluded upstream by `GitHubService.ParseReviewerStates`).
- `GitHubService.ParseReviewerStates(node)` builds a `Dictionary<string, ReviewState>` per PR from two GraphQL fields: `reviews(last: 20) { nodes { author { login } state submittedAt } }` (latest non-dismissed submitted review per author) and `reviewRequests(first: 10)` (an active pending request always overrides to `Pending`, even over a stale prior review — a fresh re-review request awaiting a new response). Copilot reviewers are filtered out. Populates `PullRequestInfo.ReviewerStates` / `PrItemViewModel.ReviewerStates`.
- `PrItemViewModel.StateOf(login)` looks up a reviewer's state, defaulting to `Pending` when not yet recorded.
- Icon precedence (each `Show*Icon` property is mutually exclusive, evaluated in this order, all require `IsOwnPr`):
  1. `HasUnresolvedReviewComments` — grey comment icon (`E24C`), takes priority over all reviewer-state icons.
  2. `ShowChangesRequestedIcon` — red icon (`E888`, `cancel`) when any reviewer's latest state is `ChangesRequested`.
  3. `ShowNoReviewerWarning` — amber icon (`F567`) when no non-Copilot reviewer is assigned.
  4. `ShowReviewPendingIcon` — grey clock icon (`EFD6`, `schedule`) when reviewer(s) are assigned and all are still `Pending`.
  5. `ShowCommentedIcon` — blue icon (`E0CB`, `chat_bubble`) when any reviewer's latest state is `Commented` and none has requested changes, and the PR isn't `Approved`.
  6. `ShowApprovedIcon` — green checkmark (`F0BE`), shown last when approved and none of the above apply.
- Rendered in `MainWindow.xaml` in the PR row icon `StackPanel`, present in Hotfixes, My Auto-Merge PRs, My PRs, My Draft PRs, and Later sections (Draft PRs section omits the Approved icon, matching prior behavior). Non-own-PR sections (Awaiting My Review, Dependabot, Team Review Requests) don't show these reviewer-state icons since they aren't `IsOwnPr`.
- `PrTooltip`'s reviewer line includes each reviewer's display state, e.g. `Reviewers: alice (Approved), bob (Pending)`, via `ReviewState.ToDisplayString()`.

### Label chips and priority indicator
- All three search queries fetch `labels(first: 20) { nodes { name } }`. `GitHubService.ParseLabels(node)` fills `PullRequestInfo.Labels`, keeping GitHub's order and dropping empty and case-insensitively duplicate names. Labels are not part of the delta detection in `PollingService`, so they never trigger notifications.
- `AppSettings.LabelRules` (`List<LabelRule>`: `Label`, `Text`, `Color`, `IsPriority`) holds the mapping. If the key is missing, the default is one rule: `Prioriteit/High` → `HIGH`, no colour, priority. An explicit `[]` stays empty. `LoadFrom` drops rules without a label. `SettingsViewModel.Save()` trims fields and clears any colour that is not `#RRGGBB`.
- `PrItemViewModel.From(..., labelRules)` calls `MatchLabels`. For each rule, in rule order, whose label is on the PR (OrdinalIgnoreCase), it adds one `LabelChipViewModel`, skipping duplicate chip texts. It also sets `IsPriority` when any matching rule is a priority rule.
  - Chip colour: the rule's colour when valid. Otherwise it is the colour of `EffectiveCIState` from `CIStateToBrushConverter.StateToColor`, with one exception: `Unknown` (e.g. drafts) uses the muted text grey `#8B949E`, because the dot grey is too dark for text.
  - The chip is drawn as text and a 1px border in that colour, on a background of the same colour at 20% alpha. Its brushes are frozen.
- `PrTooltip` ends with `Labels: a, b` listing every label on the PR, mapped or not. `DisplaySignature` includes `IsPriority` and the chips' text and colour, so a label change rebuilds the rows.
- `MainViewModel.PriorityFirst` stable-sorts priority rows to the top of every section, including Later. The Stacks section is left in chain order. Hotfixes, Awaiting My Review and Team Review Requests are sorted before `ApplyInlineStackGrouping`, so a stack anchors where its first member lands.
- XAML:
  - The `PrRow` style reserves a `3,0,0,0` left border on every row, transparent by default, so contents stay aligned. A `DataTrigger` on `IsPriority` colours that border through `CIStateToBrush`.
  - Every row template wraps the repo line in a horizontal `StackPanel` with an `ItemsControl` using the shared `LabelChips` style.
  - The chips are static: there is no animation.


### Assign reviewer submenu
- Own non-draft PR rows (My Auto-Merge PRs, My PRs, Hotfixes, own PRs in Later) show an **Assign reviewer** submenu in their right-click context menus.
- **Currently assigned reviewers** appear at the top with a checkmark; clicking them removes the reviewer via the GraphQL `requestReviewsById` mutation.
- **Up to 10 recently used reviewers** (`RecentReviewers` in settings) are listed below in case-insensitive alphabetical order for one-click assignment; labels prefer cached full names and fall back to handles when no name is known. The list is updated on every successful assign/remove.
- **Search…** opens `AssignReviewerSearchWindow` — a modal search dialog with instant client-side filtering of org members once loaded. It shows a recents panel when the search box is empty, with per-item remove (✕ or Delete key). Keyboard navigation (↑/↓) works between the search box and results.
- The org-member list is fetched via GraphQL (`organization.membersWithRole`) and cached in `OrgMembersCache` / `OrgMembersCachedAt` in settings. The cache is reused across restarts and only re-fetched after 30 days or when the user clicks the ↺ refresh button in the dialog.

### Tray icon colors
- Red `#F85149` — CI failures present
- Amber `#D29922` — reviews pending or unresolved comments on My PRs, no CI failures
- Purple `#8957E5` — pipeline running (Pending CI on visible PRs), no failures or review actions; PRs only waiting on an open stack parent are excluded unless `StackBlockedCountsForTrayIcon` is enabled
- Green `#3FB950` — all clear
- Blue `#005FAA` — only Later-items, nothing active
- Gray `#8B949E` — idle / not polled yet
- Badge text rendering uses anti-aliased glyphs tuned for small transparent tray icons so two-digit counts remain legible.

### Version display
- App version is defined once in `src/PrMonitor.csproj` via `<Version>`.
- Runtime reads the assembly informational/file version (used by `UpdateService` for version comparisons and by `AboutWindow` to display the current version).

### Update checks
- `UpdateService` calls `gh api repos/jvanoostveen/pr-monitor/releases/latest` first (authenticated), with HTTP `GET https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest` as fallback, and parses `tag_name`, `html_url`, and `body` (release notes).
- `UpdateCheckResult` exposes `ReleaseUrl` (compare URL), `ReleaseNotesUrl` (release page), `ReleaseNotes` (markdown body), and `LatestVersionText`.
- `UpdateService.GetRelevantChangelogAsync(currentVersion, latestVersion)` fetches the raw repository `CHANGELOG.md` from GitHub and extracts only the released sections newer than the running version and up to the latest available release.
- Current version is read from assembly metadata; tags like `v1.2.3` are normalized before semantic comparison.
- A `System.Threading.Timer` fires 30 seconds after startup then every 24 hours; it calls `RunAutoUpdateCheckAsync()` which silently calls `MainViewModel.SetUpdateAvailable()` when a newer version is found.
- When an update is available, the PR window footer shows a green banner. The "What's new?" link that opens an in-app changelog dialog for the relevant version range is always visible next to the banner text — before, during, and after the download:
  - **Before download**: "Update available: vX.Y.Z — click to download"
  - **Downloading**: "Downloading update… N%" with a thin green progress bar; cursor changes to `Wait`
  - **Ready**: "vX.Y.Z ready — click to restart"; clicking triggers the in-place swap and restarts. No toast notification is shown when the download finishes — the banner state change is sufficient.
- Manual **Check for updates…** also offers to open that same filtered changelog dialog instead of the GitHub compare/commit view.
- **In-place update flow**: `UpdateService.DownloadUpdateAsync()` downloads the release zip to a **uniquely-named** file (GUID-prefixed) under `%TEMP%\PrMonitor_update\` via `HttpClient` and extracts `PrMonitor.exe`. The unique name avoids collisions with a locked/leftover zip from a previous failed attempt. Stale zips from earlier failed attempts are removed best-effort before each new download, and the extraction step (which can be briefly blocked by antivirus/SmartScreen scanning the freshly-downloaded file) retries automatically with backoff (`RetryOnFileLockedAsync`) instead of failing immediately on a sharing-violation `IOException`. `UpdateService.StartUpdateProcess()` writes a `.bat` launcher script to `%TEMP%` that waits for the current PID to exit, renames the old exe to `.exe.old`, copies the new exe, starts it, and self-deletes. `MainViewModel.RestartToInstallUpdate()` calls `StartUpdateProcess` then `Application.Current.Shutdown()`.
- **Post-update changelog**: `AppSettings.LastRunVersion` records the version the app ran as, rewritten on every startup by `App.ShowChangelogAfterUpdateAsync()`. When the recorded version is strictly older than the running one (`UpdateService.IsUpgrade(from, to)` — false for a first run with no recorded version, an unchanged version, a downgrade, or an unparseable version), that method fetches `GetRelevantChangelogAsync(previousVersion, currentVersion)` and opens `ChangelogWindow` with the title *Updated to vX.Y.Z* and the subtitle *What's new since vA.B.C*. It is fire-and-forget from `OnStartup`, and silent on any failure — a startup dialog must not depend on the changelog being reachable. `AppSettings.ShowChangelogAfterUpdate` (default `true`, **Settings → General → "Show what's new after an update"**) suppresses the dialog; the version is still recorded so enabling it later does not replay old releases.
- `ChangelogWindow.ShowForOwner()` takes optional `titleOverride` and `subtitle` arguments; without them the title comes from `UpdateChangelogResult.Title` and the subtitle reads "Relevant entries from CHANGELOG.md".
- On each startup, `App.CleanupOldExe()` deletes `{exePath}.old` if it exists (leftover from previous update).
- `MainViewModel.SetUpdateAvailable(version, releaseUrl, releaseNotesUrl, releaseNotes)` takes 4 parameters.
- `MainViewModel` accepts `UpdateService` as a constructor parameter (injected in `App.xaml.cs`).
- Manual checks are triggered from the **Check for updates…** button in the About dialog; the manual check still shows a MessageBox for immediate feedback and also updates the banner.
- Update-check failures are logged to diagnostics (`pr-monitor.log`), and manual checks show the concrete error message instead of a generic failure.

### Release automation
- Build validation workflow: `.github/workflows/ci-build.yml`
- Triggers:
  - `pull_request` to `main`
  - `push` to `main`
- Behavior:
  - Restores and builds the full solution (`pr-monitor.slnx`) in `Release` with .NET 10 on `windows-latest`
  - Runs `dotnet test` on `tests/PrMonitor.Tests`
  - Build + test validation only (no tag/release/upload steps)

- Release workflow: `.github/workflows/release-on-version-change.yml`
- Triggers:
  - `push` to `main` when `src/PrMonitor.csproj` changes
  - `workflow_dispatch` (manual)
- Behavior:
  - Reads version from `src/PrMonitor.csproj`
  - For push events, releases only when version changed from previous commit
  - Skips if tag `v<version>` already exists
  - Publishes a single-file `win-x64` executable (including native libraries for self-extract at runtime) and attaches `PrMonitor-<version>-win-x64.zip` (only `PrMonitor.exe`) to GitHub Release

---

## WPF pitfalls

1. **`DragMove()` breaks button hover and click events**: only call it from a dedicated drag element (StackPanel on title), NOT from the whole header border. Buttons must not be children of the drag element.

2. **`DataTrigger` with `TargetName` in a `Style`**: not allowed outside `ControlTemplate`. Drive animations from code-behind instead (see `UpdateRefreshIcon` in `MainWindow.xaml.cs`).

3. **WPF + WinForms type ambiguity**: always use fully-qualified names:
   - `System.Windows.Application` (not `Application`)
   - `System.Windows.MessageBox`
   - `System.Windows.Media.Color`

4. **`ActualHeight` is 0 before first render**: use the `Loaded` event or check `ActualHeight > 0` before positioning.

5. **`JsonElement?` nullable**: use `is not { } jsonValue` pattern, not `.HasValue`.

6. **`RepeatBehavior="Forever"` animations never stop on their own**: hiding or collapsing the animated element does not stop the storyboard. On this window (`AllowsTransparency="True"` → software-rendered layered window) a forever-running animation redraws the entire window every frame for the process's lifetime, costing ~16% of a CPU core and several GB of native memory per hour. Always start such animations from code-behind and stop them with `BeginAnimation(property, null)`.

---

## Naming conventions

- ViewModels: `*ViewModel.cs` in `ViewModels/`
- Services: `*Service.cs` in `Services/`
- Converters: `*Converter.cs` registered in `App.xaml` resources as `CamelCase` keys
- XAML event handlers: `ElementName_EventName` (e.g. `RefreshButton_Click`, `AutoMergeHeader_Click`)

---

## Settings schema (`%APPDATA%/pr-monitor/settings.json`)

```json
{
  "organizations": ["org1", "org2"],
  "pollingIntervalSeconds": 120,
  "autoStartWithWindows": true,
  "compactMode": false,
  "gitHubUsername": "your-username",
  "hotfixExpanded": true,
  "autoMergeExpanded": true,
  "reviewExpanded": true,
  "myPrsExpanded": true,
  "teamReviewExpanded": false,
  "dependabotExpanded": false,
  "draftExpanded": false,
  "stacksExpanded": true,
  "showTeamReviewSection": true,
  "teamReviewCountsForTrayIcon": false,
  "teamReviewCountsForStatistics": false,
  "teamReviewCountsAsReviewer": false,
  "showStackRelations": true,
  "stackBlockedCountsForTrayIcon": false,
  "laterExpanded": false,
  "mainWindowVisible": false,
  "mainWindowLeft": 1440.0,
  "mainWindowTop": 120.0,
  "mainWindowSnappedCorner": null,
  "alwaysOnTop": true,
  "statsWindowLeft": null,
  "statsWindowTop": null,
  "statsWindowWidth": null,
  "statsWindowHeight": null,
  "hiddenPrKeys": [],
  "manuallyHiddenPrKeys": [],
  "snoozedPrs": {},
  "notifyCiFailed": true,
  "notifyCiPassed": true,
  "notifyCiError": true,
  "notifyReviewRequested": true,
  "notifyPrMergedOrClosed": true,
  "notifyFlakinessRerun": true,
  "notifyFlakinessRealFailure": true,
  "notifyStartupSummary": true,
  "notifyMentioned": true,
  "notificationMode": "Always",
  "flakinessAnalysisEnabled": true,
  "flakinessAutoMergeOnly": false,
  "flakinessCustomHints": "",
  "flakinessMaxReruns": 3,
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
  "labelRules": [
    { "label": "Prioriteit/High", "text": "HIGH", "color": "", "isPriority": true }
  ],
  "flakinessRerunCounts": {
    "owner/repo#123": { "count": 1, "lastAttempt": "2026-03-24T00:00:00Z" }
  },
  "recentReviewers": ["alice", "bob"],
  "orgMembersCache": [
    { "login": "alice", "name": "Alice Smith" }
  ],
  "orgMembersCachedAt": "2026-04-01T12:00:00Z",
  "lastRunVersion": "1.14.0",
  "showChangelogAfterUpdate": true,
  "verboseLogging": false
}
```

Serialized as camelCase. `AppSettings.Load()` / `settings.Save()` handle file I/O.
On `Load()`, rerun records older than 30 days are automatically pruned. Expired `snoozedPrs` entries are also pruned on load.
`settings.Save()` keeps a best-effort backup at `%APPDATA%/pr-monitor/settings.json.bak`; when `settings.json` is unreadable, `AppSettings.Load()` falls back to the backup before returning defaults.

