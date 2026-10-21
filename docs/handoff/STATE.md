# NexPane — Current State

> Updated by every subagent at the end of every session. Newest entry is at the top of the Session Log.
> Last update: **2026-10-21** by **Subagent 7 (Release & Installer Packaging, P1.11)**.

## 1. Snapshot

| Item | Value |
|---|---|
| Current phase | **Phase 1 — Architecture / Stack Lock / Handoff Init** (all deliverables done except P1.12) |
| Toolchain | .NET SDK 10.0.402, Git 2.55.0, Inno Setup **6.7.3** (per-user: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) |
| Pipeline (P1.11) | `eng\build.ps1` → `eng\publish.ps1` → `eng\installer.ps1 -AllowMissingBranding` verified: `dist\NexPane-Setup.exe` = **55,354,308 bytes (52.8 MB)**; publish folder 462 files / 196.2 MB |
| Git | Repo on `main`, no remote. Commits: `c6b4d4f` (scaffold) and a follow-up docs/decisions commit (see `git log`). Local identity: `NexPane Dev <ewaisg@gmail.com>`. |
| Solution | `NexPane.sln`: 15 `src` projects, 16 test projects, 1 benchmark runner |
| Build | `dotnet build NexPane.sln -c Release` → **Build succeeded. 0 Warning(s) 0 Error(s)** |
| Tests | Default filter → **total: 47, failed: 0, succeeded: 47, skipped: 0** |
| App | `NexPane.exe` opens a 1280×800 window titled "NexPane"; `CloseMainWindow` exits with code 0 |
| Feature code | None yet. Library projects are empty shells with correct references. |
| User decisions | D1–D5 recorded (ARCHITECTURE §20): proprietary EULA; unsigned; no Windows 11 top-level menu; Index Service checked by default (admin installs); no Explorer replacement; GPU Whisper in scope |
| Blockers | None |

## 2. What exists on disk (tracked)

```
C:\Users\Ewais\Projects\NexPane\
├─ NexPane.sln  global.json  Directory.Build.props  Directory.Packages.props
├─ .editorconfig  .gitattributes  .gitignore  README.md  LICENSE.txt  THIRD-PARTY-NOTICES.txt
├─ docs\handoff\  ARCHITECTURE.md  TODO.md  STATE.md  HANDOFF.md
├─ eng\  common.ps1  build.ps1  publish.ps1  installer.ps1        (P1.11)
├─ installer\  NexPane.iss                                         (P1.11; P7.08 completes it)
├─ src\
│  ├─ NexPane.Core ............ net10.0, no references
│  ├─ NexPane.Shell, NexPane.Storage ........................ → Core
│  ├─ NexPane.Security, .Network ............................ → Core, Shell
│  ├─ NexPane.Indexing, .Metadata ........................... → Core, Shell, Storage
│  ├─ NexPane.Search, .Sync, .AI ............................ → Core, Storage
│  ├─ NexPane.Archives, .Scripting .......................... → Core
│  ├─ NexPane.Preview (WPF lib) ............................. → Core, Shell
│  ├─ NexPane.App (WPF exe, AssemblyName NexPane) ........... → all of the above; App.xaml, MainWindow.xaml, app.manifest
│  └─ NexPane.IndexService (worker exe) ..................... → Core, Shell, Indexing; Program.BuildHost, service "NexPaneIndex"
└─ tests\
   ├─ Directory.Build.props (xUnit v3 MTP + FluentAssertions 7.2.0 for *Tests)   Shared\ModuleAssembly.cs
   ├─ NexPane.<Module>.Tests ×14  (ModuleBuildTests: x64 + TFM/platform attributes)
   ├─ NexPane.IndexService.Tests  (+ HostLifetimeTests: start/stop, service name)
   ├─ NexPane.Architecture.Tests  (29 tests: layering + repository conventions)
   ├─ NexPane.UITests             (AppBuildTests; FlaUI added in Phase 4)
   └─ NexPane.Benchmarks          (BenchmarkSwitcher runner; benchmarks added in P3.35)
```

Package pins (`Directory.Packages.props`): Microsoft.Extensions.Hosting.WindowsServices 10.0.13, xunit.v3.mtp-v2 4.0.2, FluentAssertions [7.2.0], BenchmarkDotNet 0.15.8.

## 3. Just finished (this session)

**2026-10-21 (c), S7: P1.11 → `[READY_FOR_QA]`**
- Added `eng/common.ps1`, `eng/build.ps1`, `eng/publish.ps1`, `eng/installer.ps1` and `installer/NexPane.iss`.
- Evidence:
  - `-DryRun` on all three scripts prints the HANDOFF §5 commands.
  - `build.ps1`: restore; build `0 Warning(s) 0 Error(s)`; tests `total: 47, failed: 0, succeeded: 47`.
  - `publish.ps1`: App + IndexService published self-contained R2R into `build\publish\win-x64` (462 files, 196.2 MB); "signing skipped". The published `NexPane.exe` opens the "NexPane" window and exits 0.
  - `installer.ps1 -AllowMissingBranding`: Inno Setup 6.7.3 compiled with no warnings in about 34 s, producing `dist\NexPane-Setup.exe` (55,354,308 bytes).
  - Fail-fast: a failing step's exit code propagates (7 → 7). An ISCC error exits 2 and its message is shown. Missing branding exits 1 (script) or hits `#error` (`.iss`).
- The setup exe was **not** run on this machine.

**Earlier (S1)**
- P1.01 toolchain: verified, `[READY_FOR_QA]` (installed by ORC).
- P1.09 repo, solution and test framework: `[READY_FOR_QA]`; initial commit `c6b4d4f`.
- P1.10 architecture layering tests, including the negative check (a forbidden `Search → Indexing` reference fails 2 tests; reverted): `[READY_FOR_QA]`.
- P1.13 LICENSE.txt (proprietary EULA) and THIRD-PARTY-NOTICES.txt: `[READY_FOR_QA]`.
- User decisions D1–D5 recorded in ARCHITECTURE §20/§21, TODO (P1.13, P6.26–P6.30, P7.08, P7.09, P7.11, P7.12) and HANDOFF §9.
- New TODO items: P3.37 (NetArchTest type-level rules) and P6.26–P6.30 (GPU Whisper). The TODO now has 175 items: 11 `[READY_FOR_QA]`, 164 `[PENDING]`.

## 4. Active work

| Item | Owner | Status | Notes |
|---|---|---|---|
| P1.11 eng scripts + `installer/NexPane.iss` | S1 (done by S7) | `[READY_FOR_QA]` | Awaiting QA re-run |
| P1.12 `eng/check-placeholders.ps1` | S1 | `[PENDING]` | Next |
| Phase 2 / Phase 3 kickoff | S2 / S3 | `[PENDING]` | Can start now, in parallel |

## 5. Active issues / risks
1. **Deviations from the original plan** (all recorded in ARCHITECTURE §21):
   - The test runner is Microsoft.Testing.Platform, so the test command syntax changed (see §6).
   - `tools/NexPane.AssetGen` is deferred to P2.02.
   - `NexPane.IndexService.Tests` was added.
   - Layering is enforced by parsing .csproj files; NetArchTest type-level rules move to P3.37.
2. Every test project must keep at least one test: Microsoft.Testing.Platform fails an assembly with zero tests (exit code 8).
3. FluentAssertions 7 `OnlyContain` fails on empty collections; use `NotContain(x => !predicate)`.
4. The EULA in LICENSE.txt was drafted without legal review; a lawyer should review it before public release.
5. The orchestrator's HANDOFF §3 `$Iscc` line had the `| Where-Object` pipeline inside a trailing comment, which made `$Iscc` an array of all three candidate paths. This is fixed in HANDOFF §3.
6. NuGet audit warnings (NU1901–NU1904) are deliberately not errors; QA treats them as release blockers (P7.04).
7. **Installer gaps before release (P7.08 / P7.09):**
   - The WebView2 bootstrapper is not in the `.iss` yet.
   - Release builds must compile **without** `-AllowMissingBranding` once P2.04 and P2.06 deliver the branding assets.
   - Service install/upgrade/uninstall and the HKLM/HKCU verbs are compiled but have never been executed; they are tested in Windows Sandbox in P7.09.
   - The optional signing path is untested because signtool is not installed.
8. Running `eng\publish.ps1` rebuilds App and IndexService as self-contained in their `bin\Release\...\win-x64` folders, so the next `eng\build.ps1` recompiles those two projects. This is expected.

## 6. Exact next commands (new PowerShell window)

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
Set-Location C:\Users\Ewais\Projects\NexPane
git status; git log --oneline -3
# Full pipeline (each script fails fast; add -DryRun to only print commands):
powershell -NoProfile -ExecutionPolicy Bypass -File eng\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng\installer.ps1 -AllowMissingBranding   # pre-Phase-2 only; drop the switch once assets exist
# Then: implement P1.12 (eng\check-placeholders.ps1),
# or hand Phase 2 to S2 (start with P2.01 logo SVGs, then P2.02 tools\NexPane.AssetGen)
# and Phase 3 to S3 (start with P3.01 VfsPath, P3.03 Storage migrations).
```

## 7. Session log

### 2026-10-21 (c) — Subagent 7 (Release & Installer Packaging Engineer)
- P1.11:
  - Wrote the eng pipeline scripts and `installer/NexPane.iss`.
  - Ran build → test → publish → installer end to end; the evidence is in §3.
  - Fixed two Inno 6.7.3 compile issues: custom messages need the `english.` prefix, and `RemoveBackslash` is deprecated.
  - Added two ARCHITECTURE §21 decision-log rows: the service is managed in `[Code]`, and the `-AllowMissingBranding` / `-DryRun` / warnings-as-errors script behaviour.
- Deviations:
  - Orchestrator correction: the output is the unversioned `dist\NexPane-Setup.exe` as the product spec mandates (the release engineer had used `NexPane-Setup-<version>.exe`). The version lives in the exe's version resource (ProductVersion 0.1.0). Re-verified: compiles warning-free, 55,354,308 bytes.
  - The WebView2 bootstrap is deferred to P7.08.

### 2026-10-21 (b) — Subagent 1 (Lead Architect & State Scribe)
- Executed P1.09 at the orchestrator's request:
  - Ran `git init -b main`, pinned `global.json` to 10.0.402 with latestFeature roll-forward and MTP runner, and created the classic-format `NexPane.sln`.
  - Hand-wrote all csproj files (no template boilerplate) and added Directory.Build.props, Directory.Packages.props, .editorconfig and .gitattributes.
  - Built a minimal WPF window and the IndexService host.
- Wrote the architecture tests (29) and per-module build tests. Build: 0 warnings, 0 errors. Tests: 47/47 pass. App window verified.
- Commit `c6b4d4f` "Phase 1: architecture docs, solution skeleton, test framework".
- Recorded user decisions D1–D5. Wrote LICENSE.txt and THIRD-PARTY-NOTICES.txt. Designed GPU Whisper (§13.2) and added P6.26–P6.30.

### 2026-10-21 (a) — Subagent 1 (Lead Architect & State Scribe)
- Wrote ARCHITECTURE.md:
  - stack rationale, layout, layering and threading;
  - 5 Mermaid flows plus a dependency graph;
  - full SQL for `index.db` and `app.db`, plus C# contracts;
  - packages and licenses, security, performance budgets and testing;
  - branding and installer, 24 risks, open questions, decision log and traceability.
- Wrote TODO.md, STATE.md, HANDOFF.md, README.md and .gitignore.
