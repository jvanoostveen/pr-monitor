# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- No unreleased changes yet.

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