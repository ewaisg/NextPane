# NexPane — Current State

> Updated by every subagent at the end of every session. Newest entry on top of the Session Log.
> Last update: **2026-10-21** by **Subagent 1 (Lead Architect & State Scribe)**.

## 1. Snapshot

| Item | Value |
|---|---|
| Current phase | **Phase 1 — Architecture / Stack Lock / Handoff Init** |
| Phase 1 progress | Docs complete (`[READY_FOR_QA]`); toolchain install `[IN_PROGRESS]` (ORC); repo/solution init `[PENDING]` (ORC) |
| Source code | **None yet.** No `.sln`, no `.csproj`, no `.cs` files exist. |
| Git | Not initialized (no `.git` directory). |
| Build status | N/A (no solution). |
| Test status | N/A. |
| Blockers | Toolchain install must finish before P1.09. User decisions Q1–Q6 (ARCHITECTURE §20) pending; only Q1 blocks an item (P1.13). |

## 2. What exists on disk

```
C:\Users\Ewais\Projects\NexPane\
├─ .gitignore                       (new)
├─ README.md                        (new)
└─ docs\handoff\
   ├─ ARCHITECTURE.md               (new — 22 sections)
   ├─ TODO.md                       (new — Phases 1–7)
   ├─ STATE.md                      (this file)
   └─ HANDOFF.md                    (new)
```

## 3. Just finished
- P1.02 ARCHITECTURE.md, P1.03 TODO.md, P1.04 STATE.md, P1.05 HANDOFF.md, P1.06 README.md, P1.07 .gitignore, P1.08 traceability matrix (ARCHITECTURE §22) — all `[READY_FOR_QA]`.
- Installer AppId fixed: `{23676795-F60E-4EAF-95C6-C4D81BAD01C1}`.

## 4. Active work

| Item | Owner | Status | Notes |
|---|---|---|---|
| P1.01 Toolchain install (.NET 10 SDK, Inno Setup 6, Git) | ORC | `[IN_PROGRESS]` | Running in parallel via winget; not verified by S1 (S1 did not run dotnet/git by instruction). |
| P1.09 Git repo + solution + test framework | ORC | `[PENDING]` | Starts after P1.01 verification. |

## 5. Active issues / risks
1. Toolchain not yet verified — `dotnet`, `git`, `ISCC.exe` may not be on PATH in the current shell until a new terminal is opened (winget updates PATH for new processes only).
2. FluentAssertions must be pinned to `7.2.0` (v8+ commercial license) — enforce in `Directory.Packages.props`.
3. `xunit.v3` + STA tests: use the in-repo `StaTestRunner` helper (ARCHITECTURE §18) rather than relying on a third-party STA attribute.
4. Open user questions Q1–Q6 (ARCHITECTURE §20). Q1 (NexPane license) blocks P1.13 only.

## 6. Exact next commands (run in a NEW PowerShell window after toolchain install)

```powershell
# 1) Verify toolchain (P1.01 acceptance)
dotnet --list-sdks
git --version
Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"; Test-Path "$env:ProgramFiles\Inno Setup 6\ISCC.exe"

# 2) Initialize repo (P1.09)
Set-Location C:\Users\Ewais\Projects\NexPane
git init -b main
dotnet new globaljson --sdk-version 10.0.100 --roll-forward latestFeature
dotnet new sln -n NexPane
# then create projects exactly per ARCHITECTURE.md §3 (see HANDOFF.md §4 for the full scripted list),
# add Directory.Build.props / Directory.Packages.props / .editorconfig / .gitattributes,
dotnet build NexPane.sln -c Release
dotnet test NexPane.sln -c Release
git add -A
git commit -m "Phase 1: architecture docs, solution skeleton, test framework"
```

## 7. Session log

### 2026-10-21 — Subagent 1 (Lead Architect & State Scribe)
- Wrote ARCHITECTURE.md (stack rationale, layout, layering, threading, 5 Mermaid flows + dependency graph, full SQL for `index.db`/`app.db`, C# contracts, packages/licenses, security, budgets, testing, branding/installer, 24 risks, 6 open questions, decision log, traceability).
- Wrote TODO.md (Phases 1–7, 169 items, each with owner + acceptance test), STATE.md, HANDOFF.md, README.md, .gitignore.
- Key decisions: Jint for scripting; 7z.dll (SevenZipSharp) + SharpCompress fallback, RAR read-only; brute-force float32 vector search with TensorPrimitives; separate LocalSystem index service for USN/MFT; MediaElement (no LibVLC); Markdig→WebView2; official OpenAI + Anthropic SDKs, AWSSDK.BedrockRuntime, REST for Gemini.
- Did not run dotnet/git (instructed). No code written.
