# NexPane — Current State

> Updated by every subagent at the end of every session. Newest entry is at the top of the Session Log.
> Last update: **2026-10-22 (QA-P3)** by **Subagent 6 (QA, Verification & Security Inspector)**: Phase 3 gate P3.36 on branch `phase3-core-search` @ 25da0df — **not passed** (28 verified, 6 rejected, see §3). Earlier: 2026-10-22 by S2. Note: this branch's Phase 1/2 lines predate main `4589ad1` (Phase 1 complete 14/14, Phase 2 merged); take main's text for those when merging.
> Roster (user-mandated numbering): S1 Architect & State Scribe · S2 Core Systems & Indexing Engine · S3 Native Windows Shell & OS Integration · S4 AI & Semantic Pipeline · S5 UI/UX & Workspace · **S6 QA, Verification & Security Inspector (veto)** · S7 Release & Installer Packaging (incl. icons/assets). Older log entries below that say "S7 QA role" refer to the QA role, which is now **S6**.

## 1. Snapshot

| Item | Value |
|---|---|
| **Phase 3 gate (P3.36)** | **NOT passed (S6, 2026-10-22).** 28 of 34 S2 items `[VERIFIED_COMPLETE]`; **rejected → `[IN_PROGRESS]`: P3.11, P3.13, P3.14, P3.16, P3.22, P3.31**; P3.34 (S3) and P3.37 (S1) `[PENDING]`; new P3.38 (S2). Do not merge `phase3-core-search` into `main` until the rejects are fixed and re-verified |
| Current phase | **Phase 1 — NOT complete: 13/14 `[VERIFIED_COMPLETE]`.** P1.02 rejected again → `[IN_PROGRESS]` (S1): the uninstall-time purge in §19.3/§21 relies on Inno `ExecAsOriginalUser`, which Inno does not support at uninstall time. Phase 2 (S7) and Phase 3 (S2) may start now |
| Placeholder scanner (P1.12) | `eng\check-placeholders.ps1` → **26 files, 0 violations, 0 allowlisted, 0 stale, exit 0**. The four `TODO P1.11` cross-references now read "task(s) P1.11"; `eng\placeholder-allowlist.json` has `"entries": []` |
| Toolchain | .NET SDK 10.0.402, Git 2.55.0, Inno Setup 6.7.3 (per-user: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) |
| Pipeline (P1.11) | `eng\build.ps1` → `eng\publish.ps1` → `eng\installer.ps1 -AllowMissingBranding` → unversioned `dist\NexPane-Setup.exe` (55.35 MB, ProductVersion 0.1.0; never executed) |
| Git | Repo on `main`, no remote. Recent commits: `c6b4d4f` scaffold, `0fe2b0a` decisions, `92abcb9` P1.11, `6e4bdbb` unversioned installer, `22fb2ba` QA audit, then the S1 QA-fix commit (see `git log -1`). Identity: `NexPane Dev <ewaisg@gmail.com>` |
| Solution | `NexPane.sln`: 15 `src` projects, 16 test projects, 1 benchmark runner |
| Build | `dotnet build NexPane.sln -c Release` → **Build succeeded. 0 Warning(s) 0 Error(s)** |
| Tests | Default filter → **total: 108, failed: 0, succeeded: 108** (QA added 28 `GitIgnoreTests`) |
| App | `NexPane.exe` opens a 1280×800 window titled "NexPane" and exits 0 on `CloseMainWindow` |
| Feature code | None yet |
| User decisions | D1–D5 (ARCHITECTURE §20) + P1.14 (§21): network credentials in the Windows Credential Locker; AI keys via DPAPI/Credential Manager |
| Blockers | None for Phase 2/3. P1.02 open (uninstall purge mechanism; affects P4.43/P7.14 only) |

## 2. What exists on disk (tracked)

```
C:\Users\Ewais\Projects\NexPane\
├─ NexPane.sln  global.json  Directory.Build.props  Directory.Packages.props
├─ .editorconfig  .gitattributes  .gitignore  README.md  LICENSE.txt  THIRD-PARTY-NOTICES.txt
├─ docs\handoff\  ARCHITECTURE.md  TODO.md  STATE.md  HANDOFF.md
├─ eng\  common.ps1  build.ps1  publish.ps1  installer.ps1        (P1.11)
│        check-placeholders.ps1  placeholder-allowlist.json     (P1.12)
├─ installer\  NexPane.iss                                         (P1.11; P7.08/P7.14 complete it)
├─ src\   (15 projects; see ARCHITECTURE §3, layering §4)
└─ tests\ (16 test projects + NexPane.Benchmarks; Architecture.Tests = 62 tests incl. 33 PlaceholderScannerTests)
```

Package pins: Microsoft.Extensions.Hosting.WindowsServices 10.0.13, xunit.v3.mtp-v2 4.0.2, FluentAssertions [7.2.0], BenchmarkDotNet 0.15.8.

## 3. Just finished

**2026-10-22 (QA-P3), S6: Phase 3 gate P3.36 — NOT passed**
- Worked in `.worktrees\phase3` only (absolute paths); scratch output in the S6 scratchpad. Power: **AC** (Win32_Battery status 2, 99 %). CPU: AMD Ryzen 9 9955HX, 16C/32T.
- Independent runs: `eng\build.ps1` exit 0 — **0 warnings, 0 errors; 745 tests, 742 passed, 3 skipped** (2 opt-in fixture regenerators, 1 symlink test without Developer Mode); all battery-conditional timing gates ran and passed. `eng\check-placeholders.ps1` exit 0 (92 files, 0 violations, 0 allowlisted). Migration SQL identical to ARCHITECTURE §9.
- Elevated evidence: user-run `build\elevated-tests.log` names commit 25da0df = HEAD (tree clean now), AC power, build 0/0, 5/5 `Category=Elevated` tests passed. Runner caveats → P3.38: the log's "Z" times are local time (`ToString('u')` without `ToUniversalTime()`), and the runner does not record or refuse a dirty working tree.
- **Benchmarks** (BenchmarkDotNet `--filter *`, AC power, means):

| Benchmark | Measured | Budget (§17) |
|---|---|---|
| Cold literal substring, 1M | 1.05 ms (σ 0.03) | p50 ≤ 5 / p95 ≤ 15 ms |
| Incremental refinement, 1M | 0.048 ms | p50 ≤ 1 / p95 ≤ 3 ms |
| Broad refinement worst (`re`→`rep`), 1M | mean 0.69, median 0.55, σ 0.27 ms (p95 ≈ 1.1) | p50 ≤ 1 / p95 ≤ 3 ms |
| Regex `(a+)+$` NonBacktracking, 1M | 43.6 ms | p95 ≤ 150 ms |
| `path:` directory set, 1M | 27.2 ms | ≤ 50 ms |
| Cold literal substring, 5M | 5.94 ms | p95 ≤ 60 ms |
| `GetFullPath` × 1M | 148.2 ms | ≤ 400 ms |
| Snapshot load, 1M | 800 ms | ≤ 2 s |
| FTS5 100k docs (phrase / term / prefix) | 0.29 / 6.4 / 12.3 ms | p95 ≤ 50 ms |
| USN enumeration, raw $MFT fill per 1M | **not measured** | ≤ 5 s / ≤ 10 s → P3.38 |

- **Rejections (details in TODO.md):** P3.11 size/mtime parity samples 1/97 of the 10k files; P3.13 no `sc create` + non-elevated client test (QA decision: testable on the dev machine, not deferred to Phase 7), client pipe opened at default SecurityImpersonation without server verification (pipe-squatting impersonation when the service is absent), journal reset detected by message substring; P3.14 no elevated NTFS deny-ACL test, `SubscribeAsync` checks only the direct parent (changes under descendants of a denied folder leak), verdict cache never invalidated on security changes; P3.16 `RdcwWatcher.Dispose` can double-free the native buffer; P3.22 slow-mode regex has a per-match timeout but no per-query/batch budget; P3.31 THIRD-PARTY-NOTICES lacks BouncyCastle.Cryptography and still marks shipped packages `[planned]`, §15 still lists MsgReader/UglyToad.PdfPig.
- Other review results (no defect): FTS5 queries quote every token and bind parameters (no FTS/SQL injection); the only interpolated SQL is an enum list and `PRAGMA user_version`; no PowerShell/process launches in `src` (share names only in tests); lock-free index readers reviewed (publication ordering, append-only arenas, epoch-based path cache) and found sound; sync-over-async only on dedicated writer/worker threads; `SettingsStore.Open` writes synchronously (call it off the UI thread).
- **Merge forecast vs main 4589ad1:** `git merge-tree` → only `docs/handoff/ARCHITECTURE.md`, `HANDOFF.md`, `STATE.md`, `TODO.md` conflict; `Directory.Packages.props`, `THIRD-PARTY-NOTICES.txt`, the solution and all code auto-merge. A throwaway merged worktree (docs resolved arbitrarily, never committed, then removed) built with 0 warnings / 0 errors and passed 819 of 822 tests (same 3 skips), placeholder scan clean (102 files). Resolve the doc conflicts by keeping main's Phase 1/2 text and this branch's Phase 3 text, and append both sides' §21 rows.

**2026-10-22, S2 (Core Systems & Indexing Engine): Phase 3 on branch `phase3-core-search` (worktree `.worktrees\phase3`, not merged)**

| Result | Detail |
|---|---|
| Items | 27 `[READY_FOR_QA]`: P3.01–P3.09, P3.15–P3.17, P3.19–P3.23, P3.25–P3.33, P3.35. 7 `[IN_PROGRESS]`: P3.10–P3.14 and P3.18 (code done; their `[Elevated]` acceptance tests are written but not run, because this session was not elevated), and P3.24 (p50 budget missed for broad keystrokes). P3.34 (S3), P3.36 (S6) and P3.37 (S1) are not S2's. |
| Build | `dotnet build NexPane.sln -c Release`: 0 warnings, 0 errors |
| Tests | Default filter: total 742, failed 0, succeeded 737, skipped 5. The skips are: the symlink test (Developer Mode off), 2 fixture regenerators (opt-in via `NEXPANE_REGENERATE_FIXTURES=1`), and timing gates that skip only when exceeded on battery power. |
| Scanner | `eng\check-placeholders.ps1`: 89 files, 0 violations, 0 allowlisted, exit 0 |
| Shared files touched | `Directory.Packages.props` (+Microsoft.Data.Sqlite 10.0.13, DocumentFormat.OpenXml 3.5.1, MimeKit 4.18.2, PdfPig 0.1.16, OpenMcdf 3.3.0); `src/NexPane.Shell/NexPane.Shell.csproj` (now byte-identical to main 4589ad1); `src/NexPane.IndexService/Program.cs` plus new `IndexPipeServer.cs` (the csproj is untouched); `THIRD-PARTY-NOTICES.txt` (+1 OpenMcdf line); `docs/handoff/ARCHITECTURE.md` (§21 rows only); `tests/NexPane.Indexing.Tests/NexPane.Indexing.Tests.csproj` (+Search reference). `NexPane.sln` is not touched. |

Benchmarks (`dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *`; Ryzen 9 9955HX, 32 threads; **laptop on battery power** for these runs, so the numbers are pessimistic):

| Budget (§17) | Measured (mean) | Verdict |
|---|---|---|
| Cold substring 1M: p50 ≤ 5 ms, p95 ≤ 15 ms | 0.81–1.40 ms (`invoice_00`); unit test p95 over 8 queries incl. broad ones is within 2× | met |
| Incremental refinement 1M: p50 ≤ 1 ms, p95 ≤ 3 ms | After the P3.24 fix, on AC: broad `in`→`inv` median 0.60 ms, `re`→`rep` 0.66 ms, `invoice_0`→`invoice_00` 0.14 ms, narrow 53 µs. Before: broad keystrokes about 2.9 ms p50 | met (fixed 2026-10-22 c) |
| Cold substring 5M: p95 ≤ 60 ms | 8.0 ms | met |
| Name regex NonBacktracking 1M: p95 ≤ 150 ms | 48.8–55.5 ms (`(a+)+$`) | met |
| `path:` dir-set 1M: ≤ 50 ms | 27.7–33.6 ms | met |
| `GetFullPath` × 1M: ≤ 400 ms | 177–201 ms (after the lock-free cache; 796 ms before) | met |
| Snapshot load 1M: ≤ 2 s | 853–860 ms | met |
| FTS5 100k documents: p95 ≤ 50 ms | phrase 0.30 ms, frequent term 7.2 ms, prefix 13.5–15.8 ms | met |
| Index memory ≤ 160 MB / 1M | below 160 MB managed heap (test passes; structural estimate 134 MB) | met |
| Walker ≥ 50k entries/s warm | about 1.37 M entries/s | met |
| FS change visible ≤ 1 s (local) | create/rename/move-in/delete each ≤ 1 s (test) | met |
| USN enumeration ≤ 5 s / $MFT fill ≤ 10 s per 1M | not measured (needs elevation) | open |


**2026-10-21 (f), S6 QA: re-verification of S1 commit 180b0c0**

| Item | Verdict | Evidence |
|---|---|---|
| P1.07 | VERIFIED | `git check-ignore -q`: 18 source/asset/fixture probes exit 1, 16 output/secret probes exit 0; `git ls-files -ci --exclude-standard` empty; new `GitIgnoreTests` (28) pass, and re-adding `models/` fails 3 of them (reverted) |
| P1.08 | VERIFIED | Every product requirement maps to a §22 row with existing TODO ids |
| P1.13 | VERIFIED | All §15 names + 4/4 PackageVersions present in notices; ASCII |
| P1.14 | VERIFIED | §21 decision; consistent terms in §2/§3/§9.2/§10.3/§12.6/§16/§18/§22; P5.31/P6.01 use PasswordVault |
| P1.02 | **REJECTED** | Other fixes OK (SQL re-run with FTS5 OK; Mermaid balanced, no stray `;`). Defect: §19.3 + §21 run `--purge-user-data` from the uninstaller via `ExecAsOriginalUser`; Inno 6.7.3 help (`ISetup.chm` › ExecAsOriginalUser › Remarks): "This function is not supported at uninstall time." P4.43/P7.14 acceptance inherit it |
| Roster | OK | Every owner tag in TODO.md matches the user roles (S2 engines/indexing/archives/sync/network, S3 native shell/OS/credential stores, S4 AI, S5 UI, S6 QA, S7 release/assets) |
| Build/test/scan | OK | `eng\build.ps1` exit 0: 0 warnings, 0 errors, 108/108; `check-placeholders.ps1` 26 files, 0 violations, 0 allowlisted, exit 0 |


**2026-10-21 (e), S1: QA rejection fixes, P1.14, roster renumbering**

| Item | Change | Evidence |
|---|---|---|
| Roster | All owner tags in TODO.md remapped **by role** to the user-mandated numbering (mapping note under the TODO roster table). Old S4 items were split between S3 (native shell) and S5 (UI); old S5 between S2 (engines and network), S3 (metadata providers, archive drag-out) and S5 (dialogs and panels); old S7 between S6 (QA/audit) and S7 (release). Roster tables updated in TODO, HANDOFF §8 and the ARCHITECTURE §18 DoD line | 182 owner tags processed (P4.43 added afterwards with the new numbering); P6.01 (credential stores) moved to S3 (with S4) as OS integration |
| P1.07 | `.gitignore` anchored (root `/…` patterns, or `/src/*/`, `/tests/*/`, `/tools/*/` for bin and obj), fixtures re-included with `!/tests/TestAssets/**` | `git check-ignore`: the 6 QA paths plus 14 new probes are not ignored; build outputs, secrets, models and user files are ignored. `git status --ignored` with probe files present lists the probes as `??` and only bin/obj/build/dist as `!!` |
| P1.02 | 7 defects fixed: §21 reference, `global.json` text, §19.3 service `[Code]` flow, §19.3 uninstall/user-data statement aligned with P7.14, Credential Locker decision applied, new items referenced, Mermaid render | **Mermaid 10.9.1 render 6/6 OK** (built-in browser, ESM from jsdelivr, `parse` + `render`, securityLevel strict). This found and fixed a real parse error: the §6.4 `;` inside a sequence message |
| P1.08 | §22 rewritten (A1–A11, B1–B17, C1–C10, D1–D2, E1–E6, X1–X9) | Every row has a TODO id, including P4.40–P4.43, P5.35, P5.36, P7.14, P1.12, P1.14 |
| P1.13 | NetArchTest.Rules, explicit Microsoft.Extensions.* entries and the Windows SDK projection added | Script: 54 §15 names → 0 missing; 4/4 PackageVersions present; 0 non-ASCII bytes |
| P1.14 | Decision: **network credentials → Windows Credential Locker (`PasswordVault`, resource `NexPane:net`)**; **AI keys → DPAPI / Credential Manager (CredWrite `NexPane:ai:…` + DPAPI overflow)**. Recorded in §21; §10.3 now has `CredentialArea` + `ICredentialStoreProvider` | Unpackaged-process test (PS 5.1, Win11 26300): 15 Locker entries added without error, 4,000-char secret round-tripped, all removed. P5.31, P6.01 and P7.14 acceptance updated; new **P4.43** `NexPane.exe --purge-user-data` (S3 with S7) |
| Placeholders | Reworded `TODO P1.11` → `task(s) P1.11` in `eng\build.ps1`, `publish.ps1`, `installer.ps1` and `installer\NexPane.iss`; removed the 4 allowlist entries | `check-placeholders.ps1` exit 0 (0 allowlisted, 0 stale) |
| Build/test | — | Build 0 warnings / 0 errors; tests 80/80 |

TODO totals: 183 items: 9 VERIFIED_COMPLETE, 5 READY_FOR_QA, the rest PENDING.

## 4. Active work

| Item | Owner | Status | Notes |
|---|---|---|---|
| P1.02 uninstall purge mechanism | S1 | `[IN_PROGRESS]` (QA reject) | Replace `ExecAsOriginalUser` (unsupported at uninstall) with a supported mechanism, record in §21, update P4.43/P7.14 acceptance; then S6 re-runs |
| Phase 2 (branding/assets) | **S7** | `[PENDING]` | Start with P2.01 (logo SVGs), then P2.02 (`tools\NexPane.AssetGen`) |
| Phase 3 (core FS, indexer, search) | **S2** | After the QA gate (28 verified): `[READY_FOR_QA]` P3.16, P3.22, P3.31, P3.39. `[IN_PROGRESS]` (code done, awaiting the elevated run) P3.10, P3.11, P3.13, P3.14, P3.38 | Branch `phase3-core-search` (worktree `.worktrees\phase3`), clean tree. Next: the user runs `eng\run-elevated-tests.ps1` (elevated, AC power), then S6 re-verifies |

## 5. Active issues / risks
1. **Deviations from the original plan** (all recorded in ARCHITECTURE §21):
   - Microsoft.Testing.Platform runner, so the test command syntax changed.
   - `tools/NexPane.AssetGen` is deferred to P2.02.
   - `NexPane.IndexService.Tests` was added.
   - Layering is checked by parsing csproj files; NetArchTest type-level rules come in P3.37.
2. Every test project needs at least one test (Microsoft.Testing.Platform exit code 8 on zero tests).
3. FluentAssertions 7 `OnlyContain` fails on empty collections; use `NotContain(x => !predicate)`.
4. The EULA in LICENSE.txt was drafted without legal review; a lawyer should review it before public release.
5. NuGet audit warnings NU1901–NU1904 are not errors; QA treats them as release blockers (P7.04).
6. **Installer gaps before release (P7.08 / P7.09 / P7.14):**
   - The WebView2 bootstrapper is not in the `.iss` yet.
   - Release builds must compile without `-AllowMissingBranding`.
   - The service and verbs have never been executed.
   - Signing is untested because signtool is absent.
   - `usPostUninstall` still deletes only `{localappdata}\NexPane`; the designed `--purge-user-data` flow (P4.43 + P7.14) replaces it.
7. **Credential Locker limit:** Microsoft documents a ten-credentials-per-app limit. It was not enforced for an unpackaged process on this machine (15 stored), but P5.31 must surface `PasswordVault.Add` failures and test 25 connections.
8. New code must pass `eng\check-placeholders.ps1`, which runs in the default suite. Allowlist entries are added only by QA, each with a justification.
9. `eng\publish.ps1` rebuilds App and IndexService self-contained, so the next `eng\build.ps1` recompiles those two projects (expected).

## 6. Exact next commands (new PowerShell window)

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
Set-Location C:\Users\Ewais\Projects\NexPane
git status; git log --oneline -3
dotnet build NexPane.sln -c Release
dotnet test --solution NexPane.sln -c Release --filter-not-trait "Category=Elevated" --filter-not-trait "Category=Live"
powershell -NoProfile -ExecutionPolicy Bypass -File eng\check-placeholders.ps1               # exit 0 expected
# QA (S6) re-run for P1.07:
git check-ignore src/NexPane.Core/Models/VfsEntry.cs src/NexPane.Security/Secrets/x.cs src/NexPane.App/Packages/x.cs src/NexPane.Core/Logs/x.cs src/NexPane.Core/Log/x.cs tests/TestAssets/content/a.bak   # prints nothing, exit 1
# Then: Phase 2 → S7 (P2.01, P2.02), Phase 3 → S2 (P3.01, P3.03) in parallel.
```

## 7. Session log

### 2026-10-23 (e) — Subagent 2: Phase 3 QA rejects and P3.39
- Commits after the gate `f852016`:
  - `77b53bf` P3.31: license notices and §15.
  - `8e09726` P3.16: `RdcwWatcher` ownership handshake; 10k-watcher stress test.
  - `b884139` P3.22: per-query regex budget; live queries keep the abort.
  - `cab3609` P3.11 and P3.38: full size/mtime parity, system-volume budget tests; the runner logs UTC and refuses dirty trees.
  - `0e476c0` P3.14: journal ancestor rule and a bounded, invalidated cache.
  - `b995326` P3.13: identification-only pipe, SCM server verification, reset error code. Also: the service exe now hosts the pipe server, which it did not before. Elevated test: `sc create` plus a `runas /trustlevel` client.
  - `eb4f660` P3.39: 7 internal implementations behind Core interfaces, `AddShell`/`AddIndexing`/`AddSearch`.
- Found and fixed along the way:
  - `FileReferencePath` never stripped `\?\`.
  - `RunAsClient` left service threads impersonating at identification level, so `GetFileSecurity` failed with 1346; replaced by `PipeClientToken`.
- Default suite 756: 0 failed, 3 skipped. Scanner: 98 files, 0 violations. Build: 0 warnings.
- Needs the user's elevated run (`eng\run-elevated-tests.ps1`): P3.10, P3.11, P3.13, P3.14, P3.38.

### 2026-10-22 (S3-P3.34) — Subagent 3: ShellExecuteEx launch service
- Branch `p3-shellexec` (worktree `.worktrees\p3-shell`, from `phase3-core-search` @ f852016); code commit 9e65a1e.
- Added `IShellLauncher` (+ `ShellLaunchRequest`/`ShellLaunchResult`/`ShellLaunchStatus`), `Launch/ShellExecuteLauncher` (PIDL via `SHParseDisplayName`, `ShellExecuteExW` with `SEE_MASK_INVOKEIDLIST|NOASYNC|FLAG_LOG_USAGE|UNICODE`, optional `FLAG_NO_UI`), `Launch/ShellLaunchErrors` (GetLastError / HRESULT / `SE_ERR_*` → typed status), `Threading/ShellStaScheduler` (the §5 Shell STA pool, 2–4 pumping STA threads) and `AddShell(IServiceCollection, IConfiguration)`. Not wired into the UI (Phase 4).
- Tests: 46 new test cases in `tests/NexPane.Shell.Tests` (`Launch/`, `Threading/`); the integration tests register a unique per-user extension + ProgID with marker-writing verbs (never `.txt`) and delete it in `Dispose`. Shell.Tests 56 total: 55 passed, 1 documented skip (symlinks). Default suite 791: 788 passed, 0 failed, 3 skipped. Build 0 warnings / 0 errors. Scanner 98 files, exit 0.
- Deviations recorded in ARCHITECTURE §21 (typed result instead of `bool`; `LaunchAsync`; MAX_PATH fallback; DI abstractions packages in Shell).
### 2026-10-22 (P3.37) — Subagent 1 (Lead Architect & State Scribe), branch `p3-archrules`
- P3.37 → `[READY_FOR_QA]`. Added NetArchTest.Rules type-level rules in `tests/NexPane.Architecture.Tests/TypeLevelRulesTests.cs`:
  - R1: no WPF/WinForms below L3 (types, compiled references, `FrameworkReference`, shared build files);
  - R2: no Shell/WinRT/Win32/COM interop types and no P/Invoke in Core;
  - R3: no externally visible P/Invoke;
  - R4: Core-contract implementations internal (explicit test).
- Each rule was proven by a planted violation, then reverted.
- R4 finds 7 existing public implementations in Phase 3 code: `LocalFileSystemProvider`, `RdcwVfsWatcher`, `ContentStore`, `IndexStore`, `SearchEngine`, `QueryParser`, `QueryPrinter`. Production code was not changed; a follow-up for S2 is described in the P3.37 row.
- Build 0/0; Architecture.Tests 134 passed / 13 explicit skipped; default suite 786 passed, 0 failed, 16 skipped; placeholder scan exit 0.

### 2026-10-22 (QA-P3) — Subagent 6 (QA)
- Ran the Phase 3 gate P3.36 in `.worktrees\phase3`; 28 items verified, 6 rejected, P3.38 added; benchmarks and merge forecast in §3. Committed docs to `phase3-core-search` only.

### 2026-10-22 (d) — Subagent 2: MFT directory classification fix
- The elevated re-run at `762b552` (AC) passed the metafile parity check. It found another product bug: `M:\System Volume Information` was indexed as a file (FileNotFoundException in the size loop).
- Root cause: `MftReader` sent the `$STANDARD_INFORMATION` attributes. On NTFS these never contain FILE_ATTRIBUTE_DIRECTORY (0x10), because directories are marked by record header flag 0x0002 (and 0x10000000 in `$FILE_NAME`). The store derives the directory flag from `Attributes & 0x10`, so every MFT-enumerated directory became a file. The journal path was not affected (USN records and the by-id stat carry 0x10). The old directory fixture wrote 0x10 into `$SI`, which hid the bug.
- Fix: `MftReader.ToStat` sets 0x10 from the record header and clears the NTFS-internal 0x10000000 bit.
- New fixture `tests/TestAssets/mft/directory.bin` (as NTFS writes it: `$SI` 0x06, `$FILE_NAME` 0x10000006) and the unelevated test `Directory_without_the_directory_attribute_in_standard_information_is_indexed_as_a_directory` (parser → stat → IndexStore). The other fixtures regenerated byte-identical.
- The elevated test asserts `IsDirectory` parity for every entry against the filesystem, and replaces `BeEquivalentTo` on 10k-path sets (2m17s run) with set differences that print the first 20 differences.
- Default suite 745: 742 passed, 0 failed, 3 skipped. Scanner: 92 files, exit 0. Next: re-run `eng\run-elevated-tests.ps1`.

### 2026-10-22 (c) — Subagent 2: P3.24 fix, NTFS metafile exclusion
- P3.24: added `RefinementBuffer`, which keeps contiguous folded names per result partition; refinement is a vectorized scan and selects without copying.
  - Before: broad keystrokes about 2.9 ms p50 (unit chain, AC) / 1.9 ms (probe, battery).
  - After, on AC (BenchmarkDotNet `BroadRefinementBenchmarks`, base query in iteration setup): `in`→`inv` median 0.60 ms, `re`→`rep` 0.66 ms, `invoice_0`→`invoice_00` 0.14 ms; narrow keystroke 53 µs.
  - The unit test asserts p50 ≤ 1 ms and p95 ≤ 3 ms again (2× gate).
  - Cost: cold name-only queries now copy their matches' names, e.g. cold `invoice_00` 1.8–1.9 ms (was 0.8–1.4 ms) and cold `in` (167k results) 5.2–6.6 ms. Both remain within the cold budget (p95 ≤ 15 ms).
- The elevated run by the user found a product bug: NTFS metafiles (`$Quota`, …) were indexed. They are now excluded by record number and `$Extend` ancestry in enumeration and journal; in-process elevated mode applies the user's AccessCheck (walker parity). Unit-tested; the elevated test asserts parity with directory enumeration.
- 4 of 5 elevated tests had passed in that run; the 5th needs a re-run of `eng\run-elevated-tests.ps1` (added by the orchestrator, committed unchanged).
- Default suite 744: 741 passed, 0 failed, 3 skipped. Scanner: 92 files, exit 0.

### 2026-10-22 — Subagent 2 (Core Systems & Indexing Engine Engineer)
- Phase 3 on branch `phase3-core-search` (worktree `C:\Users\Ewais\Projects\NexPane\.worktrees\phase3`), 13 commits from `2017395` to the docs commit; not merged into main (the orchestrator merges).
- Delivered:
  - VfsPath and the VFS contracts; SQLite storage with migrations and settings.json.
  - The in-memory index (Core read model + Indexing writer, compaction, snapshot persistence).
  - The query language (parser, printer, evaluators, planner, parallel executor, live queries), tags, categories and dupe.
  - The content pipeline (queue, extractors, FTS5).
  - The local file system provider and RDCW watcher; the parallel walker and source selection.
  - NTFS parsing (USN, MFT), volume IOCTLs, AccessCheck, the Index Service pipe protocol/server/client and the network crawler.
  - The benchmarks project.
- Not run: the `[Elevated]` tests (USN/MFT VHDX, journal reset, service as admin, SMB share). Run them from an elevated terminal with `dotnet test --project tests/NexPane.Indexing.Tests -c Release --filter-trait "Category=Elevated"` (and the same for `tests/NexPane.IndexService.Tests`). Hyper-V is not needed; the tests use diskpart.
- Incident: early in the session a .NET `[IO.File]` call with a relative path wrote three empty files into the main checkout. They were deleted at once; the orchestrator confirmed main is clean. Rule: use absolute worktree paths only.
- The machine ran on battery for most measurements. Timing gates in the unit suite assert 2× the budget and skip only when exceeded on battery power.

### 2026-10-21 (f) — Subagent 6 (QA, Verification & Security Inspector)
- Re-ran P1.02, P1.07, P1.08, P1.13, P1.14 independently after 180b0c0; verdicts in §3. Checked the roster remap in TODO.md.
- Added `tests/NexPane.Architecture.Tests/GitIgnoreTests.cs` (28 cases; proven to fail on a re-added `models/` line).
- Found the `ExecAsOriginalUser` uninstall limitation by decompiling the locally installed Inno help (`hh.exe -decompile` into `C:\npqa_chm`; that folder could not be deleted by the agent because of a path guard — safe to delete manually).
- Build 0/0, tests 108/108, placeholder scan exit 0.

### 2026-10-21 (e) — Subagent 1 (Lead Architect & State Scribe)
- Applied the user-mandated roster numbering (by role) across TODO, ARCHITECTURE, HANDOFF and STATE.
- Fixed the QA rejections:
  - P1.07: `.gitignore` anchoring;
  - P1.02: 7 ARCHITECTURE defects, including the recorded Mermaid render, which found and fixed a real §6.4 parse error;
  - P1.08: full §22 traceability;
  - P1.13: notices.
- Decided P1.14 (Credential Locker for network, DPAPI/CredMan for AI) after an empirical PasswordVault test. Added P4.43.
- Reworded the `TODO P1.11` cross-references and emptied the allowlist.
- Results: build 0/0, tests 80/80, scanner exit 0. Commit message ends with the Co-Authored-By line.

### 2026-10-21 (d) — QA Inspector (orchestrator-assigned QA agent; QA role = S6 under the current numbering; veto)
- Re-ran every Phase 1 acceptance test independently:
  - build/test, and layering with planted violations;
  - all eng scripts, real runs and `-DryRun`, plus the app window check;
  - the SQL schema in SQLite with FTS5 (via a throwaway Microsoft.Data.Sqlite console project in `%TEMP%\npqa`);
  - a static Mermaid check, license/notices, and `.gitignore` probes.
- Implemented P1.12 (scanner, allowlist, 33 tests), verified with planted violations in temp trees only.
- Verdicts: P1.01, P1.03–P1.06 and P1.09–P1.12 VERIFIED; P1.02, P1.07, P1.08 and P1.13 REJECTED (defects inline in TODO.md). New items P1.14, P4.40–P4.42, P5.35, P5.36, P7.14. `dist\NexPane-Setup.exe` was rebuilt and not executed.

### 2026-10-21 (c) — Subagent 7 (Release & Installer Packaging Engineer)
- P1.11:
  - Wrote the eng pipeline scripts and `installer/NexPane.iss`.
  - Ran build → test → publish → installer end to end. Results: build 0/0, tests 47/47, publish 462 files / 196.2 MB, "signing skipped", ISCC warning-free.
  - Verified fail-fast: exit 7 propagates as 7; an ISCC error exits 2; missing branding exits 1 (script) or hits `#error` (`.iss`).
  - Fixed two Inno 6.7.3 compile issues: the `english.` prefix on custom messages, and the deprecated `RemoveBackslash`.
  - Added the §21 rows for the `[Code]` service handling and the `-AllowMissingBranding` / `-DryRun` behaviour.
- Orchestrator correction (commit `6e4bdbb`): the output is the unversioned `dist\NexPane-Setup.exe`; the version lives in the exe's ProductVersion resource.
- The WebView2 bootstrap is deferred to P7.08.

### 2026-10-21 (b) — Subagent 1 (Lead Architect & State Scribe)
- Executed P1.09 (repo, solution, test framework). Build 0/0, tests 47/47, app window verified. Commit `c6b4d4f`.
- Recorded user decisions D1–D5. Wrote LICENSE.txt and THIRD-PARTY-NOTICES.txt. Designed GPU Whisper and added P6.26–P6.30. Commit `0fe2b0a`.

### 2026-10-21 (a) — Subagent 1 (Lead Architect & State Scribe)
- Wrote ARCHITECTURE.md, TODO.md, STATE.md, HANDOFF.md, README.md and .gitignore.
