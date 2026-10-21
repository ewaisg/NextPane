# NexPane — Current State

> Updated by every subagent at the end of every session. Newest entry is at the top of the Session Log.
> Last update: **2026-10-21** by **Subagent 1 (Lead Architect & State Scribe)**.

## 1. Snapshot

| Item | Value |
|---|---|
| Current phase | **Phase 1 — Architecture / Stack Lock / Handoff Init** (all deliverables done except P1.11 and P1.12) |
| Toolchain | .NET SDK 10.0.402, Git 2.55.0, Inno Setup 6 (per-user: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) |
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
- P1.01 toolchain: verified, `[READY_FOR_QA]` (installed by ORC).
- P1.09 repo, solution and test framework: `[READY_FOR_QA]`; initial commit `c6b4d4f`.
- P1.10 architecture layering tests, including the negative check (a forbidden `Search → Indexing` reference fails 2 tests; reverted): `[READY_FOR_QA]`.
- P1.13 LICENSE.txt (proprietary EULA) and THIRD-PARTY-NOTICES.txt: `[READY_FOR_QA]`.
- User decisions D1–D5 recorded in ARCHITECTURE §20/§21, TODO (P1.13, P6.26–P6.30, P7.08, P7.09, P7.11, P7.12) and HANDOFF §9.
- New TODO items: P3.37 (NetArchTest type-level rules) and P6.26–P6.30 (GPU Whisper). The TODO now has 175 items: 11 `[READY_FOR_QA]`, 164 `[PENDING]`.

## 4. Active work

| Item | Owner | Status | Notes |
|---|---|---|---|
| P1.11 eng scripts (`build.ps1`, `publish.ps1` with optional skipped signing, `installer.ps1`) | S1 | `[PENDING]` | Next |
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

## 6. Exact next commands (new PowerShell window)

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
Set-Location C:\Users\Ewais\Projects\NexPane
git status; git log --oneline -3
dotnet build NexPane.sln -c Release
dotnet test --solution NexPane.sln -c Release --filter-not-trait "Category=Elevated" --filter-not-trait "Category=Live"
# Then: implement P1.11 (eng\build.ps1, eng\publish.ps1, eng\installer.ps1) and P1.12 (eng\check-placeholders.ps1),
# or hand Phase 2 to S2 (start with P2.01 logo SVGs, then P2.02 tools\NexPane.AssetGen)
# and Phase 3 to S3 (start with P3.01 VfsPath, P3.03 Storage migrations).
```

## 7. Session log

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
