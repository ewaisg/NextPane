# NexPane

NexPane is an AI-powered file explorer for Windows 10 (1809+) and Windows 11, built with C# on .NET 10 (LTS) and WPF (x64, unpackaged).

**Highlights (planned)**
- Search-as-you-type over an in-memory filename index, inline query operators (`ext:`, `size:>50MB`, `modified:today`, `content:"..."`, `regex:`, boolean logic), a visual filter builder, and live results.
- Fast NTFS indexing through an optional index service (USN journal / MFT), with a multithreaded walker and change watchers as the fallback.
- Dual pane, unlimited tabs, session restore, preview pane, and the native Windows Shell context menu.
- Power tools: archives as virtual folders (RAR is extract-only), folder compare/sync, batch renamer with undo, a metadata editor, JavaScript scripting, and SMB/FTP/FTPS/SFTP/WebDAV browsing.
- AI with your choice of provider (Azure AI Foundry, Gemini, Claude, OpenAI, AWS Bedrock, LM Studio/Ollama): semantic search, natural-language queries, summarize/translate/reply drafts, transcription, OCR, and image descriptions.

**Status:** Phase 1 (architecture + solution skeleton). The solution builds and tests pass; no features implemented yet.

**License:** Proprietary — see [LICENSE.txt](LICENSE.txt). Third-party components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Documentation

| Document | Purpose |
|---|---|
| [docs/handoff/HANDOFF.md](docs/handoff/HANDOFF.md) | Start here: setup, build/test/publish/installer commands, gotchas, team rules |
| [docs/handoff/STATE.md](docs/handoff/STATE.md) | Exact current state and next commands |
| [docs/handoff/TODO.md](docs/handoff/TODO.md) | Phase-by-phase checklist with owners and acceptance tests |
| [docs/handoff/ARCHITECTURE.md](docs/handoff/ARCHITECTURE.md) | Full technical design |

## Quick build

Requires the .NET 10 SDK (10.0.402 or a later 10.0 feature band).

```powershell
dotnet build NexPane.sln -c Release
dotnet test --solution NexPane.sln -c Release --filter-not-trait "Category=Elevated" --filter-not-trait "Category=Live"
```
