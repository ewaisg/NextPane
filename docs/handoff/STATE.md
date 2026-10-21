# NexPane — Current State

> Updated by every subagent at the end of every session. Newest entry is at the top of the Session Log.
> Last update: **2026-10-21 (k)** by **the orchestrator**: merged `phase2-branding` into `main` — **Phase 1 complete (14/14), Phase 2 complete (P2.01–P2.09)**.
> Roster (user-mandated numbering): S1 Architect & State Scribe · S2 Core Systems & Indexing Engine · S3 Native Windows Shell & OS Integration · S4 AI & Semantic Pipeline · S5 UI/UX & Workspace · **S6 QA, Verification & Security Inspector (veto)** · S7 Release & Installer Packaging (incl. icons/assets). Older log entries below that say "S7 QA role" refer to the QA role, which is now **S6**.

## 1. Snapshot

| Item | Value |
|---|---|
| Current phase | **Phase 1 — COMPLETE: 14/14 `[VERIFIED_COMPLETE]`** (S6, after 5f64ea8). **Phase 2 — COMPLETE: P2.01–P2.09 `[VERIFIED_COMPLETE]`** (S6 gate; merged into `main` by the orchestrator). Phase 3 (S2) in progress in worktree `.worktrees\phase3`, branch `phase3-core-search` |
| Placeholder scanner (P1.12) | `eng\check-placeholders.ps1` → **26 files, 0 violations, 0 allowlisted, 0 stale, exit 0**. The four `TODO P1.11` cross-references now read "task(s) P1.11"; `eng\placeholder-allowlist.json` has `"entries": []` |
| Toolchain | .NET SDK 10.0.402, Git 2.55.0, Inno Setup 6.7.3 (per-user: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) |
| Pipeline (P1.11) | `eng\build.ps1` → `eng\publish.ps1` → `eng\installer.ps1 -AllowMissingBranding` → unversioned `dist\NexPane-Setup.exe` (55.35 MB, ProductVersion 0.1.0; never executed) |
| Git | Repo on `main`, no remote. Recent commits: `c6b4d4f` scaffold, `0fe2b0a` decisions, `92abcb9` P1.11, `6e4bdbb` unversioned installer, `22fb2ba` QA audit, then the S1 QA-fix commit (see `git log -1`). Identity: `NexPane Dev <ewaisg@gmail.com>` |
| Solution | `NexPane.sln`: 15 `src` projects, 16 test projects, 1 benchmark runner |
| Build | `dotnet build NexPane.sln -c Release` → **Build succeeded. 0 Warning(s) 0 Error(s)** |
| Tests | Default filter → **total: 185, failed: 0, succeeded: 185** (post-merge of Phase 2, 2026-10-21 k) |
| App | `NexPane.exe` opens a 1280×800 window titled "NexPane" and exits 0 on `CloseMainWindow` |
| Feature code | Phase 2 branding (AssetGen, icons, tray interop, theme tokens). Phase 3 code lives on branch `phase3-core-search` until merged |
| User decisions | D1–D5 (ARCHITECTURE §20) + P1.14 (§21): network credentials in the Windows Credential Locker; AI keys via DPAPI/Credential Manager |
| Blockers | None |

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

**2026-10-21 (k), orchestrator: merged `phase2-branding` into `main`**
- Doc conflicts resolved: main's Phase 1 text kept; the branch's Phase 2 rows/notes taken. Code and assets merged cleanly. Post-merge `eng\build.ps1` and `eng\check-placeholders.ps1` re-run (results in the merge commit message).

**2026-10-21 (QA-P2), S6: Phase 2 gate (P2.09) — all Phase 2 items VERIFIED**
- Worked inside `.worktrees\phase2` only; scratch output in the S6 scratchpad and `%TEMP%\npqa2`.
- `eng\build.ps1` exit 0 (0 warnings, 0 errors, 185/185, 0 skipped); `eng\check-placeholders.ps1` exit 0 (36 files); `eng\publish.ps1` exit 0 (465 files; no Svg.*/Skia/HarfBuzz/ExCSS in the output, so MS-PL Svg.Custom stays build-time only); `eng\installer.ps1` **without** `-AllowMissingBranding` exit 0, warning-free, `dist\NexPane-Setup.exe` 55,526,088 bytes (not executed).
- AssetGen out-of-process twice: 23 files, identical SHA-256 between runs and with the committed assets.
- Independent native checks (QA console tool): NexPane.ico 9 layers decode via `CreateIconFromResourceEx` and `LoadImage` at exact size (256 = PNG); tray icons 6 layers, glyph contrast 9:1 on both taskbar themes; 10 BMPs 24 bpp exact size; 9 PNGs RGBA; exe/service/setup RT_GROUP_ICON = 9 layers; runtime `WM_GETICON` 16x16 / 32x32; theme token contrasts recomputed.
- **Manual icon review (P2.04/P2.09):** `IShellItemImageFactory` renders of the published `NexPane.exe` at 16/32/48/96/256 px (Explorer small → Extra-large) inspected as PNG: correct NexPane mark (white "N" with spark on a blue-indigo rounded tile), crisp at 256, recognisable at 16, transparent corners. Wizard bitmap (164x314) shows the logo lockup on navy, legible.
- Advisory: `tray.ico` (full-colour tile) is not a taskbar-contrast glyph (2.6:1 dark, 1.0:1 light worst pixel); `VariantForCurrentTaskbar()` never selects it — Phase 4 should not show it in the notification area.

**2026-10-21 (j), S6 QA: P1.02 → `[VERIFIED_COMPLETE]`; Phase 1 complete**
- The 4 consistency defects from e9e59ab are fixed in 5f64ea8 (§13.2 GPU row, P6.30, P7.08, single HKCU `{AppId}_is1` rule in §19.3/§21/P4.43(d)/P7.14(3)(4) + cases (f)/(g)). Rule premise confirmed in the installed Inno 6.7.3 help (*Non Administrative Install Mode*: with admin rights available, a non-admin install's uninstaller "will always be marked as requiring administrative privileges").
- Grep of docs/README/`.iss` for uninstall/remove-data/runtimes/`ExecAsOriginalUser`/`IsAdminInstallMode`: no remaining contradiction (history entries and "current `.iss` state" notes are accurate).
- `eng\build.ps1` exit 0: 0 warnings, 0 errors, **108/108** tests; `eng\check-placeholders.ps1` exit 0 (26 files, 0 violations, 0 allowlisted).


**2026-10-21 (i), S1: P1.02 consistency fixes (after S6 e9e59ab)**
1. **ARCHITECTURE §13.2 GPU row.** Packs are removed via Settings › Transcription, via "Remove my NexPane data…"/`--purge-user-data` (P4.43), and by the uninstaller only for per-user installs on Yes. Admin-mode uninstalls keep them.
2. **P6.30 acceptance.** Now covers (a) the in-app purge, (b) a per-user uninstall answering Yes, and (c) an admin-mode uninstall that keeps the packs.
3. **P7.08 wording.** The purge prompt appears only when the uninstall key is under HKCU.
4. **One rule.** Adopted S6's recommendation: the `…\Uninstall\{23676795-F60E-4EAF-95C6-C4D81BAD01C1}_is1` key under the running account's HKCU **alone** decides. `IsAdminInstallMode` is not consulted. An elevated same-account per-user uninstall purges on Yes; without the key (admin mode, or another account) the uninstaller never purges.
   - Stated identically in §19.3 (rewritten as a single-rule bullet), the §21 "P1.02 rework" row, P4.43 (d) and P7.14 (3)/(4).
   - New P7.14 cases: (f) elevated per-user uninstall by the same admin account → purge on Yes; (g) uninstaller started by a different account → no prompt, data kept.
- §18 installer test row aligned.
- Grep of `docs\` for uninstall + user data / purge / remove-data / runtimes: no remaining contradictions; historical log entries were left as history.
- `check-placeholders.ps1` exit 0. Only the four handoff docs were changed and staged. `.worktrees\` untouched.

**2026-10-21 (h), S6 QA: P1.02 re-run after 61f0577 → REJECTED**
- Inno 6.7.3 help decompiled into the S6 scratchpad (8.3 path); all four quotations in §19.3/§21 confirmed verbatim (ExecAsOriginalUser not supported at uninstall; `Exec` uses Setup/Uninstall credentials; *Non Administrative Install Mode* "user-level files … handled by the application itself"; `IsAdminInstallMode` True when Uninstall runs with admin privileges). HKCU `{AppId}_is1` discriminator matches Inno key naming. §22 E3, P4.43, P7.14 consistent on the main flow.
- Defects (S1): (1) ARCHITECTURE §13.2 GPU row: packs "deleted by the uninstaller's remove user data option"; (2) P6.30 acceptance "uninstall with remove-data → folder gone"; (3) P7.08 title "uninstall data prompt incl. `runtimes\`"; (4) §19.3 says any elevated uninstall (`IsAdminInstallMode` True) never purges, while P7.14 (3) adds "and key not in HKCU" and P4.43 (d) expects elevated same-account per-user uninstalls to purge — pick one rule (recommend the HKCU key check) and add the P7.14 case.
- No code changed; only TODO/STATE/HANDOFF staged. `.worktrees\` untouched.


**2026-10-21 (g), S1: P1.02 rework — uninstall-time user-data removal**
- **Research.** I decompiled the installed Inno Setup 6.7.3 help (`ISetup.chm`) into the S1 scratchpad (via its 8.3 short path, because `hh.exe` fails on long paths) and checked the APIs. The copy was deleted afterwards.
  - `ExecAsOriginalUser`: "not supported at uninstall time".
  - `Exec`: runs "using the same credentials as Setup/Uninstall"; no uninstall restriction.
  - *Non Administrative Install Mode*: in administrative install mode, "user-level files and settings must be handled by the application itself, and never in an administrative install mode installer".
  - `IsAdminInstallMode` is True "if Uninstall is running with administrative privileges".
- **Mechanism chosen** (options (a) + (c)), in ARCHITECTURE §19.3 and §21 "P1.02 rework" (the old row is struck through):
  1. Per-user data (both NexPane folders, Credential Manager `NexPane:*`, Credential Locker `NexPane:net`) is removed **by the app, per user**: Settings › Privacy › "Remove my NexPane data…" and `NexPane.exe --purge-user-data` (P4.43).
  2. The uninstaller always removes machine-wide items.
  3. In admin mode it never touches per-user data; it tells the user what was kept and how to remove it.
  4. For **per-user installs** (its uninstall key is in the running account's HKCU) it asks, default No, and on Yes runs plain `Exec` of `{app}\NexPane.exe --purge-user-data` at `usUninstall`. Silent uninstalls never purge.
  5. Rejected, with reasons in §21: HKCU RunOnce cleanup, and a WTS session-user comparison.
- **TODO:** P4.43 and P7.14 acceptance rewritten (admin-mode keeps data with a message; per-user Yes/No/silent cases; `.iss` grep for no `ExecAsOriginalUser` or user-folder `DelTree`). §22 E3 updated. P1.02 → `[READY_FOR_QA]`.
- **Checks:** build 0 warnings / 0 errors; tests **108/108**; `check-placeholders.ps1` exit 0. Phase 3 worktree not touched.

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

> S6 (2026-10-21 j): Phase 1 closed. Next QA gates: P2.09 (Phase 2, S7 worktree `.worktrees\phase2`) and P3.36 (Phase 3, S2 worktree `.worktrees\phase3`) when their items reach `[READY_FOR_QA]`; every merge to `main` must keep `eng\build.ps1` and `eng\check-placeholders.ps1` green.

> S6 (2026-10-21 h): next for S1 — fix the 4 P1.02 consistency defects listed in TODO.md P1.02 / §3 above, set `[READY_FOR_QA]`, S6 re-runs (doc-only check).

| Item | Owner | Status | Notes |
|---|---|---|---|
| Phase 1 | — | `[VERIFIED_COMPLETE]` 14/14 | Closed |
| Phase 2 (branding/assets) | S7 / S6 | `[VERIFIED_COMPLETE]` P2.01–P2.09 | Merged into `main` |
| Phase 3 (core FS, indexer, search) | **S2** | `[IN_PROGRESS]` | Worktree `.worktrees\phase3`, branch `phase3-core-search`; S6 gate P3.36 there before the orchestrator merges |

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
   - `usPostUninstall` still deletes `{localappdata}\NexPane`, even in admin mode. P7.14 removes this and implements §19.3: admin mode keeps per-user data and shows a message; per-user installs offer a purge via `Exec` of `--purge-user-data`.
   - Users of admin-mode installs must remove their data in-app (P4.43) **before** uninstalling; afterwards it can only be removed manually (the two folders and the `NexPane:` entries in Credential Manager). The uninstaller's final message says so.
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
# QA (S6) re-run for P1.02: review ARCHITECTURE §19.3 "Uninstall ... per-user data" and §21 "P1.02 rework"; confirm no design relies on ExecAsOriginalUser:
Select-String -Path docs\handoff\ARCHITECTURE.md -Pattern 'ExecAsOriginalUser'   # only the documented-limitation sentence in §19.3 and the struck-through §21 row
# Phases 1-2 closed. Phase 3 → S2 in .worktrees\phase3 (branch phase3-core-search) — do not edit that worktree from main.
```

## 7. Session log

### 2026-10-21 (k) — Orchestrator
- Merged `phase2-branding` into `main`; resolved doc conflicts (main's Phase 1 text + the branch's Phase 2 text).

### 2026-10-21 (QA-P2) — Subagent 6 (QA)
- Ran the Phase 2 gate P2.09 in `.worktrees\phase2`; all P2 items verified (evidence in TODO.md and §3). Committed docs to `phase2-branding` only.

### 2026-10-21 (h2) — Subagent 7 (Release & Installer Packaging Engineer), branch `phase2-branding`
- **P2.01–P2.08 → `[READY_FOR_QA]`** (evidence inline in TODO.md).
- **Design.** Rounded-square tile with a sky→indigo gradient (`#1FB6FF`→`#4338F5`), a white "N" made of two vertically offset panes joined by a translucent diagonal, and an amber spark (`#FFC24B`) at the top left.
  - `nexpane-mark.svg` is the 16-unit-grid small-size variant.
  - `nexpane-logo.svg` is a square stacked lockup: the detailed mark above a geometric-path wordmark in `#4D74FF`.
- **Tool.** `tools\NexPane.AssetGen` (SkiaSharp 4.148.0 + Svg.Skia 5.2.3) writes 23 files into `assets\icons` and `assets\installer`; `--preview <png>` writes a review sheet.
- **Wiring.** ApplicationIcon on App and IndexService; `MainWindow.Icon`; `NexPane.Shell.Tray.TrayIcon` (Shell_NotifyIconW); `Themes\Tokens.xaml`.
- **Tests.** New `tests\NexPane.AssetGen.Tests` (50), plus UITests (FlaUI, icon group, theme contrast), Shell.Tests (tray) and IndexService.Tests (icon). Default suite **185/185**.
- **Pipeline without `-AllowMissingBranding`.** build → publish (465 files) → installer → `dist\NexPane-Setup.exe` 55,520,871 bytes, never executed. The setup exe, `NexPane.exe` and `NexPane.IndexService.exe` icon groups each contain 16/20/24/32/40/48/64/128/256.
- **Merge notes.** Small edits to `NexPane.Shell.csproj` (`AllowUnsafeBlocks`, `InternalsVisibleTo`), `Directory.Packages.props`, `NexPane.sln` and `THIRD-PARTY-NOTICES.txt`. Phase 3 may touch the same files.

### 2026-10-21 (j) — Subagent 6 (QA)
- Re-verified P1.02 after 5f64ea8 (diff review, Inno help check in the S6 scratchpad, doc grep, full build + default suite + placeholder scan). VERIFIED → Phase 1 complete (14/14). Only TODO/STATE/HANDOFF changed; `.worktrees\` untouched.

### 2026-10-21 (i) — Subagent 1 (Lead Architect & State Scribe)
- Fixed S6's 4 P1.02 consistency defects; adopted the single HKCU `{AppId}_is1` rule in §19.3/§21/P4.43/P7.14; added P7.14 cases (f)/(g); aligned §13.2, §18, P6.30, P7.08. Placeholder scan exit 0. Docs-only commit.

### 2026-10-21 (h) — Subagent 6 (QA)
- Re-verified P1.02 after S1 commit 61f0577 against the locally installed Inno 6.7.3 help (decompiled in the S6 scratchpad). Mechanism correct; rejected for 4 doc/acceptance inconsistencies (TODO.md P1.02). Worked only in the main checkout; staged only docs.

### 2026-10-21 (g) — Subagent 1 (Lead Architect & State Scribe)
- Reworked the P1.02 uninstall user-data mechanism after S6's rejection, verifying every Inno API against the installed 6.7.3 help.
- Chose app-owned per-user removal (P4.43) plus an uninstaller purge only for per-user installs via plain `Exec`. Recorded in §19.3/§21; P4.43/P7.14 acceptance and §22 E3 updated.
- Build 0/0, tests 108/108, scan exit 0. Scratchpad help copy deleted. Did not touch `.worktrees\phase3`.

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
