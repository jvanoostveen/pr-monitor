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
| **Team Review Requests** | PRs where a review was requested from a team you belong to (collapsed by default; **Settings → Sections → Hide team review requests** removes the section *and* those PRs — they do not move to Awaiting My Review) |
| **My Draft PRs** | Your own draft PRs (collapsed by default) |
| **Stacks** | Stacked PRs with no review claim on you — your own PRs, auto-merge, drafts, Dependabot — grouped per stack (see below) |
| **Later** | PRs you've snoozed with "Move to later" |

Each PR row keeps its CI status circle and also shows a message icon when unresolved review comments exist; hovering the icon shows the unresolved comment count.

### CI checks panel

Click the colored **status dot** of a PR row — or pick **Show CI checks** from its right-click menu — to open a panel over the list showing every check on that PR's latest commit:

- a status icon per job (failed, running, queued, passed, skipped) in GitHub's own colors;
- the workflow the job belongs to, dimmed in front of the job name (`Components / Test`) — without it two workflows that both have a `Test` job are indistinguishable;
- the job's duration, or its elapsed time while it is still running;
- a summary line such as `CHECKS RUNNING 3/4`, `2 CHECKS FAILED` or `ALL CHECKS PASSED`, counting finished checks over the total (skipped checks excluded).

Hovering a **failed** job replaces its duration with a ↺ button that reruns *that job alone* (and anything depending on it). The panel reloads straight after and keeps auto-refreshing until the restarted job shows up, so you can watch it go from queued to green without touching anything. The row context menu's **Rerun failed jobs** still restarts every failed job on the PR at once — use the per-job button when only one job is flaky.

**Skipped checks are hidden**, since a job that never ran tells you nothing about the PR. A `Show 3 skipped checks` line under the list brings them back if you want them, and that choice sticks until you restart the app.

Identical runs of the same job are merged into a single row with a `×N` badge. GitHub reports a separate check run per check suite, so a workflow that triggers on `pull_request_review` can otherwise fill the panel with nine copies of the same skipped job. Only runs that match on workflow, job *and* status are merged, and the row links to the most recent of them — a job that failed and then passed on a rerun stays visible as two rows.

Jobs are ordered by what needs attention first: failed, then cancelled, running, queued, passed and finally skipped. **Clicking a job opens its log page on GitHub directly**, so a red PR takes one click to the failing job instead of a trip through the PR, its checks tab and the workflow run.

While at least one job is still running or queued, the panel **reloads itself every 30 seconds** so you can watch a build finish without touching anything. That timer only exists while it is useful — it stops as soon as every job has finished, when you close the panel or hide the window to the tray, when a reload fails, or when your GitHub API budget runs low. There is no background polling of checks: closing the panel ends the cycle completely.

The **refresh button shows which mode you are in**, like the pin button in the window header:

| Button | Meaning |
|---|---|
| Blue ↻ with an interval next to it (e.g. `30s`) | Auto-refresh is on — the panel reloads itself at that interval |
| Grey ↻ | Auto-refresh is off — nothing is running, so click to reload |

Clicking it always reloads immediately, whichever mode is shown.

PR Monitor asks GitHub for the remaining API budget on every query (a field that costs nothing), so it knows how much room it has without spending a call to find out. If that budget runs thin — usually because something else is using the same token — the panel automatically slows down instead of racing to the limit, and the button shows the adjusted interval. Below 100 remaining points it stops reloading altogether and says so.

The panel also has an **Open on GitHub** button for the PR itself. Close it with `Esc`, the ✕ button above it, or by clicking next to it.

### Stacked PRs

PRs created as a [stack](https://github.github.com/gh-stack/) — where a PR's base branch is another open PR's head branch — are detected automatically from the polled data (no extra API calls). They are moved out of their regular section into a dedicated **Stacks** section, grouped per stack with the bottom PR first, a thin separator between stacks and every row but each stack's first indented one level, regardless of who authored them.

Being stacked never outranks *why* a PR is listed: **Hotfixes**, **Awaiting My Review** and **Team Review Requests** keep their stacked PRs, so a review that was only requested from one of your teams never ends up looking like a direct request. Inside those sections a stack's members are pulled together to where its bottom PR already sat, bottom PR first, with every row but that first one indented one level (no separator line, since those rows sit between unrelated PRs). Only stacked PRs with no review claim on you — your own PRs, auto-merge PRs, drafts and Dependabot — move to the **Stacks** section.

The repository line shows a `· stack 2/3 · waits on #8632 (you)` badge, and the tooltip lists the whole chain with each member's number, author and status (`▸` marks the PR you're hovering).

A stacked PR keeps its own CI colour, so a PR that is green but still waiting on the PR below it stays green. Right-clicking such a PR offers **Open parent PR** and **Open whole stack**.

The separate section and whether stack-blocked PRs count towards the tray icon can be configured in **Settings → Sections → Stacked PRs**.

### Label chips and priority PRs

GitHub labels can be shown as small chips next to a PR's `repo #number`. You choose which labels appear, and how, in **Settings → Labels**. Each rule maps a label name (case-insensitive) to:

- a **chip text** (empty shows the label name),
- a **colour**: click the colour button to pick one from the palette, choose **Custom colour…** for the Windows colour dialog, or pick **Default (CI status)** to make the chip follow the row's CI status colour (green, amber, red or grey),
- a **Priority**: **None** shows only the chip, **High** adds an accent bar on the row's left edge in its CI status colour and moves the PR to the top of its section, **Low** moves the PR to the bottom of its section. If a PR matches both a High and a Low rule, High wins.

By default `Prioriteit/High` is a High-priority label shown as a `HIGH` chip, and `Prioriteit/Low` is a Low-priority label shown as a muted grey `LOW` chip that pushes less urgent PRs down. Remove or change these rules if your repositories label priority differently. Hover a row to see all of its labels, including unmapped ones.

### Reviewer indicator on your own PRs

Your own PR rows show an amber "no reviewer assigned" icon until someone is requested as reviewer. Because CODEOWNERS requests a *team* on every PR automatically, a team request alone does **not** count as an assigned reviewer by default — the tooltip then reads `No individual reviewer assigned (team: …)`. Enable **Settings → Sections → Review requests → "Team review request counts as an assigned reviewer"** to treat a team request as a real reviewer.

Empty sections are hidden automatically. While the very first poll is in progress, the window shows a subtle spinning-icon "Loading pull requests…" indicator instead of a blank list. If, once loaded, there are truly no PRs anywhere (including snoozed **Later** items), a short, randomly-picked one-liner is shown instead (e.g. "Zero PRs. Look at you go.").

The tray icon badge changes colour to reflect the worst state:

- 🔴 Red — one or more CI failures
- 🟡 Amber — reviews pending or unresolved review comments on your own PRs, no CI failures
- 🟣 Purple — pipeline still running (pending CI), no failures or review actions needed
- 🟢 Green — everything is fine
- 🔵 Blue — only "Later" items, nothing active
- ⚫ Gray — not yet polled

Badge text rendering is tuned for small tray icons so two-digit counts remain readable.

Click the tray icon to toggle the window. Right-click for a context menu with **Open PR Monitor**, **About…**, **Settings…**, **Statistics…**, and totals per section.

Windows toast notifications are shown under the app name **PR Monitor**.

### Statistics

PR Monitor keeps lightweight activity statistics while it runs:

- **Reviews completed** — review requests on you that were resolved
- **PRs opened** — your PRs created while the app is running
- **PRs merged** — your PRs that left every section (merged or closed)
- **CI failures** — failures on your PRs
- **Flaky reruns** — automatic reruns triggered by flakiness analysis
- **Real failures** — CI failures classified as genuine (non-flaky)

Open the **Statistics** window from the chart button in the PR Monitor window header or the **Statistics…** item in the tray context menu. Each metric is shown per period — **Today**, **This week**, **This month**, and **Total**. Counts are persisted to `%APPDATA%/pr-monitor/statistics.json` and survive restarts. Statistics are only counted while the app is running (there is no historical backfill); *PRs merged* and *reviews completed* are heuristics based on a PR disappearing from its section.


The app automatically checks for a new release ~30 seconds after startup and again every 24 hours. When a newer version is available, a green clickable banner appears at the bottom of the PR window showing the target version. Click the banner to download the update, and once it is ready the banner switches to a restart action. The adjacent **What's new?** link opens an in-app changelog view sourced from `CHANGELOG.md`, filtered to the versions between your current build and the latest release. A manual check is also available from **About… → Check for updates…**.

After an update has installed and the app has restarted, that same changelog opens by itself, showing everything released between the version you were running and the one now installed — so you do not have to read it before restarting. It appears only after a real version increase, never on a first install. Turn it off with **Show what's new after an update** on the **General** settings tab.

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

Hidden PRs can be managed from **Settings → Hidden PRs**. Entries now show a readable PR label and an **Open** button to jump to GitHub, and removing an entry makes that PR visible in the main window again.
When upgrading from older builds, existing saved **Later** entries are automatically migrated so they continue to appear in the Later section.
Settings loading is backward-compatible with older notification mode values, so existing flakiness hints and learned rules are preserved across updates.

PR row right-click actions now use a native Win32 popup menu (same rendering path as the tray icon menu), so visuals and dark/light behavior stay aligned with Windows.

The window can be **snapped to any corner** of any monitor by dragging it near a corner — the border turns blue to preview the snap, and the window locks into position on release. Snapped placement uses a compact 6 px edge inset. When a monitor is disconnected the window recovers to the same corner on the primary display.

By default the window **stays on top** of other windows (floating behaviour). Click the pin icon in the window header, or toggle **Settings → General → "Always on top (floating window)"**, to let other windows cover it instead — the change applies immediately, no restart needed. This setting defaults to enabled, so upgrading from an older build keeps the current floating behaviour.

PR Monitor now also remembers whether the window was open, plus its last position. If it was visible when you last used the app, it opens automatically on startup and restores the previous location. If monitor layout changed, the window is moved by the smallest possible amount so it is fully visible.
On first show after startup, the saved position is applied before any fallback corner alignment, so secondary-monitor placement is retained across restarts.
Startup ignores pre-restore size-driven auto-alignment, and snapped windows keep a monitor anchor derived from restored coordinates so early layout passes cannot drift them to another screen.
After a user drag ends, the final location and snapped corner are persisted immediately, so a restart does not depend on a later tray-hide or app-exit save.

## Requirements

- Windows 11 or later
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
- Toggle **Show what's new after an update** — opens the changelog automatically on the first start after an update (on by default)
- Configure **Notifications** — choose when to show toasts: **Always** (default), **Only when window is closed**, or **Never**, with per-event-type toggles for CI failures, review requests, and more
- Map GitHub **Labels** to chips on PR rows, and give labels High or Low priority (default: `Prioriteit/High` → High, `Prioriteit/Low` → Low)
- Configure **Flakiness** options, including limiting AI flakiness analysis to **My Auto-Merge PRs** only and setting **Maximum automatic reruns** (1-10, default 3)

With a fresh settings file, section defaults are: **My PRs** expanded, **Later** collapsed, and **Dependabot** collapsed.
When moving PRs to **Later**, the section keeps your chosen collapsed/expanded state and does not auto-open on the first moved item.

Settings are stored in `%APPDATA%\pr-monitor\settings.json`.
PR Monitor also keeps a best-effort backup at `%APPDATA%\pr-monitor\settings.json.bak` and will recover from it if the main settings file is unreadable.

You can also run a manual update check at any time from **About…** in the tray menu.

The Settings window is sized to keep tab headers on a single row, including the **Hidden PRs** tab.

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

See [ARCHITECTURE.md](ARCHITECTURE.md) for the technical reference: tech stack, folder layout,
subsystem behaviour, WPF constraints, and the full settings schema.

### Troubleshooting

For diagnostics when polling or GitHub API calls intermittently return no data, check the local log file:

- `%APPDATA%\pr-monitor\logs\pr-monitor.log`

The file contains timestamped `INFO`, `WARN`, and `ERROR` entries.

Window restore and restart-placement troubleshooting now also writes structured `MainWindowPlacement` entries to the same log, including saved coordinates, snap corner, chosen monitor, deferred resize branches, and final persisted state.
Those traces are also used to diagnose drag-time snap issues; while the window is being dragged, deferred resize auto-positioning is now suppressed so an old snapped corner cannot pull it back across the screen.

Update-check failures are logged there as well (including HTTP status or exception details), and the manual **Check for updates…** action in **About…** shows the concrete error reason.

#### Memory usage

PR Monitor is designed to stay small while running for days in the tray. The garbage collector is configured to release memory back to the OS, and an aggressive memory trim runs whenever the window is hidden. Memory counters (managed heap, working set, handles, GDI objects) are written to the log after every poll, escalating to `WARN` above 500 MB working set or 2000 GDI objects.

If the app still grows unexpectedly, use **About… → Copy diagnostics** to put the current counters and uptime on your clipboard and include them in a bug report.

Shutdown reliability note: app exit now guards single-instance mutex release, so a tray-menu exit/right-click shutdown path will not crash if the current thread does not own the mutex.

For flakiness analysis, PR Monitor uses `gpt-4o-mini` through GitHub Models.

If Windows SmartScreen shows "Windows protected your PC" for `PrMonitor.exe`:

1. Right-click `PrMonitor.exe` and choose **Properties**.
2. In the **General** tab, check **Unblock** (under Security) and click **Apply**.
3. Start the app again.

You may still need to click **More info** → **Run anyway** the first time because the app is unsigned (`Unknown publisher`).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
