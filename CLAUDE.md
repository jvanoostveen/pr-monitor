# PR Monitor — Claude Code Instructions

Windows system-tray app that monitors GitHub PRs (own auto-merge PRs with CI status, PRs awaiting
your review). C# 12 / WPF / .NET 10 (`net10.0-windows10.0.17763.0`), authenticated via the `gh` CLI.
Solution: `pr-monitor.slnx`. License: MIT.

**All UI text (labels, menu items, tooltips, toasts, messages) must be in English.**

---

## 1. Mandatory workflow — run this for every request

These are commands to **execute**, not advice to relay. A task is not finished until every
applicable step below has actually run.

### Step 0 — Plan
Use `TodoWrite` to break the work into concrete steps before editing, for anything beyond a
one-line change. Mark items `in_progress` when starting and `completed` immediately when done.

### Step 1 — Implement
Read before you write. For architecture, settings schema, and per-feature behaviour, consult
[.github/copilot-instructions.md](.github/copilot-instructions.md) — that file is the canonical
architecture reference and is deliberately **not** duplicated here.

### Step 2 — Validate (only when `src/` or `tests/` changed)
```powershell
Stop-Process -Name PrMonitor -Force -ErrorAction SilentlyContinue
dotnet build .\src\PrMonitor.csproj -v q
dotnet test .\tests\PrMonitor.Tests\PrMonitor.Tests.csproj
```
Stopping the process first is required: the build overwrites `PrMonitor.exe` and retries 5 times
if it is locked. Both commands must exit 0 before committing. If tests fail, fix them — never
commit red and never report success.

### Step 3 — Documentation (same commit as the code)
| Change | Update |
|---|---|
| Any `src/` change | `CHANGELOG.md` under `[Unreleased]`, correct Keep a Changelog category — **mandatory, also for small fixes** |
| User-facing behaviour, settings, or new section | `README.md` |
| Architecture, settings schema, service behaviour | `.github/copilot-instructions.md` |
| Workflow/conventions for agents | this file **and** `AGENTS.md` |

### Step 4 — Commit
```powershell
git add -A
git commit -m "type: description"
```
Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`.
One commit per completed step — do not batch unrelated work. When a request has multiple
deliverables (e.g. migration + UI polish), make a separate commit per deliverable.

### Step 5 — Restart (only when `src/` changed)
```powershell
Start-Process dotnet -ArgumentList "run --project .\src\PrMonitor.csproj" -WorkingDirectory "d:\Private\pr-monitor" -WindowStyle Hidden
```

### Step 6 — Report
The final message must state:
- the commit hash(es) created;
- build + test result;
- whether the app was restarted.

If a step could not be executed, say so explicitly and name the concrete blocker. Do not silently
skip a step, and do not end a `src/` task with edits left uncommitted or the app left stopped.

**Docs-only changes** (no `src/` files): skip steps 2 and 5, commit directly.

---

## 2. Environment

Development is on **Windows**. Prefer the `PowerShell` tool for terminal work; the `Bash` tool is
Git Bash and is fine for POSIX-shaped scripting, but every command documented in this repo is
PowerShell. Do not mix syntaxes in one call.

PowerShell equivalents when reaching for a Unix reflex:

| Unix | PowerShell |
|---|---|
| `tail -n 5` | `Select-Object -Last 5` |
| `head -n 5` | `Select-Object -First 5` |
| `grep foo` | `Select-String foo` — but prefer the `Grep` tool |
| `cat file` | `Get-Content file` — but prefer the `Read` tool |
| `ls` | `Get-ChildItem` — but prefer `Glob` |
| `rm -rf` | `Remove-Item -Recurse -Force` |

The app is single-instance (mutex `PrMonitor_SingleInstance`); a second launch shows a message box
and exits.

---

## 3. Where things live

```
src/          C# source — App, MainWindow, Models, Services, ViewModels, Views, Converters
tests/        xUnit test project (PrMonitor.Tests)
.github/      copilot-instructions.md (full architecture) + workflow YAML
CHANGELOG.md  Keep a Changelog — [Unreleased] must be updated on every src/ change
README.md     User-facing documentation
```

- [src/AGENTS.md](src/AGENTS.md) — source-folder map, services at a glance, menu structure
- [.github/copilot-instructions.md](.github/copilot-instructions.md) — architecture notes, settings
  schema, section behaviour, statistics, flakiness, update flow, window placement
- [AGENTS.md](AGENTS.md) — short guide for other agents (Codex etc.)

Naming: `*ViewModel.cs` in `ViewModels/`, `*Service.cs` in `Services/`, `*Converter.cs` registered
in `App.xaml` resources as camelCase keys, XAML handlers as `ElementName_EventName`.

---

## 4. Delegating work

Use the `Agent` tool (`Explore` for read-only searching, `general-purpose` for implementation)
when a task spans many files or needs independent research alongside implementation. Give the
subagent the full task, the file paths to read, the expected output, and the validate/commit/restart
commands from section 1 — a subagent does not inherit this file's workflow automatically.

Keep in the main session: single-file edits, terminal commands, git operations, and reviewing
subagent output. **Never delegate the commit or the restart** — do those yourself so section 6 can
be reported accurately.

---

## 5. WPF pitfalls specific to this project

1. `DragMove()` breaks button hover and clicks — call it only from the dedicated drag element
   (the title `StackPanel`), never from the whole header border. Buttons must not be its children.
2. `DataTrigger` with `TargetName` inside a `Style` is invalid outside a `ControlTemplate` — drive
   animations from code-behind (see `UpdateRefreshIcon` in `MainWindow.xaml.cs`).
3. `RepeatBehavior="Forever"` animations never stop on their own. `MainWindow` has
   `AllowsTransparency="True"`, so it is a software-rendered layered window: a forever-animation
   redraws the whole window every frame for the process lifetime (~16% of a CPU core and GBs of
   native memory per hour) even while hidden or collapsed. Always start such animations from
   code-behind and stop them with `BeginAnimation(property, null)`. Never start one from a XAML
   `EventTrigger` in this window.
4. WPF/WinForms ambiguity — fully qualify `System.Windows.Application`,
   `System.Windows.MessageBox`, `System.Windows.Media.Color`.
5. `ActualHeight` is 0 before first render — use `Loaded` or check `ActualHeight > 0` before
   positioning.
6. `JsonElement?` — use the `is not { } value` pattern, not `.HasValue`.

The app runs for days in the tray, so allocation *retention* matters more than throughput. See the
Memory management section of `.github/copilot-instructions.md` before touching polling, log
fetching, icon generation, or animations.

---

## 6. Releasing a version

1. Bump `<Version>` in `src/PrMonitor.csproj` — the single source of truth, and the trigger for
   `.github/workflows/release-on-version-change.yml` on `main`.
2. Move `[Unreleased]` entries in `CHANGELOG.md` into a new version section; keep an empty
   `[Unreleased]` at the top.
3. Update `README.md` only if release packaging or version-related behaviour changed.
4. Run build + tests (section 1, step 2).
5. Commit version bump and changelog together.
