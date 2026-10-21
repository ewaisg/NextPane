# NexPane — Session Handoff Briefing

> Read this first in every new session, then `STATE.md` (exact current state), then the relevant phase of `TODO.md`, then the referenced sections of `ARCHITECTURE.md`.
> Last update: 2026-10-21 by Subagent 1 (Lead Architect & State Scribe).

## 1. Executive summary

NexPane is a production-grade, AI-powered Windows file explorer: C# / **.NET 10 LTS** / **WPF**, x64, unpackaged, Windows 10 1809+ and Windows 11, Inno Setup 6 installer, **proprietary license, shipped unsigned**.

Phase 1 is complete up to QA. The toolchain is installed. The git repository (`main`) and `NexPane.sln` exist, with 15 `src` projects layered per ARCHITECTURE §4, 16 xUnit v3 test projects and a benchmarks runner. The solution builds with 0 warnings and 0 errors, and 47 of 47 tests pass. The app opens an empty window titled "NexPane". The IndexService is a Windows-service host skeleton that starts and stops cleanly. No feature code exists yet.

Next: P1.11 and P1.12 (eng scripts and the placeholder scanner), then Phase 2 (branding, S2) and Phase 3 (core FS, indexer and search, S3), which can run in parallel.

## 2. Files currently being worked on

| File / area | State |
|---|---|
| `docs/handoff/*.md`, `README.md`, `.gitignore` | Complete, `[READY_FOR_QA]` |
| `LICENSE.txt` (proprietary EULA), `THIRD-PARTY-NOTICES.txt` | Complete, `[READY_FOR_QA]` (P1.13); full license texts are appended at P7.06 |
| `NexPane.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes` | Complete, `[READY_FOR_QA]` (P1.09) |
| `src/*` | Project shells only (no code), except `NexPane.App` (MainWindow) and `NexPane.IndexService` (`Program.BuildHost`) |
| `tests/NexPane.Architecture.Tests` | Layering + convention tests, `[READY_FOR_QA]` (P1.10) |
| `tests/Shared/ModuleAssembly.cs` | Shared helper linked into every `*Tests` project (x64 / target framework assertions) |
| Next up | `eng/build.ps1`, `eng/publish.ps1`, `eng/installer.ps1` (P1.11), `eng/check-placeholders.ps1` (P1.12) |

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
6. `tools/NexPane.AssetGen` does not exist yet; S2 creates it in P2.02.

## 5. Build, test, publish, installer commands

```powershell
# Build (verified: 0 Warning(s), 0 Error(s))
dotnet build NexPane.sln -c Release

# Test. global.json selects Microsoft.Testing.Platform, so use --solution and xUnit v3 trait filters.
# Default CI filter (Elevated and Live suites are opt-in) — verified 47/47 passing:
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

# Installer (Phase 7) → dist\NexPane-Setup-<version>.exe
& $Iscc installer\NexPane.iss
```

Build outputs: `NexPane.exe` is in `src/NexPane.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/` (the App's `AssemblyName` is `NexPane`).

Wrapper scripts (P1.11) will be `eng/build.ps1`, `eng/publish.ps1` and `eng/installer.ps1`. **Code signing is an optional step that is skipped by default:** `eng/publish.ps1` signs only when `NEXPANE_SIGN_CERT_THUMBPRINT` is set, and v1 ships unsigned. `build/` and `dist/` are git-ignored output folders.

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
12. **Secrets** go only through `ICredentialStore` (CredMan; blobs over 2,560 bytes overflow to DPAPI). Never put them in settings.json, app.db, logs or URLs. The Gemini key goes in the `x-goog-api-key` header, never in a query string.
13. **No trimming or single-file publish.** Do not enable `PublishTrimmed` or `PublishSingleFile`; an architecture test checks this.
14. **FluentAssertions** is pinned to `7.2.0` (v8+ is commercially licensed); a test checks the pin. In v7, `OnlyContain` **fails on empty collections**, so use `NotContain(x => !predicate)` for "all items satisfy" checks.
15. **Archives:** never offer RAR creation, always apply zip-slip checks, and extract to a per-user ACL'd temp folder.
16. **Windows 11 context menu:** registry verbs appear under "Show more options". This is final: a top-level Windows 11 entry is out of scope for v1 (decided, ARCHITECTURE R6).
17. **Anthropic provider:** there is no embeddings API (semantic search needs another provider). Handle `stop_reason: "refusal"`, discover models through the Models API, and use `claude-opus-5-5` as the preset default model.
18. **Microsoft.Testing.Platform:** `dotnet test` needs `--solution`/`--project`. VSTest-style `--filter "Category!=X"` does not work; use `--filter-trait` / `--filter-not-trait`.
19. **Analyzers:** `AnalysisLevel=latest-recommended` with warnings as errors. CA2007 (`ConfigureAwait`) is enforced in libraries but disabled in `NexPane.App` and in tests. Executable types should be `internal`; WPF XAML uses `x:ClassModifier="internal"`.
20. **GPU Whisper** runtimes (CUDA and Vulkan) are optional downloads into `%LOCALAPPDATA%\NexPane\runtimes\`. They are never bundled in the installer, and loading must always fall back to CPU without crashing (ARCHITECTURE §13.2).

## 7. Working rules

- **Zero-placeholder rule.** No `TODO`/`FIXME`/`NotImplementedException`, stub returns, fake data, lorem ipsum or commented-out features in `src/`, and do not write the word "TODO" in source comments (use "task P3.13" instead). If something cannot be done, mark the TODO item `[BLOCKED]` with the reason; do not ship a stub. `eng/check-placeholders.ps1` (P1.12) will enforce this.
- **Status discipline.** Owners move items `[PENDING] → [IN_PROGRESS] → [READY_FOR_QA]` (or `[BLOCKED]`). **Only Subagent 7 (QA) sets `[VERIFIED_COMPLETE]`.**
- **Session-end rule.** **No subagent ends a session without updating `STATE.md` (snapshot, just finished, active work, issues, exact next commands, session log), `TODO.md` (status tags) and `HANDOFF.md` (files being worked on, new gotchas).**
- **Commits.** Commit at the end of each work item with a descriptive message ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. No remote, no push.
- **Architecture changes** require a row in the ARCHITECTURE §21 Decision Log and, if a requirement is affected, a Risks entry in §20.
- **Layer rules** (ARCHITECTURE §4) are enforced by `NexPane.Architecture.Tests`. Never add references between L2 feature modules.
- Do not run the app elevated during development, except for `[Elevated]` tests.

## 8. Subagent roster

| Id | Role | Owns |
|---|---|---|
| S1 | Lead Architect & State Scribe | Phase 1; ARCHITECTURE/TODO/STATE/HANDOFF stewardship; architecture tests; eng scripts |
| S2 | Branding & Asset Engineer | Phase 2: logo SVGs, AssetGen, ICO/PNG/BMP, theme palette |
| S3 | Core FS, Indexing & Search Engineer | Phase 3: VFS core, storage, in-memory index, USN/MFT/service, walker, watchers, crawler, query language, content index |
| S4 | Shell UI & Native Integration Engineer | Phase 4: WPF shell, panes/tabs, search UI, native context menu, IFileOperation, drag/drop, preview, customization |
| S5 | Power Tools Engineer | Phase 5: archives, compare/sync, batch rename, metadata, scripting, network VFS |
| S6 | AI Integration Engineer | Phase 6: credentials, providers, semantic search, NL→query, summarize/translate/reply, transcription (CPU + GPU), OCR, descriptions |
| S7 | QA Auditor & Release Engineer | Verifies every phase; Phase 7 audit, publish, installer, release notes |
| ORC | Orchestrator | Toolchain, scheduling subagents |

## 9. User decisions (recorded 2026-10-21, ARCHITECTURE §20)

| # | Decision |
|---|---|
| D1 | License: **proprietary EULA, all rights reserved**, © 2026 NexPane. `LICENSE.txt` is shown on the installer license page; `THIRD-PARTY-NOTICES.txt` lists all dependencies. |
| D2 | **No code signing**; v1 ships unsigned (SmartScreen warning accepted). Signing remains an optional build step that is skipped. The Windows 11 top-level context menu entry is **out of scope**. |
| D3 | Index Service installer task is **checked by default** for admin installs, with a privacy note. |
| D4 | Replacing Explorer as the default folder handler is **out of scope** for v1. |
| D5 | GPU Whisper (optional download of roughly 500 MB) is **in scope** for v1 (TODO P6.26–P6.30). |
