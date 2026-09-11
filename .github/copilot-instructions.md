# PR Monitor — Copilot Instructions

Windows system-tray app that monitors GitHub PRs (own auto-merge PRs with CI status, PRs awaiting
your review). C# 12 / WPF / .NET 10 (`net10.0-windows10.0.17763.0`), authenticated via the `gh`
CLI. Solution: `pr-monitor.slnx`. License: MIT.

**All UI text (labels, menu items, tooltips, toasts, messages) must be in English.**

Architecture, settings schema, per-feature behaviour and WPF constraints are **not** in this file —
they live in [ARCHITECTURE.md](../ARCHITECTURE.md), the canonical technical reference. Read the
relevant section there before changing a subsystem.

---

## 1. Mandatory workflow — apply to every request, however small

These are steps to **execute**, not advice to relay. A task is not finished until every applicable
step below has actually run.

### Step 0 — Plan
Break the work into concrete, actionable steps with the `manage_todo_list` tool before writing
code. Mark each item `in-progress` when starting it and `completed` immediately when done.

Example for "add a new section to the window":
- [ ] Read relevant files (ViewModel, XAML, service)
- [ ] Update model/service layer
- [ ] Update ViewModel
- [ ] Update XAML
- [ ] Build, run tests, verify no errors
- [ ] Update CHANGELOG.md
- [ ] Commit and restart

### Step 1 — Implement
Read before you write. Consult [ARCHITECTURE.md](../ARCHITECTURE.md) for how the subsystem you are
touching works, and its [WPF pitfalls](../ARCHITECTURE.md#wpf-pitfalls) section before changing
XAML, animations, or window placement.

When a task touches **3 or more files** or needs independent research alongside implementation,
delegate to a subagent with the `runSubagent` tool. Give it: the full task description, the file
paths to read, the expected output (which files to change and how), and the validate/commit/restart
commands from this section — a subagent does not inherit these instructions automatically.

Keep in the main session: single-file edits, quick investigations, terminal commands, git
operations, and reviewing subagent output. **Never delegate the commit or the restart** — do those
yourself so step 5 can be reported accurately.

### Step 2 — Validate (only when `src/` or `tests/` changed)
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrMonitor.csproj -v q
dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj
```
Stopping the process first is required: the build overwrites `PrMonitor.exe` and retries 5 times if
it is locked. Both commands must report `ExitCode: 0` before committing. If tests fail, fix them —
never commit red and never report success.

### Step 3 — Documentation (same commit as the code)
| Change | Update |
|---|---|
| Any `src/` change | `CHANGELOG.md` under `[Unreleased]`, correct Keep a Changelog category (`Added`, `Changed`, `Fixed`, …) — **mandatory, also for small fixes and cosmetic changes** |
| User-facing behaviour, settings, or new section | `README.md` — feature table and prose |
| Architecture, settings schema, service or window behaviour | `ARCHITECTURE.md` |
| Agent workflow or repository conventions | this file **and** `CLAUDE.md` **and** `AGENTS.md` |

`CHANGELOG.md` is the canonical version history: concise entries, grouped by Keep a Changelog
category, under `[Unreleased]` until released. `README.md` keeps a release-first onboarding flow in
**Getting started** (download the latest release and run the executable); clone/run-from-source
belongs in the separate development section.

**Amend, don't append.** Before writing a changelog entry, check whether `[Unreleased]` already has
one for the feature or area being touched. If it does, edit that entry in place instead of adding a
new bullet — fold the new behaviour into its description. This applies especially to a fix for a
feature that is itself still under `[Unreleased]`: nobody has seen the old behaviour yet, so there
is nothing to record as a fix — just update the existing `Added`/`Changed` entry so it describes
what the feature does now, rather than adding a separate `Fixed` line. Only add a new entry when
the change is unrelated to anything already listed under `[Unreleased]`. Once a version is released
this no longer applies — a fix for released behaviour always gets its own new `Fixed` entry.

### Step 4 — Commit
```powershell
git add -A
git commit -m "type: description"
```
Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`.
One commit per completed step — do not batch unrelated work. When a request contains multiple
deliverables (for example framework migration + UI polish), make a separate commit per deliverable.

### Step 5 — Restart (only when `src/` changed)
```powershell
Start-Process dotnet -ArgumentList "run --project .\src\PrMonitor.csproj" -WorkingDirectory "d:\Private\pr-monitor" -WindowStyle Hidden
```

### Step 6 — Report
The final response must state:
- the commit hash(es) created;
- the build and test result;
- whether the app was restarted.

If a step could not be executed, stop and report the concrete blocker. Do not silently skip a step,
and never end a `src/` task with edits left uncommitted or the app left stopped.

**Docs-only changes** (no `src/` files): skip steps 2 and 5, commit directly.

---

## 2. Development environment

Development is on **Windows** with **PowerShell**. Never use Unix/bash commands such as `tail`,
`grep`, `find`, `cat`, `head`, `ls`, `rm`, or `cp`:

| Unix | PowerShell |
|---|---|
| `tail -n 5` | `Select-Object -Last 5` |
| `head -n 5` | `Select-Object -First 5` |
| `grep foo` | `Select-String foo` |
| `cat file` | `Get-Content file` |
| `ls` | `Get-ChildItem` |
| `rm -rf` | `Remove-Item -Recurse -Force` |

Run the app in development with `dotnet run --project .\src\PrMonitor.csproj`. It is
single-instance (mutex `PrMonitor_SingleInstance`); a second launch shows a message box and exits.

---

## 3. Releasing a version

1. Bump `<Version>` in `src/PrMonitor.csproj` — the single source of truth, and what triggers
   `.github/workflows/release-on-version-change.yml` on `main`.
2. In `CHANGELOG.md`, move `[Unreleased]` entries into a new version section (e.g. `[1.2.0]`) and
   keep an empty `[Unreleased]` at the top.
3. Update `README.md` only if version-related behaviour or release packaging changed.
4. Run build and tests (section 1, step 2).
5. Commit the version bump and changelog together.

---

## 4. Related documents

| File | Purpose |
|---|---|
| [ARCHITECTURE.md](../ARCHITECTURE.md) | Canonical technical reference — tech stack, structure, subsystems, WPF pitfalls, settings schema |
| [../src/AGENTS.md](../src/AGENTS.md) | Source-folder map, services at a glance, menu structure |
| [CLAUDE.md](../CLAUDE.md) | Same workflow, for Claude Code |
| [AGENTS.md](../AGENTS.md) | Short guide for other agents (Codex etc.) |
| [CHANGELOG.md](../CHANGELOG.md) | Version history |

Note: Claude Code additionally enforces steps 2-5 with hooks in `.claude/` (see `CLAUDE.md` section 7).
Copilot has no equivalent, so the steps above must be executed deliberately.
