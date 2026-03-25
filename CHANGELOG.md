# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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