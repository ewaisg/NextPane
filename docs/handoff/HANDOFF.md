# NexPane — Session Handoff Briefing

> Read this first in every new session, then `STATE.md` (exact current state), then the relevant phase of `TODO.md`, then the referenced sections of `ARCHITECTURE.md`.
> Last update: 2026-10-22 by Subagent 2 (Core Systems & Indexing): Phase 3 files, gotchas 24–32 (branch `phase3-core-search`). Previously: 2026-10-21 (f) by Subagent 6 (QA): re-verification after S1 commit 180b0c0 — 13/14 Phase 1 items verified; P1.02 rejected (uninstall purge mechanism).

## 1. Executive summary

NexPane is a production-grade, AI-powered Windows file explorer: C# / **.NET 10 LTS** / **WPF**, x64, unpackaged, Windows 10 1809+ and Windows 11, Inno Setup 6 installer, **proprietary license, shipped unsigned**.

Phase 1 is complete up to QA. The toolchain is installed. The git repository (`main`) and `NexPane.sln` exist, with 15 `src` projects layered per ARCHITECTURE §4, 16 xUnit v3 test projects and a benchmarks runner. The solution builds with 0 warnings and 0 errors, and 108 of 108 tests pass with the default filter. The app opens an empty window titled "NexPane". The IndexService is a Windows-service host skeleton that starts and stops cleanly. No feature code exists yet.

The build → test → publish → installer pipeline (P1.11) is scripted in `eng\` and verified end-to-end. The placeholder scanner (P1.12) is in place and runs inside the default test suite (default suite now 108 tests, incl. QA's `GitIgnoreTests`).

**Phase 1 status:**
- `[VERIFIED_COMPLETE]` (S6, 2026-10-21): P1.01, P1.03–P1.14 except P1.02 — 13 of 14.
- `[IN_PROGRESS]`: **P1.02** — §19.3/§21 have the uninstaller run `NexPane.exe --purge-user-data` via Inno `ExecAsOriginalUser`, which Inno does not support at uninstall time (Inno help, ExecAsOriginalUser › Remarks). S1 must choose a supported mechanism and update P4.43/P7.14.
- Phase 1 is therefore not formally complete, but the open defect only affects uninstall design (Phase 4/7).

Phase 2 (S7, branding) and Phase 3 (S2, core systems) can start in parallel now.

## 2. Files currently being worked on

| File / area | State |
|---|---|
| `docs/handoff/*.md`, `README.md`, `.gitignore` | Complete, `[READY_FOR_QA]` |
| `LICENSE.txt` (proprietary EULA), `THIRD-PARTY-NOTICES.txt` | Complete, `[READY_FOR_QA]` (P1.13); full license texts are appended at P7.06 |
| `NexPane.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes` | Complete, `[READY_FOR_QA]` (P1.09) |
| `src/*` | Project shells only (no code), except `NexPane.App` (MainWindow) and `NexPane.IndexService` (`Program.BuildHost`) |
| `tests/NexPane.Architecture.Tests` | Layering + convention tests, `[READY_FOR_QA]` (P1.10) |
| `tests/Shared/ModuleAssembly.cs` | Shared helper linked into every `*Tests` project (x64 / target framework assertions) |
| `eng/common.ps1`, `eng/build.ps1`, `eng/publish.ps1`, `eng/installer.ps1`, `installer/NexPane.iss` | Complete, `[READY_FOR_QA]` (P1.11); pipeline verified end-to-end → `dist\NexPane-Setup.exe` (52.8 MB). The `.iss` still needs the WebView2 bootstrapper and real branding in P7.08 |
| `eng/check-placeholders.ps1`, `eng/placeholder-allowlist.json`, `tests/NexPane.Architecture.Tests/PlaceholderScannerTests.cs` | `[VERIFIED_COMPLETE]` (P1.12). QA owns the allowlist |
| `.gitignore`, `THIRD-PARTY-NOTICES.txt`, ARCHITECTURE §22 / P1.14 sections | `[VERIFIED_COMPLETE]` (P1.07, P1.13, P1.08, P1.14). `.gitignore` is guarded by `tests/NexPane.Architecture.Tests/GitIgnoreTests.cs` |
| `ARCHITECTURE.md` §19.3 + §21 (uninstall user-data purge) | `[IN_PROGRESS]` — P1.02 QA reject: `ExecAsOriginalUser` is unsupported at uninstall time |
| `eng/build.ps1`, `eng/publish.ps1`, `eng/installer.ps1`, `installer/NexPane.iss`, `eng/placeholder-allowlist.json` | Cross-references reworded to "task(s) P1.11"; the allowlist is empty; scanner exit 0 |
| Phase 3 (S2, branch `phase3-core-search`) | `src/NexPane.Core/{Vfs,Indexing,Search}` (VfsPath, VFS contracts, index read model, query AST, search contracts); `src/NexPane.Storage` (SQLite factory, migrations, single writer, settings.json); `src/NexPane.Indexing/{Store,Sources,Content,Ntfs,Service}`; `src/NexPane.Search/{Language,Execution,Live,Tags,Categories}`; `src/NexPane.Shell/{FileSystem,Content,Security}`; `src/NexPane.IndexService/IndexPipeServer.cs`; `tests/NexPane.Benchmarks` (+README); fixtures `tests/TestAssets/{content,usn,mft}`. 27 items `[READY_FOR_QA]`; P3.10–P3.14 and P3.18 wait for their `[Elevated]` runs; P3.24 p50 open |
| Next up | S1: fix P1.02 (purge mechanism) → S6 re-run. In parallel: S7 Phase 2 (P2.01, P2.02), S2 Phase 3 (P3.01, P3.03) |

## 3. Environment setup

Installed on this machine: .NET SDK **10.0.402**, Git **2.55.0**, Inno Setup 6 (**per-user**, `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`).

```powershell
# Fresh machine only (run from an elevated PowerShell for the installs):
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
winget install --id JRSoftware.InnoSetup    -e --accept-source-agreements --accept-package-agreements
winget install --id Git.Git                 -e --accept-source-agreements --accept-package-agreements

# EVERY new shell (tool shells do not pick up PATH changes made by winget):
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')

# Verify
dotnet --list-sdks                 # expect 10.0.402 (global.json rolls forward to later 10.0 feature bands)
git --version
# ISCC.exe is not on PATH. Per-user install location is probed first:
$Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
```

Optional for tests: Windows Sandbox (installer tests), the Hyper-V PowerShell module (`New-VHD` for elevated USN tests), Developer Mode (unprivileged symlink tests), the en-US OCR language capability (`Get-WindowsCapability -Online -Name "Language.OCR*"`), and an NVIDIA GPU with a CUDA 12-capable driver for GPU Whisper tests (`NEXPANE_IT_WHISPER_GPU=1`).

The repo-local git identity is `NexPane Dev <ewaisg@gmail.com>`. There is no remote; do not push.

## 4. Repository layout and how to add a project

Initialization is done (commit `c6b4d4f`). The repo was scaffolded by hand rather than from templates, so it contains no template boilerplate. Rules for adding a project:

1. Create `src/NexPane.<Name>/NexPane.<Name>.csproj` with only `RootNamespace` and the allowed `ProjectReference`s. The TFM (`net10.0-windows10.0.19041.0`), x64, nullable, warnings-as-errors and analyzers all come from `Directory.Build.props`. Only `NexPane.Core` overrides the TFM, to `net10.0`.
2. Add any NuGet package with `dotnet add <csproj> package <Id>`. Central package management writes the version into `Directory.Packages.props`; never put `Version=` in a csproj (an architecture test enforces this).
3. Classify every new `src` project in `tests/NexPane.Architecture.Tests/LayeringRules.cs` and in ARCHITECTURE §3/§4. Unclassified projects fail `Every_layer_project_exists_under_src`.
4. Test projects must be named `*Tests`. `tests/Directory.Build.props` then makes them xUnit v3 MTP executables with FluentAssertions and the shared helper. Each test project must contain at least one test, because Microsoft.Testing.Platform fails an assembly with zero tests (exit code 8).
5. Add the project to the solution: `dotnet sln NexPane.sln add <csproj> --solution-folder src|tests|tools`.
6. `tools/NexPane.AssetGen` does not exist yet; S7 (Release & Installer Packaging, which owns branding) creates it in P2.02.

## 5. Build, test, publish, installer commands

```powershell
# Build (verified: 0 Warning(s), 0 Error(s))
dotnet build NexPane.sln -c Release

# Test. global.json selects Microsoft.Testing.Platform, so use --solution and xUnit v3 trait filters.
# Placeholder scanner (P1.12; also runs inside the default suite) — exit 0 clean, 1 violations, 2 allowlist error:
powershell -NoProfile -ExecutionPolicy Bypass -File eng\check-placeholders.ps1
# Default CI filter (Elevated and Live suites are opt-in) — verified 80/80 passing (QA, 2026-10-21):
dotnet test --solution NexPane.sln -c Release --filter-not-trait "Category=Elevated" --filter-not-trait "Category=Live"
# Elevated suite (from an elevated terminal):
dotnet test --solution NexPane.sln -c Release --filter-trait "Category=Elevated"
# Single project:
dotnet test --project tests/NexPane.Architecture.Tests -c Release
# Benchmarks
dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *

# Publish (self-contained, ReadyToRun, NOT single-file, NOT trimmed)
dotnet publish src/NexPane.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o build/publish/win-x64
dotnet publish src/NexPane.IndexService -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o build/publish/win-x64

# Branding assets (Phase 2, after P2.02 creates the tool)
dotnet run -c Release --project tools/NexPane.AssetGen -- --svg assets/brand/nexpane-logo.svg --mark assets/brand/nexpane-mark.svg --out assets

# Installer → dist\NexPane-Setup.exe (version = <Version> in Directory.Build.props)
& $Iscc /DAppVersion=0.1.0 /DPublishDir=C:\Users\Ewais\Projects\NexPane\build\publish\win-x64 installer\NexPane.iss
```

Build outputs: `NexPane.exe` is in `src/NexPane.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/` (the App's `AssemblyName` is `NexPane`).

**Pipeline scripts (P1.11) — use these.** Each one stops at the first non-zero exit code, prints `ERROR: ...` and exits with that code; `-DryRun` prints the exact commands without running them.

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
Set-Location C:\Users\Ewais\Projects\NexPane
powershell -NoProfile -ExecutionPolicy Bypass -File eng\build.ps1       # restore, build Release, default test suite  [-SkipTests] [-DryRun]
powershell -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1     # cleans + publishes App and IndexService into build\publish\win-x64  [-DryRun]
powershell -NoProfile -ExecutionPolicy Bypass -File eng\installer.ps1   # ISCC → dist\NexPane-Setup.exe  [-DryRun]
# Before Phase 2 delivers the branding assets, the installer step fails on purpose. For pipeline verification ONLY:
powershell -NoProfile -ExecutionPolicy Bypass -File eng\installer.ps1 -AllowMissingBranding
```

- `-AllowMissingBranding` passes `/DNoBranding` to ISCC, which leaves out `SetupIconFile`, `WizardImageFile` and `WizardSmallImageFile`. It exists only to verify the pipeline before Phase 2. **Phase 7 release builds must not use it.** Without the switch, missing `assets\icons\NexPane.ico` or `assets\installer\wizard-*.bmp` / `wizard-small-*.bmp` fail both `installer.ps1` and the `.iss` (`#error`).
- `installer.ps1` treats any ISCC `Warning:` line as an error, because P7.08 requires a warning-free compile.
- **Code signing is an optional step that is skipped by default.** `publish.ps1` (both exes) and `installer.ps1` (setup exe) sign only when `NEXPANE_SIGN_CERT_THUMBPRINT` is set; `NEXPANE_SIGN_TIMESTAMP_URL` is optional. Otherwise they print "signing skipped (NEXPANE_SIGN_CERT_THUMBPRINT not set)". v1 ships unsigned.
- `build/` and `dist/` are git-ignored output folders. **Never run `dist\NexPane-Setup.exe` on the dev machine;** installer testing happens in Windows Sandbox (P7.09).

## 6. Architectural gotchas (read before writing code)

1. **COM apartments.** Shell COM objects (IShellFolder, IContextMenu, IFileOperation, IPropertyStore, IShellItemImageFactory, IFilter, in-proc preview handlers) must be created, used and released on the **same STA thread**, with a message pump. Use the UI thread (context menus only) or `ShellStaScheduler`. Never touch them from ThreadPool threads.
2. **IContextMenu2/3 message forwarding.** While `TrackPopupMenuEx` is open, add an `HwndSource.AddHook` on the owner window and forward `WM_INITMENUPOPUP`, `WM_DRAWITEM`, `WM_MEASUREITEM`, `WM_MENUCHAR` (and `WM_MENUSELECT`) to `IContextMenu3.HandleMenuMsg2` (or `IContextMenu2.HandleMenuMsg`). Without this, "Send to", "Open with" and "New" are empty.
3. **Preview handlers run out-of-proc.** Create them with `CLSCTX_LOCAL_SERVER` so they load in the `prevhost.exe` surrogate per their AppID; this is how Explorer isolates them. Fall back to `CLSCTX_INPROC_SERVER` on a Shell STA thread only together with the crash sentinel and blocklist. Host them in an `HwndHost` child HWND; call `SetRect` on resize and `Unload` on selection change. **Airspace:** WPF cannot draw anything over that HWND.
4. **PerMonitorV2 DPI.** `src/NexPane.App/app.manifest` already declares PerMonitorV2, `longPathAware`, Common Controls v6, `asInvoker` and Windows 10 `supportedOS`. Do not remove these.
5. **Never block the dispatcher.** No synchronous I/O, COM-to-STA waits, or `.Result`/`.Wait()` on the UI thread. Background work flows through `Channel<T>` to `DispatcherCoalescer` (≤ 10 UI batches/s). The stall watchdog logs anything over 100 ms.
6. **Long paths.** Use `\\?\` / `\\?\UNC\` prefixes for Win32 file APIs (.NET does this automatically). Shell APIs (PIDLs, IContextMenu, ShellExecuteEx, preview handlers) are limited to MAX_PATH; fall back to the built-in menus and operations with a notice.
7. **ShellExecuteEx** must run on an STA with `SEE_MASK_NOASYNC` (otherwise DDE-based launches can be dropped when the calling thread exits), plus `SEE_MASK_INVOKEIDLIST` for PIDL-based items.
8. **USN/MFT needs admin.** The app must run unelevated, because UIPI breaks drag/drop with Explorer when it is elevated. Fast NTFS indexing goes through `NexPane.IndexService` (service `NexPaneIndex`, LocalSystem) over `\\.\pipe\NexPane.Index.v1`. The service filters results with `AccessCheck` against the client token. The installer task is **checked by default** in admin installs and shows a privacy note.
9. **ReadDirectoryChangesW buffers.** The maximum over SMB is 64 KB. Treat `ERROR_NOTIFY_ENUM_DIR` and zero-byte completions as an overflow and rewalk the subtree.
10. **Single writer** for the in-memory index and for each SQLite DB. Readers use immutable generations (index) and WAL (SQLite).
11. **Regex from users** uses `RegexOptions.NonBacktracking`; fall back to backtracking only with a timeout.
12. **Secrets** go only through `ICredentialStoreProvider` (decision P1.14, ARCHITECTURE §10.3):
    - **Network credentials** → `For(CredentialArea.Network)` = **Windows Credential Locker** (`Windows.Security.Credentials.PasswordVault`, resource `NexPane:net`, userName = connection_id).
    - **AI keys** → `For(CredentialArea.Ai)` = **Credential Manager** generic credentials (`NexPane:ai:<providerId>:<field>`; blobs over 2,560 bytes overflow to DPAPI files).
    - Never put secrets in settings.json, app.db, logs or URLs.
    - Never swallow a `PasswordVault.Add` exception (Microsoft documents a per-app limit; it was not enforced for unpackaged apps in a test on 2026-10-21).
    - Tests use test-only resource/target prefixes and clean up. The Gemini key goes in the `x-goog-api-key` header, never in a query string.
13. **No trimming or single-file publish.** Do not enable `PublishTrimmed` or `PublishSingleFile`; an architecture test checks this.
14. **FluentAssertions** is pinned to `7.2.0` (v8+ is commercially licensed); a test checks the pin. In v7, `OnlyContain` **fails on empty collections**, so use `NotContain(x => !predicate)` for "all items satisfy" checks.
15. **Archives:** never offer RAR creation, always apply zip-slip checks, and extract to a per-user ACL'd temp folder.
16. **Windows 11 context menu:** registry verbs appear under "Show more options". This is final: a top-level Windows 11 entry is out of scope for v1 (decided, ARCHITECTURE R6).
17. **Anthropic provider:** there is no embeddings API (semantic search needs another provider). Handle `stop_reason: "refusal"`, discover models through the Models API, and use `claude-opus-5-5` as the preset default model.
18. **Microsoft.Testing.Platform:** `dotnet test` needs `--solution`/`--project`. VSTest-style `--filter "Category!=X"` does not work; use `--filter-trait` / `--filter-not-trait`.
19. **Analyzers:** `AnalysisLevel=latest-recommended` with warnings as errors. CA2007 (`ConfigureAwait`) is enforced in libraries but disabled in `NexPane.App` and in tests. Executable types should be `internal`; WPF XAML uses `x:ClassModifier="internal"`.
20. **GPU Whisper** runtimes (CUDA and Vulkan) are optional downloads into `%LOCALAPPDATA%\NexPane\runtimes\`. They are never bundled in the installer, and loading must always fall back to CPU without crashing (ARCHITECTURE §13.2).
21. **Inno Setup 6.7.3** is installed. ISPP `RemoveBackslash` is deprecated (it causes a warning, which fails `eng/installer.ps1`), so use `RemoveBackslashUnlessRoot`. Language-specific custom messages use a dot (`english.Name=`), not a colon. ISCC writes compile errors to stderr. Index Service setup lives in the `.iss` `[Code]` section (ARCHITECTURE §21), not in `[Run]`.
22. **`.gitignore` rule (P1.07 fix):** the repo has `core.ignorecase=true`, so every directory pattern must be anchored (`/models/`) or scoped to a project folder (`/src/*/[Bb]in/`). Never add an unanchored folder pattern. `tests/TestAssets/**` is always re-included. Check new patterns with `git check-ignore -v <path>` and `git status --ignored`. With `-v`, a negation match (`!…`) also exits 0, so use `git check-ignore -q` (exit 1 = not ignored) to test. Long scratch paths (> 260 chars, e.g. the agent scratchpad) make `dotnet` fail with "The directory name is invalid"; use a short `%TEMP%\...` folder for throwaway projects.
23. **Inno uninstaller limits (QA, P1.02):** `ExecAsOriginalUser`/`ShellExecAsOriginalUser` and the `runasoriginaluser` flag are **not supported at uninstall time** (Inno help, ExecAsOriginalUser › Remarks); the elevated uninstaller's `{localappdata}`/`{userappdata}` may be the admin's profile. Inno Pascal cannot call WinRT (`PasswordVault`). Design per-user cleanup around these limits. `.gitignore` regressions are caught by `GitIgnoreTests`.
24. **Use absolute paths everywhere (worktrees).** In PowerShell, `[IO.File]` and other .NET calls resolve relative paths against the *process* directory, not `Set-Location`. In a worktree, a relative path silently writes into the main checkout. The Write/Edit tools also resolve relative paths against the main checkout.
25. **Index layering (§21):** the index read model (`IndexSegment`, `IndexGeneration`) lives in Core so that Search can scan it without referencing Indexing. Only the index writer thread may call the `IndexSegment` mutation methods. Readers capture `IndexStore.CurrentGeneration` and bound iteration by `Count(segment)`.
26. **xUnit v3 analyzers** require `TestContext.Current.CancellationToken` for every CancellationToken parameter in tests (xUnit1051 is an error under warnings-as-errors).
27. **Placeholder scanner traps in src:**
    - the word "placeholder" in comments;
    - `mock`/`fake`/`stub` anywhere in identifiers or text;
    - comment-only `catch` blocks (give them a statement);
    - `Task.Delay` (use `PeriodicTimer` or a timed `SemaphoreSlim.Wait`).
28. **Named pipes:** the server can impersonate the client (`RunAsClient`) only after it has read data from it. The Index Service therefore impersonates after the client's `Hello`.
29. **Parent-index directory times:** a listing reports a sub-directory's mtime from the parent's index entry, which can be stale. The crawler stats each sub-directory itself before deciding to skip its listing.
30. **MsgReader is not used** (it pulls in Maui.Graphics and Win2D). `.msg` text comes from OpenMcdf (§21). The PdfPig NuGet id is `PdfPig`, not `UglyToad.PdfPig`.
31. **Timing tests and power:** the unit suite asserts 2× the §17 budgets and skips only when a gate is exceeded on battery power. Precise numbers come from `tests/NexPane.Benchmarks`, run on AC power.
32. **Elevated suites:**
    - `--filter-trait "Category=Elevated"` on `tests/NexPane.Indexing.Tests` creates and mounts a 256 MB VHDX with diskpart (no Hyper-V module needed);
    - the same filter on `tests/NexPane.IndexService.Tests` runs the NTFS backend over the system volume;
    - the SMB crawler test uses `New-SmbShare`.

## 7. Working rules

- **Zero-placeholder rule.** No `TODO`/`FIXME`/`NotImplementedException`, stub returns, fake data, lorem ipsum or commented-out features in `src/`, and do not write the word "TODO" in source comments (use "task P3.13" instead). If something cannot be done, mark the TODO item `[BLOCKED]` with the reason; do not ship a stub. **Enforced** by `eng/check-placeholders.ps1` (P1.12) over `src/`, `tools/`, `installer/`, `eng/` — run it directly, or it fails `Repository_has_no_placeholders_stubs_or_fake_work` in the default test suite. Rules PH001–PH008 (markers, NotImplementedException, placeholder text, message-less `NotSupportedException()`, empty/comment-only catch, `Task.Delay`/`Thread.Sleep` in non-test C#, Console/Debug-only event handlers, mock/fake/dummy/stub/sample data). A legitimate hit (e.g. retry backoff `Task.Delay`) needs an entry in `eng/placeholder-allowlist.json` (path + rule + ≥ 10-char literal line fragment + ≥ 20-char justification), added by QA; stale entries fail the scan (exit 2). Test code under `tests/` is not scanned.
- **Status discipline.** Owners move items `[PENDING] → [IN_PROGRESS] → [READY_FOR_QA]` (or `[BLOCKED]`). **Only Subagent 6 (QA, Verification & Security Inspector, veto power) sets `[VERIFIED_COMPLETE]`.**
- **Session-end rule.** **No subagent ends a session without updating `STATE.md` (snapshot, just finished, active work, issues, exact next commands, session log), `TODO.md` (status tags) and `HANDOFF.md` (files being worked on, new gotchas).**
- **Commits.** Commit at the end of each work item with a descriptive message ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. No remote, no push.
- **Architecture changes** require a row in the ARCHITECTURE §21 Decision Log and, if a requirement is affected, a Risks entry in §20.
- **Layer rules** (ARCHITECTURE §4) are enforced by `NexPane.Architecture.Tests`. Never add references between L2 feature modules.
- Do not run the app elevated during development, except for `[Elevated]` tests.

## 8. Subagent roster

| Id | Role | Owns |
|---|---|---|
| S1 | Lead Architect & State Scribe | Phase 1; ARCHITECTURE/TODO/STATE/HANDOFF stewardship; architecture tests (P1.10, P3.37) |
| S2 | Core Systems & Indexing Engine Engineer | Phase 3: VFS core, storage, in-memory index, USN/MFT + Index Service, walker, watchers, crawler, query language + regex search, content index. Phase 5 engines: archives, sync/compare, batch-rename engine, scripting engine, network clients (SMB/FTP/SFTP/WebDAV), transfers |
| S3 | Native Windows Shell & OS Integration Engineer | Context menus, IFileOperation, ShellExecuteEx, drag/drop + clipboard, thumbnails, native preview-handler hosting, manifest/DPI/process hardening (P4.42), Explorer "Open in NexPane", property-system/ADS metadata providers, credential stores (Credential Locker, Credential Manager, DPAPI; P6.01), `--purge-user-data` (P4.43) |
| S4 | AI & Semantic Pipeline Engineer | Phase 6: providers, embeddings/vector index, semantic search, NL→query, summarize/translate/reply, transcription (CPU + GPU), OCR, visual descriptions |
| S5 | UI/UX & Workspace Engineer | Phase 4 UI: shell window, panes/tabs, search UI + filter builder, built-in previews, themes, docking, toolbars, shortcuts, settings window; dialogs and panels for Phase 5/6 features |
| S6 | QA, Verification & Security Inspector (**veto power**) | Verifies every phase (P2.09, P3.36, P4.39, P5.34, P6.25); Phase 7 audits P7.01–P7.05; sole owner of `[VERIFIED_COMPLETE]`; owns the placeholder allowlist |
| S7 | Release & Installer Packaging Engineer | **Phase 2 branding/icons/assets** (P2.01–P2.08); eng pipeline, publish, installer, notices, signing step, release notes, uninstall data removal (P7.06–P7.12, P7.14) |
| ORC | Orchestrator | Toolchain, scheduling subagents |

Renumbered 2026-10-21 at the user's mandate, by role. Mapping: old S2 → S7; old S3 → S2; old S4 → S3 / S5; old S5 → S2 / S3 / S5; old S6 → S4; old S7 → S6 / S7. Log entries written before this change use the old numbers.

## 9. User decisions (recorded 2026-10-21, ARCHITECTURE §20)

| # | Decision |
|---|---|
| D1 | License: **proprietary EULA, all rights reserved**, © 2026 NexPane. `LICENSE.txt` is shown on the installer license page; `THIRD-PARTY-NOTICES.txt` lists all dependencies. |
| D2 | **No code signing**; v1 ships unsigned (SmartScreen warning accepted). Signing remains an optional build step that is skipped. The Windows 11 top-level context menu entry is **out of scope**. |
| D3 | Index Service installer task is **checked by default** for admin installs, with a privacy note. |
| D4 | Replacing Explorer as the default folder handler is **out of scope** for v1. |
| D5 | GPU Whisper (optional download of roughly 500 MB) is **in scope** for v1 (TODO P6.26–P6.30). |
| P1.14 | Network credentials → **Windows Credential Locker** (`PasswordVault`); AI keys → **DPAPI / Credential Manager**. Uninstall "remove user data" (default No) purges both stores plus `%LOCALAPPDATA%\NexPane` and `%APPDATA%\NexPane` for the installing user via `NexPane.exe --purge-user-data` (P4.43, P7.14). |
