# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- PRs with merge conflicts now show a red dot instead of the default grey/green indicator. Previously, a conflicting PR with no CI runs would appear green because `statusCheckRollup` is null when pipelines don't start.

### Added

- PR rows now show a green checkmark icon (✓) when the PR has been approved. When the PR also has unresolved review comments, the comments icon takes priority and the checkmark is not shown.
- Persisted PR window state: the app now remembers last visibility and position (Left/Top), restores them on startup, and auto-opens the window if it was visible in the previous session.

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