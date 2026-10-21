# NexPane — Session Handoff Briefing

> Read this first in every new session, then `STATE.md` (exact current state), then the relevant phase of `TODO.md`, then the referenced sections of `ARCHITECTURE.md`.
> Last update: 2026-10-21 by Subagent 1 (Lead Architect & State Scribe).

## 1. Executive summary

NexPane is a production-grade, AI-powered Windows file explorer: C# / **.NET 10 LTS** / **WPF**, x64, unpackaged, Windows 10 1809+ and Windows 11, Inno Setup 6 installer. Phase 1 (architecture + handoff docs) is written and awaiting QA; **no source code exists yet**. The next concrete step is toolchain verification and repository/solution initialization (P1.01, P1.09), then Phase 2 (branding) and Phase 3 (core FS, indexer, search).

## 2. Files currently being worked on

| File | State |
|---|---|
| `docs/handoff/ARCHITECTURE.md` | Complete, `[READY_FOR_QA]` |
| `docs/handoff/TODO.md` | Complete, `[READY_FOR_QA]` |
| `docs/handoff/STATE.md` | Complete, `[READY_FOR_QA]` |
| `docs/handoff/HANDOFF.md` | This file, `[READY_FOR_QA]` |
| `README.md`, `.gitignore` | Complete, `[READY_FOR_QA]` |
| Next to be created (ORC, P1.09) | `NexPane.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`, all projects under `src/` and `tests/` |

## 3. Environment setup

Run in an elevated PowerShell **only for installs**; run the app and builds unelevated.

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
winget install --id JRSoftware.InnoSetup    -e --accept-source-agreements --accept-package-agreements
winget install --id Git.Git                 -e --accept-source-agreements --accept-package-agreements
# Open a NEW terminal so PATH updates apply, then verify:
dotnet --list-sdks                 # expect 10.0.x
git --version
Get-Command ISCC.exe -ErrorAction SilentlyContinue   # usually not on PATH; use full path below
$Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")  # winget installed it per-user (LOCALAPPDATA) on this machine | Where-Object { Test-Path $_ } | Select-Object -First 1
```

Optional for tests: Windows Sandbox feature (installer tests), Hyper-V PowerShell module (`New-VHD` for elevated USN tests), Developer Mode (unprivileged symlink tests), en-US OCR language capability (`Get-WindowsCapability -Online -Name "Language.OCR*"`).

## 4. Repository initialization (P1.09 — owner ORC)

```powershell
Set-Location C:\Users\Ewais\Projects\NexPane
git init -b main
dotnet new globaljson --sdk-version 10.0.100 --roll-forward latestFeature
dotnet new sln -n NexPane
dotnet new install xunit.v3.templates

# src (TFM: Core = net10.0; all others net10.0-windows10.0.19041.0 — set <TargetFramework> explicitly in each csproj)
dotnet new classlib -n NexPane.Core          -o src/NexPane.Core
foreach ($p in 'Shell','Storage','Security','Indexing','Search','Archives','Network','Sync','Metadata','Scripting','AI') {
  dotnet new classlib -n "NexPane.$p" -o "src/NexPane.$p"
}
dotnet new wpflib -n NexPane.Preview         -o src/NexPane.Preview
dotnet new wpf    -n NexPane.App             -o src/NexPane.App
dotnet new worker -n NexPane.IndexService    -o src/NexPane.IndexService
dotnet new console -n NexPane.AssetGen       -o tools/NexPane.AssetGen

# tests
foreach ($t in 'Core','Search','Indexing','Storage','Shell','Security','Archives','Network','Sync','Metadata','Scripting','AI','Preview','Architecture','UITests') {
  $name = if ($t -eq 'UITests') { 'NexPane.UITests' } else { "NexPane.$t.Tests" }
  dotnet new xunit3 -n $name -o "tests/$name"
}
dotnet new console -n NexPane.Benchmarks -o tests/NexPane.Benchmarks

Get-ChildItem -Recurse -Filter *.csproj src,tests,tools | ForEach-Object { dotnet sln NexPane.sln add $_.FullName }
# Add project references exactly per ARCHITECTURE.md §3 project table and §4 layer rules.
```

Then add `Directory.Build.props` (ARCHITECTURE §2), `Directory.Packages.props` (§15, FluentAssertions pinned `7.2.0`), `.editorconfig`, `.gitattributes` (`* text=auto eol=crlf`; `*.ico *.png *.bmp *.dll *.zip *.7z *.rar *.pdf *.mp3 *.mp4 *.wav *.jpg binary`).

## 5. Build, test, publish, installer commands

```powershell
# Build
dotnet build NexPane.sln -c Release

# Test (default CI filter; Elevated and Live suites are opt-in)
dotnet test NexPane.sln -c Release --filter "Category!=Elevated&Category!=Live"
# Elevated suite (run from an elevated terminal)
dotnet test NexPane.sln -c Release --filter "Category=Elevated"
# Benchmarks
dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *

# Publish (self-contained, ReadyToRun, NOT single-file, NOT trimmed)
dotnet publish src/NexPane.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o build/publish/win-x64
dotnet publish src/NexPane.IndexService -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o build/publish/win-x64

# Branding assets (Phase 2)
dotnet run -c Release --project tools/NexPane.AssetGen -- --svg assets/brand/nexpane-logo.svg --mark assets/brand/nexpane-mark.svg --out assets

# Installer (Phase 7) → dist\NexPane-Setup-<version>.exe
& $Iscc installer\NexPane.iss
```

Wrapper scripts (P1.11): `eng/build.ps1`, `eng/publish.ps1`, `eng/installer.ps1`. `build/` and `dist/` are output folders and git-ignored; scripts live in `eng/`.

## 6. Architectural gotchas (read before writing code)

1. **COM apartments.** Shell COM objects (IShellFolder, IContextMenu, IFileOperation, IPropertyStore, IShellItemImageFactory, IFilter, preview handlers in-proc) must be created, used and released on the **same STA thread** with a message pump. Use the UI thread (context menus only) or `ShellStaScheduler`. Never touch them from ThreadPool threads.
2. **IContextMenu2/3 message forwarding.** While `TrackPopupMenuEx` is open, add an `HwndSource.AddHook` on the owner window and forward `WM_INITMENUPOPUP`, `WM_DRAWITEM`, `WM_MEASUREITEM`, `WM_MENUCHAR` (and `WM_MENUSELECT`) to `IContextMenu3.HandleMenuMsg2` (or `IContextMenu2.HandleMenuMsg`). Without it "Send to", "Open with" and "New" are empty.
3. **Preview handlers run out-of-proc.** Create with `CLSCTX_LOCAL_SERVER` so they load in the `prevhost.exe` surrogate per their AppID (this is how Explorer isolates them). Fall back to `CLSCTX_INPROC_SERVER` on a Shell STA thread only with the crash sentinel + blocklist. Host in an `HwndHost` child HWND; call `SetRect` on resize and `Unload` on selection change. **Airspace:** nothing WPF can be drawn over that HWND.
4. **PerMonitorV2 DPI** in `app.manifest` (plus `longPathAware`, Common Controls v6). Without PMv2 the native menus and preview HWNDs blur or mis-position on mixed-DPI setups.
5. **Never block the dispatcher.** No synchronous I/O, COM-to-STA waits, `.Result`/`.Wait()` on the UI thread. Background → `Channel<T>` → `DispatcherCoalescer` (≤ 10 UI batches/s). The stall watchdog logs anything > 100 ms.
6. **Long paths.** Use `\\?\` / `\\?\UNC\` prefixes for Win32 file APIs; .NET does this automatically, but shell APIs (PIDLs, IContextMenu, ShellExecuteEx, preview handlers) are MAX_PATH-limited → fall back to built-in menus/ops with a notice.
7. **ShellExecuteEx** on an STA with `SEE_MASK_NOASYNC` (otherwise DDE-based launches can be dropped when the calling thread exits) and `SEE_MASK_INVOKEIDLIST` for PIDL-based items.
8. **USN/MFT needs admin.** The app itself must run unelevated (UIPI breaks drag/drop with Explorer when elevated). Fast NTFS indexing goes through `NexPane.IndexService` (LocalSystem) over `\\.\pipe\NexPane.Index.v1`, which filters results with `AccessCheck` against the client token.
9. **ReadDirectoryChangesW buffers.** Max 64 KB over SMB; handle `ERROR_NOTIFY_ENUM_DIR`/zero-byte completions as overflow → rewalk subtree.
10. **Single writer** for the in-memory index and for each SQLite DB; readers use immutable generations (index) and WAL (SQLite).
11. **Regex from users** → `RegexOptions.NonBacktracking`; fallback to backtracking only with a timeout.
12. **Secrets** only via `ICredentialStore` (CredMan; > 2 560-byte blobs DPAPI-overflow). Never in settings.json, app.db, logs or URLs. Gemini key goes in `x-goog-api-key` header, never a query string.
13. **WPF trimming/single-file:** do not enable `PublishTrimmed` or `PublishSingleFile`.
14. **FluentAssertions** is pinned to `7.2.0` (v8+ is commercially licensed). Do not upgrade.
15. **Archives:** never offer RAR creation; always apply zip-slip checks; extract to per-user ACL'd temp.
16. **Windows 11 context menu:** registry verbs appear under "Show more options"; this is expected (ARCHITECTURE R6).
17. **Anthropic provider:** no embeddings API (semantic search needs another provider); handle `stop_reason: "refusal"`; discover models via the Models API; preset default model `claude-opus-5-5`.

## 7. Working rules

- **Zero-placeholder rule.** No `TODO`/`FIXME`/`NotImplementedException`/stub returns/fake data/lorem ipsum/commented-out features in `src/`. If something cannot be done, mark the TODO item `[BLOCKED]` with the reason — do not ship a stub. `eng/check-placeholders.ps1` enforces this.
- **Status discipline.** Owners move items `[PENDING] → [IN_PROGRESS] → [READY_FOR_QA]` (or `[BLOCKED]`). **Only Subagent 7 (QA) sets `[VERIFIED_COMPLETE]`.**
- **Session-end rule.** **No subagent ends a session without updating `STATE.md` (snapshot, just finished, active work, issues, exact next commands, session log), `TODO.md` (status tags), and `HANDOFF.md` (files being worked on, new gotchas).**
- **Architecture changes** require a row in ARCHITECTURE §21 Decision Log and, if a requirement is affected, a Risks entry in §20.
- **Layer rules** (ARCHITECTURE §4) are enforced by `NexPane.Architecture.Tests`; never add references between L2 feature modules.
- Do not run the app elevated during development except for `[Elevated]` tests.

## 8. Subagent roster

| Id | Role | Owns |
|---|---|---|
| S1 | Lead Architect & State Scribe | Phase 1; ARCHITECTURE/TODO/STATE/HANDOFF stewardship; architecture tests; eng scripts |
| S2 | Branding & Asset Engineer | Phase 2: logo SVGs, AssetGen, ICO/PNG/BMP, theme palette |
| S3 | Core FS, Indexing & Search Engineer | Phase 3: VFS core, storage, in-memory index, USN/MFT/service, walker, watchers, crawler, query language, content index |
| S4 | Shell UI & Native Integration Engineer | Phase 4: WPF shell, panes/tabs, search UI, native context menu, IFileOperation, drag/drop, preview, customization |
| S5 | Power Tools Engineer | Phase 5: archives, compare/sync, batch rename, metadata, scripting, network VFS |
| S6 | AI Integration Engineer | Phase 6: credentials, providers, semantic search, NL→query, summarize/translate/reply, transcription, OCR, descriptions |
| S7 | QA Auditor & Release Engineer | Verifies every phase; Phase 7 audit, publish, installer, release notes |
| ORC | Orchestrator | Toolchain install, repo init, scheduling subagents |

## 9. Open decisions for the user (ARCHITECTURE §20)

Q1 NexPane license · Q2 code-signing certificate · Q3 index service default-on · Q4 Windows 11 top-level menu via sparse package · Q5 default-file-manager replacement · Q6 Whisper GPU runtime.
