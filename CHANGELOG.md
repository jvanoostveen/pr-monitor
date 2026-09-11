# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **CI checks panel — see which jobs run, passed or failed without leaving the app.** Clicking the colored status dot of a PR row (or **Show CI checks** in its right-click menu) opens a panel over the list with every check on the PR's latest commit: a status icon per job, its duration or elapsed time, and a summary line such as `CHECKS RUNNING 3/4` or `2 CHECKS FAILED`. Failed jobs are listed first, then running, queued, succeeded and finally skipped ones. **Clicking a job opens its log page on GitHub directly**, instead of clicking through the PR, its checks tab and the workflow run. The panel also has a refresh button and an **Open on GitHub** button for the PR itself; `Esc`, the ✕ button, or a click next to the panel closes it. Checks are fetched on demand — polling is unchanged and makes no extra API calls.
- **The checks panel refreshes itself every 30 seconds while jobs are still running**, so a running build updates in place instead of needing a manual reload. The timer exists only while it is warranted: it is a single scheduled reload that re-decides after each load, and it is not rescheduled once every job has finished, when the panel is closed (including hiding to the tray), when a reload fails, or when GitHub's remaining GraphQL budget drops below 100 points — the panel then says so instead of retrying. A failed automatic reload keeps the list that is already on screen and shows the reason underneath it.

### Fixed
- **The Stacks section no longer outranks why a PR is listed.** Every stacked PR was pulled out of its own section, so a PR whose review was only requested from one of your teams showed up in **Stacks** as if you had to review it, instead of staying in **Team Review Requests**. **Hotfixes**, **Awaiting My Review** and **Team Review Requests** now keep their stacked PRs and group them in place: a stack's members are pulled together to where its bottom PR already sat, bottom PR first, with every row but that first one indented one level. Only stacked PRs with no review claim on you (your own PRs, auto-merge, drafts, Dependabot) still move to **Stacks**.

## [1.13.3] - 2026-09-10

### Fixed
- **PRs parked in Later (or hidden) no longer produce toast notifications.** Snoozed and manually hidden PRs are invisible in the window but were still generating "CI failed", "review requested" and "PR merged/closed" toasts, and could still trigger flakiness analysis. Both now skip hidden PRs entirely.
- **The PR list no longer flips between full and empty.** GitHub's search API intermittently answers a perfectly valid request with zero hits (`gh` exits 0, the JSON is well-formed, the result set is simply empty), which emptied out a whole section for one poll and then filled it again — producing a burst of "merged/closed" toasts followed by "new PR" toasts two minutes later. Any section that drops from at least one PR to zero in a single poll is now withheld until a second poll confirms it; the previous list stays on screen and no notifications or statistics are recorded in the meantime. This replaces the narrower guard that only covered a poll where *every* section was empty at once.

## [1.13.2] - 2026-09-09

### Added
- **Settings → Sections → "Team review request counts as an assigned reviewer"** (off by default). With CODEOWNERS a team is requested as reviewer automatically on every PR, which previously made own PRs look like they already had a reviewer. Team requests now no longer clear the "no reviewer assigned" warning — only an individual reviewer does. The PR tooltip shows `No individual reviewer assigned (team: …)` in that case. Enable the setting to restore the old behaviour.

### Changed
- The **"What's new?"** changelog link in the update banner is now visible before, during, and after downloading an update, instead of only before and after. It was previously hidden while the download progress bar was showing.

### Fixed
- Hotfix PRs (assigned to you but not authored by you — e.g. cherry-picked by a tool) no longer show up as a duplicate row in "Awaiting My Review". They now appear exclusively in the Hotfixes section, matching the existing behaviour for My Auto-Merge PRs/My PRs.
- **"Rerun failed jobs" incorrectly reported "No failed workflow runs were found"** for PRs whose CI indicator showed red. The action only matched workflow runs with conclusion `failure`, while GitHub's aggregated CI status (which drives the red indicator) also turns failure-red for runs that ended as `cancelled`, `timed_out`, `action_required`, or `startup_failure`. Those conclusions are now included when looking up runs to rerun.
- The confusing "Update ready — click the banner to restart" toast notification no longer appears after an update finishes downloading. The banner itself already switches to the restart prompt, which is sufficient.

## [1.13.1] - 2026-09-08

### Added
- **Settings → Statistics** now includes a "Count team review requests in statistics" toggle (off by default). Team review requests no longer inflate the "Reviews requested" / "Reviews completed" statistics unless explicitly enabled.

### Fixed
- **PR Monitor no longer shows "no PRs" when a GitHub call actually failed.** Previously, a GraphQL page that returned empty output, unparseable JSON, or errors without data was silently treated as "zero PRs", so the list emptied out, "PR merged/closed" notifications fired, statistics counted phantom merges, and every PR reappeared as new once the API recovered. Failed GraphQL calls are now retried up to three times and otherwise abort the whole poll, leaving the previous PR list intact and showing the offline indicator. In addition, a poll that returns zero PRs while the previous poll had PRs is withheld until a second poll confirms it, and the playful empty-state overlay is no longer shown while offline.
- The refresh spinner no longer keeps spinning when a manual refresh fails.
- Network failures while contacting the flakiness-analysis model API are now logged as a warning instead of a full error stack trace.
- The in-app "What's new?" changelog dialog no longer fails silently and fall back to opening a browser: `ChangelogWindow`'s `TextBox` binding required an explicit `Mode=OneWay` because its bound property is read-only (WPF's `TextBox.Text` binds two-way by default). The dialog's header now spans the full window width above the content instead of squeezing it into a narrow left column.
- **"What's new?" and manual update checks now link to the relevant `CHANGELOG.md` section on GitHub** instead of the release page (whose own "Full changelog" link pointed to a raw commit-compare view).

### Changed
- The in-app changelog dialog now renders markdown (headings, bullets, **bold**, and `code` spans) instead of showing the raw markdown text, and uses the app's dark scrollbar style.
- `DiagnosticsLogger.SummarizeException` now includes the full `InnerException` chain (up to 3 levels), making logged exceptions actionable instead of only showing the outer wrapper.

## [1.13.0] - 2026-08-19

### Added
- **Stacked PR support (gh-stack style)**: a PR whose base branch is another open PR's head branch is now recognised as part of a stack. All stacked PRs — regardless of author — are moved out of their regular sections into a dedicated collapsible **Stacks** section, grouped per stack with the bottom PR first, a thin separator between stacks and every row but each stack's first indented one level. The repository line shows a `· stack 2/3 · waits on #8632 (you)` badge, and the tooltip lists the whole chain with each member's number, author and status (the current PR is marked with `▸`). A stacked PR keeps its own CI colour, so a healthy PR waiting on the one below it still shows green.
- **Stack actions in the PR context menu**: **Open parent PR** and **Open whole stack** for PRs that are part of a stack.
- **Settings → Sections → Stacked PRs**: toggle the separate Stacks section and choose whether stack-blocked PRs count towards the tray icon status.
- **Copy diagnostics** button in the About dialog: copies the current managed-heap, working-set, handle, thread and GDI/USER object counts to the clipboard, so memory reports can include concrete numbers.
- Memory counters are now written to the diagnostics log after every poll, escalating to a warning when the working set exceeds 500 MB or GDI objects exceed 2000. The entry also includes a virtual-memory breakdown (private/mapped/image committed bytes, region count and largest single private allocation) to distinguish a managed-heap problem from a native one.

### Changed
- Own PRs now also fetch `baseRefName`, which is required to derive stack relations. Stack detection is done locally from the already-fetched PR data and costs no extra GitHub API calls.
- **Substantially reduced memory usage.** The app now targets a small, promptly-released heap instead of allocation throughput:
  - The GC is configured for a tray app (non-concurrent workstation GC, `System.GC.Conserve`), and an LOH-compacting collection followed by a working-set trim runs when the window is hidden and periodically while it stays hidden.
  - The PR list is no longer torn down and rebuilt on every poll. Rows are built off to the side and only applied to the UI when something visibly changed, avoiding a full visual-tree regeneration of all sections every two minutes.
  - CI logs for flakiness analysis are streamed and sanitized line by line with a bounded tail buffer, instead of loading the entire (potentially hundreds of megabytes) log into memory to keep the last few thousand characters.
  - Log-sanitization regexes are compiled once instead of on every analysis, so repeated CI failures no longer grow the process's code heaps.
  - The merge-conflict cache is pruned to the PRs still present in the latest poll.
  - Buffered notifications are capped and always drained, even when showing a toast fails.
  - The periodic memory trim now also runs while the window is open once private bytes exceed 400 MB, instead of waiting for the window to be hidden.

### Fixed
- **The app no longer consumes several gigabytes of memory after running for a while.** The startup loading spinner's animation was started once and never stopped, so it kept running invisibly for the entire lifetime of the process. Because the PR window is a transparent (software-rendered) window, that forced WPF to redraw it continuously, burning roughly 16% of a CPU core while idle and leaking about 3 GB of native memory per hour. The spinner now only animates while the loading overlay is actually on screen; an idle instance sits at 0% CPU with a stable working set.
- Tray icon updates leaked a GDI icon handle on every poll (roughly 700 per day), which slowly degraded rendering and could eventually exhaust the process's GDI object quota.
- A hung or stalled `gh` command could keep its output buffers and child processes alive indefinitely; `gh` invocations now time out after two minutes and the process tree is killed.

## [1.12.0] - 2026-07-22

### Added
- **Configurable always-on-top window**: the PR Monitor window's floating (always-on-top) behaviour can now be toggled off, letting other windows cover it. Available from **Settings → General → "Always on top (floating window)"** and a new pin icon in the window header; applies immediately, no restart required. Defaults to enabled, so existing installations keep the current floating behaviour after upgrading. With Always-on-top enabled, clicking the tray icon toggles the window open/closed as before. With it disabled, the window can be covered by other windows, so the tray icon always brings it to the foreground instead (use the window's close button to hide it in that mode).
- **Header buttons now hidden until hover**: the pin, statistics, refresh, and close buttons in the window header fade in only while the mouse is over the PR Monitor window, and fade out otherwise, reducing visual clutter when the window is idle.
- **Subtle startup loading indicator and playful empty state**: the PR list area now shows a small, slowly-rotating refresh icon with a dimmed "Loading pull requests…" caption while waiting for the first poll to complete. Once loaded, if there are truly no PRs anywhere (including snoozed/Later items), a short, randomly-picked one-liner ("Zero PRs. Look at you go.", "Inbox zero. Achievement unlocked.", etc.) is shown instead of a blank window.

## [1.11.0] - 2026-07-02

### Added
- **Reviewer-state icons on own PR rows**: in addition to the existing "no reviewer assigned" warning and approved checkmark, own PR rows (Hotfixes, My Auto-Merge PRs, My PRs, My Draft PRs, Later) now show a red icon when a reviewer requested changes, a grey clock icon when reviewer(s) are assigned but none has responded yet, and a blue icon when a reviewer commented without approving or requesting changes. The PR tooltip's reviewer line now shows each reviewer's latest state (e.g. `alice (Approved), bob (Pending)`) instead of just names.
- Statistics window: reviewers inactive this week are hidden by default in the "Reviews requested" breakdown; a toggle link shows/hides them.
- Statistics window: right-click a reviewer to permanently hide them from the breakdown.
- Settings → Statistics: list of hidden reviewers with option to remove them.

### Fixed
- **Auto-update download failing with "the process cannot access the file"**: each update download now writes the release zip to a uniquely-named temp file instead of a fixed name, so a locked/leftover zip from a previous failed attempt can no longer block a new download. Extraction (which can be briefly blocked by antivirus/SmartScreen scanning a freshly-downloaded file) now retries automatically with backoff instead of failing immediately, and stale zip files from earlier failed attempts are cleaned up best-effort before each new download.

## [1.10.0] - 2026-06-29

### Added
- **Statistics tracking and visualization.** PR Monitor now counts activity while it runs — reviews requested, reviews completed, your PRs opened and merged, CI failures on your PRs, flaky reruns triggered, and real (non-flaky) failures classified. Counts are persisted as daily buckets in `%APPDATA%/pr-monitor/statistics.json` and shown per period (Today / This week / This month / Total) in a new **Statistics** window, opened from the system-tray context menu (**Statistics…**) or the chart button in the PR Monitor window header. Statistics are only counted while the app is running (no historical backfill); "PRs merged" and "reviews completed" are heuristics based on a PR disappearing from its section.
- The **Statistics** window now remembers its last position and size between openings, and is clamped back onto a visible monitor work area if its previous screen is no longer connected.
- **Reviews requested** includes a per-author breakdown: clicking the metric label expands an inline sub-table showing each PR author's request counts per period, sorted by total descending.
- **Settings → Statistics tab**: a **Reset statistics…** button clears all collected data after confirmation.
- The **Statistics** window's maximize button fits the window to show all rows without scrolling instead of going full-screen.

### Fixed
- When an auto-update download fails, the update banner now changes to an orange "Download failed — click to open release page" state. Clicking opens the GitHub release page so the user can download and install manually. The failure toast notification also now tells the user to click the banner.
- Converting a PR to Draft now works correctly on all versions of the `gh` CLI by using the GraphQL `ConvertPullRequestToDraft` mutation instead of the non-existent `--draft` flag.
- The **Statistics** window now uses the same minimal overlay scrollbar as the main PR Monitor window instead of the default Windows scrollbar.

## [1.9.1] - 2026-05-21

### Fixed
- PR titles containing Unicode characters (e.g., em-dashes "—") are now correctly displayed instead of appearing as mojibake (e.g., "â€•"). Fixed by explicitly setting UTF-8 encoding when reading output from the `gh` CLI.

## [1.9.0] - 2026-05-11

### Changed
- All native `MessageBox` dialogs replaced with a custom dark-themed `DarkMessageBox` that matches the app's dark UI, including a dark title bar via `DwmSetWindowAttribute`.
- **Dependabot section** moved below **My PRs** in the window order.
- Recent reviewer lists in reviewer assignment UI are now shown in case-insensitive alphabetical order (context menu and search dialog recents panel).
- Recent reviewer labels now hide handles when a full name is known, and fall back to handles when no full name is available.
- The **Move to later** snooze option formerly labeled **1 week** is now **Next week (Monday 09:00)** and always wakes PRs on the next calendar week's Monday morning at 09:00 local time.
- Added a separate **Hide** action for PR rows that hides PRs completely from the main window without creating a new window section, plus a new **Settings → Hidden PRs** tab to remove hidden entries and show PRs again.
- The Settings window is now wider to keep tab headers on a single row instead of wrapping.
- Fresh settings defaults now start with **My PRs** expanded and both **Later** and **Dependabot** collapsed.
- Hidden PR entries in **Settings → Hidden PRs** now show a readable PR label and include an **Open** action to jump directly to the PR on GitHub.

### Fixed
- Toast notifications now properly display PR titles containing Unicode ellipsis characters (U+2026, …) by replacing them with ASCII "..." for compatibility with the Windows notification system.
- Removed automatic stale-key eviction entirely. Hidden and snoozed PRs now persist until explicitly removed via restore or Settings → Hidden PRs. The `hiddenPrLastSeen` field is no longer stored in settings.json.
- Hotfix classification now only includes open `release/*` PRs that are yours or explicitly assigned to you. PRs where you were merely involved (for example by reviewing/commenting) no longer appear in the Hotfixes section.
- Tray icon badge text rendering is now tuned for small transparent icons so two-digit counts (for example `10`) remain legible instead of appearing as a single digit.
- Existing saved Later entries from older settings formats are now migrated automatically to the new hidden/snooze structure, so previously snoozed PRs continue to appear in the Later section after upgrade.
- Settings migration now tolerates legacy/unknown `notificationMode` values so updates no longer reset unrelated settings; existing flakiness custom hints and learned rules are preserved.
- Settings persistence now keeps a `settings.json.bak` backup and automatically loads from it when `settings.json` is unreadable, preventing settings resets after file corruption/write glitches.
- Moving the first PR to **Later** no longer auto-opens the Later section; the section now stays in the user's chosen collapsed/expanded state.
- Test runs no longer write to the real `%APPDATA%/pr-monitor/settings.json`; settings save tests now use an isolated temporary settings path to prevent local settings from being overwritten during development.

## [1.8.6] - 2026-04-08

### Added
- **Enable auto-merge** context menu option on own non-draft PR rows (My PRs, My Auto-Merge PRs, Hotfixes, own PRs in Later). Enabled only when auto-merge is not already active; grayed out otherwise. Uses `gh pr merge --auto` under the hood and refreshes the PR list on success.
- **Auto-merge method** setting in Settings → General: choose between Merge commit (default), Squash and merge, or Rebase and merge. This controls the strategy used by the Enable auto-merge action.

### Changed
- **Icon sizes**: increased icon font sizes to compensate for Material Symbols' larger built-in padding — section header icons `12→16`, chevrons `10→13`, PR row icons `11→14`, header buttons `12→16`, update banner icon `11→14`. Row heights are unchanged.

### Fixed

- PRs that appear via multiple channels (e.g., directly review-requested AND via a team review request, or assigned as assignee AND team review requested) no longer show up in two separate sections. The "Awaiting My Review" section (direct request or assignee) now takes priority: any PR already present there is excluded from "Team Review Requests".

## [1.8.5] - 2026-04-03

### Added
- **Assign reviewer** submenu on own-PR context menu rows (My Auto-Merge PRs, My PRs, Hotfixes, own PRs in Later). Only shown for non-draft PRs.
  - Currently assigned reviewers appear at the top with a checkmark; clicking them removes the reviewer.
  - Up to 10 recently used reviewers are listed below for one-click assignment; persisted in `settings.json` (`recentReviewers`).
  - **Search…** opens a dialog that searches all org members by login or display name, with instant client-side filtering once the member list is loaded.
  - The org member list is fetched via GraphQL (`organization.membersWithRole`) and persisted to `settings.json` (`orgMembersCache`, `orgMembersCachedAt`). It is reused across restarts and only re-fetched after 30 days or when the user clicks the ↺ refresh button in the dialog.
  - The search dialog shows a recents panel when the search box is empty (with ✕ per-item remove and Delete-key support), supports keyboard navigation (↑/↓ between search box and results), and matches the app's dark theme including a custom scrollbar style.

### Fixed

- PRs with merge conflicts no longer flicker between green and red: the CI indicator and tray icon now correctly show red (Failure) whenever `mergeable == CONFLICTING`, independent of the actual CI result. The tooltip now shows the real CI state (e.g. "CI: Success") plus a separate "Merge conflicts" line, instead of the misleading "CI: Failure" that was shown before. All sections (Awaiting My Review, Auto-Merge, Dependabot, etc.) use the same indicator logic.

## [1.8.4] - 2026-04-02

### Fixed

- Auto-update banner text now reliably refreshes when a new version is detected, so the version number is shown consistently and the post-download restart state is no longer hidden behind stale footer text.
- Manual **Check for updates…** and the footer **What's new?** link now show a filtered view of `CHANGELOG.md` for the relevant version range instead of opening the GitHub compare page with raw commit history.

## [1.8.3] - 2026-04-02

### Fixed

- PRs authored by the current user that target a release branch (hotfix PRs) no longer appear in both the Hotfixes section and the My Auto-Merge PRs section simultaneously; they are now shown exclusively in Hotfixes.
- Unit tests no longer pollute the production log (`%APPDATA%\pr-monitor\logs\pr-monitor.log`); `DiagnosticsLogger.Null` is a new static no-op instance used by all test classes to discard log output.
- When flakiness analysis is blocked by the Azure OpenAI content filter (jailbreak detection triggered by CI log content), the analysis is now retried once with an error-lines-only excerpt (lines containing `error|fail|exception|assert|timeout` etc.) to avoid the problematic test-data content. If the retry also fails, the failure is silently skipped.

### Added

- **Dependabot section**: PRs authored by `dependabot[bot]` that are awaiting your review now appear in a dedicated collapsible "Dependabot" section, displayed below "Awaiting My Review". The section is collapsed/expanded independently and its state is persisted in settings (`dependabotExpanded`).
- **Verbose logging setting**: added a `verboseLogging` option (Settings → General → "Verbose logging") that gate-keeps all `INFO`-level log entries. Errors and warnings are always logged regardless of this setting.

## [1.8.2] - 2026-03-31

### Changed

- Reduced verbose window placement/snapping debug log entries. Only meaningful state-transition events (startup, snap applied/cleared, display change, position corrections) are now logged.

## [1.8.1] - 2026-03-30

### Added

- **In-place auto-update**: clicking the update banner now downloads the new release zip directly (no browser, no "unblock" needed), extracts the exe, and prepares it for install. A thin green progress bar appears in the footer during download.
- **"What's new?" changelog link**: next to the update banner a "What's new?" link opens the GitHub release page for the new version in the browser.
- After download completes, a toast notification confirms the update is ready and the banner changes to "click to restart". Clicking it performs the swap via a background launcher script and restarts the app automatically.
- On each startup the leftover `.exe.old` backup from a previous update is deleted if present.

### Fixed

- Team review PRs no longer count toward the tray icon badge total when "Team review counts for tray icon" is disabled.
- Disabling auto-merge on an open PR no longer triggers a "PR Merged / Closed" notification. The removed-from-auto-merge notification is now suppressed when the PR is still open (i.e. it just moved to My PRs).
- Reviewer indicator now correctly shows as assigned even after the reviewer has submitted their review. Previously, reviewers were only tracked via `reviewRequests` (pending requests), which GitHub removes once a review is submitted. The fix also reads `latestOpinionatedReviews` so reviewers who have already approved or requested changes are still counted.
- CI failure and CI error notifications are no longer shown for draft PRs.
- Flakiness analysis: CI log is now sanitized before being sent to the GitHub Models API to prevent Azure OpenAI's jailbreak content filter from blocking the request. When the filter still triggers, the result is treated as indeterminate (no toast, no rerun) rather than a false "real failure" notification.
- "Request Copilot review" now uses the REST API (`gh api … --method POST`) instead of `gh pr edit --add-reviewer copilot`, which failed with a GraphQL error because the Copilot reviewer is a GitHub App bot rather than a regular user.
- Flakiness analysis: CI log sanitization now strips GitHub Actions ISO timestamps and ANSI escape codes from each line before sending it to the AI, recovering significant token budget and reducing content-filter exposure. Truncation now keeps the tail of the log (where actual test errors appear) instead of the head (where security-scanner output and setup steps live). XSS payloads, long base64 blobs, and HTML event-handler injection strings are also redacted. The number of redacted lines is logged.
- Flakiness analysis: API errors, network failures, and JSON parse failures in `CopilotService` are now treated as indeterminate (no toast, no rerun consumed) rather than incorrectly firing a "real failure" notification.

### Changed

- Update notification link now opens a GitHub compare view (`v{current}...v{latest}`) instead of just the latest release page, so you immediately see what changed since your running version.

## [1.8.0] - 2026-03-30

### Added

- Mentions notification: toast when directly @mentioned (not via team) in a PR after app startup, scoped to configured organizations. Notifications are clickable and open the PR in the browser. Seen mentions are marked as read on GitHub so they never re-fire. Toggle in Settings → Notifications.
- Startup summary notification after first poll (shows count of pending PRs per section); toggle in Settings → Notifications.
- "Mark as ready" / "Convert to draft" context menu actions for own PRs.
- Offline indicator in window footer when polling fails; clears automatically on next successful poll.
- **Compact mode**: new toggle in Settings → General tab. When enabled, PR rows use reduced vertical padding (3 px instead of 6 px) and slightly smaller fonts, fitting more PRs on screen. The setting is persisted in `compactMode` and applied immediately when Settings is saved.
- **Team review tray icon opt-in**: new checkbox in Settings → Sections (under the team section toggle): "Count team review requests in tray icon status". Default off — team PRs no longer affect the tray icon status unless explicitly enabled.

### Fixed

- Corner snapping now works when dragging the window directly from one monitor to a corner on another. Two root causes fixed: `DetectNearCorner` now uses overlap-based screen selection instead of `Screen.FromHandle` (which lags during cross-screen `DragMove`), and `_snapAnchorScreen` is cleared before `ApplyCornerSnap` on drag-end so the correct target screen is used. Pure snap calculations extracted to `SnapHelper` with 47 new unit tests to prevent regression.
- Toggling compact mode in Settings now re-snaps the window to its corner immediately, accounting for the height change.

### Changed

- Flakiness analysis is now **enabled by default** (`flakinessAnalysisEnabled: true`). Existing installations with the setting explicitly set to `false` are unaffected.
- GraphQL queries now paginate up to 5 pages (250 PRs per section) instead of stopping at 50.
- "Move to later" replaced with snooze submenu (1h / 4h / tomorrow 09:00 / 1 week / indefinitely); Later rows show expiry time; PRs auto-restore when snooze expires.
- PR rows now show time since last update instead of creation time; tooltip shows PR creation date.

## [1.7.1] - 2026-03-28

### Security

- **Multiple hardening fixes**: all `Process.Start(UseShellExecute=true)` calls now require `https://` scheme; subprocess calls migrate to `ProcessStartInfo.ArgumentList` with slug/SHA pattern validation (removing the `EscapeForShell` workaround); untrusted GitHub data in the AI prompt is wrapped in explicit boundary markers; AI-suggested flakiness regex patterns are validated before persisting; `FlakinessCustomHints` is capped at 500 chars; `CopilotService` gains a 30 s timeout and 1 MB response cap; `AppSettings` saves atomically via a temp-file rename.

### Performance

- **Polling robustness**: `PollingService.PollAsync` is guarded by a `SemaphoreSlim(1,1)` so a manual refresh concurrent with a timer poll is skipped instead of running in parallel and emitting duplicate events; the `Timer.Elapsed` handler is changed from `async void` to fire-and-forget to prevent a threadpool unhandled exception from crashing the process; stdout and stderr in `GitHubService` are now drained in parallel to prevent a pipe-buffer deadlock on verbose `gh` error output.
- **Resource cleanup**: `HttpResponseMessage` in `CopilotService` is disposed after reading the body; both static `HttpClient` instances use `SocketsHttpHandler.PooledConnectionLifetime = 15 min` to avoid stale DNS in long-running sessions; refresh-icon brushes are frozen static fields instead of being re-allocated via `ColorConverter` on every poll cycle.

### Fixed

- Tooltips now use the app's dark color palette instead of the system light theme.

## [1.7.0] - 2026-03-27

### Added

- **Reviewer indicator** on own PR rows (My Auto-Merge PRs, My PRs, Hotfixes, and own PRs in Later): a `SwitchUser` icon (Segoe Fluent Icons `E748`, gray) shows when no non-Copilot reviewer has been assigned. No icon is shown when reviewer(s) are assigned — names appear in the row tooltip instead.
- **Combined row tooltip** on all PR rows: hovering a PR row shows CI state, reviewer info (own PRs only), unresolved comment count, and approved state in a single tooltip. Individual icons (CI dot, comment badge, approved checkmark) no longer carry separate tooltips.
- Unit test project (`tests/PrMonitor.Tests`) with 149 xUnit tests covering converters, service parsing logic, ViewModel properties, polling delta detection, and settings serialization.
- Settings → Flakiness tab now includes a **Custom AI hints** free-text field. Text entered here is injected into the gpt-4o-mini system prompt, allowing teams to describe project-specific CI characteristics (e.g. which test suites are inherently flaky) without hardcoding knowledge into the app.
- The AI flakiness analysis system prompt now treats E2E and browser-based tests (Playwright, Cypress, Selenium) as flaky by default, unless the log contains a clear deterministic assertion failure.
- Settings → Notifications tab now includes a **When to show** section with three options: **Always** (default), **Only when window is closed**, and **Never**. Per-type notification checkboxes are unchanged but are grayed out when "Never" is selected.

### Fixed

- CI runner infrastructure errors (symbolic link creation failures, disk full, git checkout/clone issues, OOM kills) are now always treated as flaky. A set of built-in regex rules in `FlakinessService` short-circuits the AI entirely for these patterns, preventing false "real failure" classifications. The AI system prompt is also expanded to classify such errors as flaky as a backstop for patterns not caught by the built-in rules.

## [1.6.1] - 2026-03-25

### Added

- PR row context menus now include **Request Copilot review** to (re)request a Copilot review via `gh pr edit --add-reviewer copilot` (enabled for non-draft PRs).
- Settings → Notifications tab now includes a **Flakiness** group with toggles for **Flaky CI — auto-rerun triggered** and **Real failure detected (not flaky)**. These two flakiness toasts were previously always shown; they can now be individually disabled.

### Changed

- PR row right-click menus now use a native Win32 popup menu (matching the tray icon menu rendering path), consolidating visual behavior with Windows and eliminating WPF context-menu theming and container issues.
- Added structured `MainWindowPlacement` diagnostics logging for startup restore, snap application, deferred `SizeChanged` branches, filtered `LocationChanged` events, display-change recovery, and persisted placement state so window-position issues can be reconstructed from `%APPDATA%/pr-monitor/logs/pr-monitor.log`.

### Fixed

- Fixed restart placement so restored windows keep their saved monitor/corner across startup, including early layout passes that previously could drift a restored secondary-monitor window back to the wrong screen.
- Fixed drag-time placement behavior by skipping deferred `SizeChanged` auto-positioning while the user is dragging and by persisting the final dragged location/corner immediately when the drag ends.

## [1.6.0] - 2026-03-24

### Added

- **Flakiness detection and auto-rerun feature**: CI failures on your own non-draft PRs can be analyzed via GitHub Models (`gpt-4o-mini`) using `gh auth token`, with local `.NET` regex rules checked first, suggested rules persisted for reuse, configurable scope to only auto-merge PRs, configurable maximum automatic reruns (1-10, default 3), dynamic retry counters in logs/toasts, and ad-hoc notifications via `NotificationService.Notify(title, body)`. Supporting plumbing includes `PullRequestInfo.HeadCommitSha` (from GraphQL `oid`) and `DetectMyPrsChanges` so both auto-merge and regular My PRs can trigger the flow.
- **Flakiness rules** are now managed in a dedicated resizable, scrollable **Manage rules…** window instead of an inline list inside the Settings dialog. The Flakiness tab shows a rule count and a button to open the window.
- **PR context menu actions**: failed PR rows now include **Rerun failed jobs** (reruns failed workflow runs resolved from head commit SHA), and PR rows also include **Copy PR URL** above **Copy branch name** for quick link copying.

## [1.5.3] - 2026-03-24

### Fixed

- PRs authored by the current user targeting a `release/*` branch now appear **only in Hotfixes**, no longer duplicated in "My PRs".
- Window no longer drifts when a monitor is disconnected while it is already on the laptop screen. If the window was on the disconnected monitor it is now repositioned to the same corner on the best remaining screen instead of wherever Windows placed it.

## [1.5.2] - 2026-03-17

### Fixed

- Team review PRs still appeared in "Awaiting My Review" when the Team Review Requests section was disabled, because the `reviewRequests` GraphQL field was not fetched in that case, making classification impossible. The field is now always fetched so team PRs are excluded regardless of the section setting.

## [1.5.1] - 2026-03-17

### Fixed

- Tray context menu first item now reads **Close PR Monitor** when the window is open and **Open PR Monitor** when it is closed, instead of always showing "Open PR Monitor".
- Team review classification now correctly identifies whether the **current user** was directly requested as a reviewer. Previously, any `User`-type reviewer on the PR (even a different person) would cause it to be classified as a direct request; now only a `User` entry matching the authenticated username counts as direct.
- When the Team Review Requests section is disabled in Settings, team PRs are now hidden entirely instead of being moved to "Awaiting My Review".
- Saving settings now immediately triggers a data refresh so changes (such as toggling the team section) take effect without waiting for the next poll.

## [1.5.0] - 2026-03-17

### Added

- PRs where the user is the **assignee** (but not the author) now appear in "Awaiting My Review". This surfaces Copilot-created draft PRs that are assigned to the user. PRs authored by the user are excluded from this list to avoid duplication with "My PRs".
- **Team Review Requests section**: A new collapsible section (collapsed by default) groups PRs where a review was requested from one of your teams, keeping "Awaiting My Review" for direct personal review requests only. The section can be disabled in Settings → Sections; when disabled, team PRs continue to appear in "Awaiting My Review" and the extra `reviewRequests` GraphQL field is omitted from the query.

### Changed

- Reduced floating window corner snap inset from 12 px to 6 px so snapped placement sits closer to screen edges.

## [1.4.1] - 2026-03-11

### Changed

- Tray icon context menu replaced with a native Win32 popup menu that follows the system dark/light mode and uses correct Windows styling.
- About window restyled to match the Settings window: dark background, dark native title bar, custom flat buttons, and a subtle divider between info and actions.

## [1.4.0] - 2026-03-11

### Added

- **Notification settings tab**: Settings window now has a dedicated "Notifications" tab where each notification type (CI Failed, CI Passed, CI Error, Review Requested, PR Merged / Closed) can be individually toggled on or off. All notifications are enabled by default.

### Fixed

- Settings window styling improvements: dark native title bar, fully custom dark-themed buttons (removing WPF `ButtonChrome`), checkboxes, and polling-interval slider.
- Snapped corner is no longer lost after restart. Programmatic moves (such as initial positioning before startup placement is restored) no longer overwrite the in-memory corner state, so `RestoreStartupPlacement` always reads the correct saved corner.
- PR list is now preserved when a poll fails due to a network error. Previously, any failed API call resulted in empty lists being shown; now the last known state is retained until a successful poll.

## [1.3.1] - 2026-03-11

### Fixed

- Windows toast notifications now display the app name as **PR Monitor** instead of **PrMonitor**.

### Changed

- Multiple PR changes of the same type within a single poll are now grouped into one notification instead of firing one per PR. For groups of more than one PR, the notification shows a count and up to four titles.
- Scrollbar replaced with a minimal 6 px overlay scrollbar that fades in on hover, matches the window colour scheme, and is only shown when there is actually content to scroll. Window `MaxHeight` increased from 600 to 700 px to reduce unnecessary scrolling for typical list sizes.

## [1.3.0] - 2026-03-10

### Fixed

- PRs with merge conflicts now show a red dot instead of the default grey/green indicator. Previously, a conflicting PR with no CI runs would appear green because `statusCheckRollup` is null when pipelines don't start.

### Added

- PR rows now show a green checkmark icon (✓) when the PR has been approved. When the PR also has unresolved review comments, the comments icon takes priority and the checkmark is not shown.
- Persisted PR window state: the app now remembers last visibility and position (Left/Top), restores them on startup, and auto-opens the window if it was visible in the previous session.
- Snapped corner is now persisted alongside the window position. After a restart, the window re-anchors to the same corner on the same monitor, so size changes (expand/collapse sections) keep the window correctly pinned.

### Changed

- Windows toast notifications now use the app display name `PR Monitor` instead of `PrMonitor`.
- Window restore now applies monitor-aware clamping with minimal displacement so the PR window is always fully visible after display layout changes.

## [1.2.0] - 2026-03-09

### Fixed

- Tray icon now correctly reflects CI failures and unresolved review comments on **My PRs** (non-auto-merge). Previously only Auto-Merge and Hotfix PRs contributed to the red/amber icon state. My PRs are also included in the badge count and tooltip, and "Move to Later" on My PRs now also shows up as a blue dot.
- Tray context menu "My PRs (…)" count now shows all own PRs (auto-merge + non-auto-merge) to match the GitHub link it opens.
- Tray icon and badge now update immediately when moving a PR to/from Later (previously only updated after the next poll).

### Added

- Automatic daily update check: the app now silently checks for a new release ~30 seconds after startup and every 24 hours. When an update is available, a green clickable banner appears in the PR window footer ("Update available: vX.Y.Z — click to download") instead of an intrusive MessageBox.
- PR row context menu now includes a **Copy branch name** option (above "Move to later") that copies the head branch name to the clipboard.

### Changed

- Added the **About** flow: tray menu now places **About…** and **Settings…** directly under **Open PR Monitor**, the About dialog shows the app logo/version/repository link, and manual **Check for updates…** runs from that dialog.

## [1.1.3]

### Fixed

- Improved update-check diagnostics: failures are now logged and manual checks show specific error details instead of a generic message.
- Improved update checks to use authenticated `gh` API access first (with HTTP fallback) to reduce anonymous GitHub rate-limit failures.
- Added automatic diagnostics log rotation to prevent `pr-monitor.log` from growing indefinitely.

## [1.1.2]

### Fixed

- Fixed single-file release startup on downloaded builds by including native libraries for self-extract runtime.

### Changed

- Expanded README troubleshooting with SmartScreen unblock steps via file Properties for `PrMonitor.exe`.

## [1.1.1]

### Changed

- Updated application icon assets (`icon.ico` and PNG source).
- Updated release packaging to publish a single-file `PrMonitor.exe` artifact for `win-x64`.

## [1.1.0]

### Added

- Added unresolved review comments indicator per PR row with count tooltip.
- Added startup and manual update checks against the latest GitHub release.
- Added tray context menu version display (`Version x.y.z`).
- Added diagnostics logging for polling and GitHub/GraphQL failures.
- Added corner snapping for the floating window with multi-monitor recovery.

## [1.0.1]

### Changed

- Improved release automation to publish artifacts only when project version changes.
- Refined build validation workflow to run restore/build checks on pushes and pull requests.

## [1.0.0]

### Added

- Initial release of PR Monitor as a Windows system-tray app for GitHub pull request monitoring.
- Added sections for Hotfixes, My Auto-Merge PRs, Awaiting My Review, My PRs, and Later.
- Added CI status indicators in PR rows and colored status badge in the tray icon.
- Added settings persistence for organizations, polling interval, auto-start, and section expansion state.
- Added GitHub authentication/API integration via the `gh` CLI.