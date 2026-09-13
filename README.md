# CleoAgent

A lightweight, provider-agnostic local AI agent host in C# / .NET. Runs an
autonomous agent (per-agent) on the user's machine with tools, persistent
semantic memory, disk-backed sessions, and LLM compaction.

Built for a single host that talks to OpenAI-compatible Responses endpoints
(OpenAI or OpenRouter).

## Status — v0.1.0

The memory engine, per-agent persona, disk-backed sessions, and compaction are
implemented and live-verified. See [v0.1.0 release](https://github.com/2D5B71P/CleoAgent/releases/tag/v0.1.0).

## Features (v0.1.0)

- **Agent host + REPL CLI.** Streaming `IModelProvider` events; `AgentLoop`
  drives tool calls. Tools: `run_command`, `read_file`, `write_file`,
  `list_directory`, `grep`, `get_environment_info` (env tool reports **names
  only**, never values), memory tools, and web tools.
- **Session logs you can actually search.** Every CLI run is one session
  (append-only JSONL under `agents/<id>/session/`). Four tools work on them:
  `list_sessions` (id, name, created, last activity, message count),
  `search_session_logs` (text search across messages/tool outputs/session
  names, with `from`/`to` date filters), `read_session_log` (paged history,
  `"current"` = the live session), and `name_session` (labels a session so it
  is findable later - "name this session 'web fetch debugging'"). Display
  metadata lives in a per-agent `sessions.json` index, reconciled against disk;
  name/times/counts never touch the raw JSONL.
- **Concurrent-session coordination.** Sessions working the same repo avoid
  edit collisions through a project-local blackboard: `work_claim`
  (claim/refresh your session's slot: task + files), `work_status` (who is
  working here, with staleness), `work_end` (release). Claims live in
  `<project>/agent_work/<sessionId>.md` - one file per SESSION, self-ignored
  from git - and expire by heartbeat (30 min): stale claims are abandoned
  and safe to take over. Advisory awareness, not locking.
- **Per-agent persona.** `agent.md` per agent (id-scoped under
  `%APPDATA%\CleoAgent\agents\<id>\`), so multiple agents stay isolated.
- **Persistent semantic memory.** Vector similarity store
  (`agents/<id>/memory/{short_term,long_term}.json`) with real embeddings
  (OpenAI `text-embedding-3-small` via OpenAI/OpenRouter). A priority-based
  summarizer distills recent turns into short-term Q/A pairs; a reflection pass
  promotes durable facts to long-term.
- **Disk-backed sessions + stability.** Sessions persist as append-only JSONL
  (`agents/<id>/session/<sessionId>.jsonl`) with stable ids and survive
  restarts. **LLM compaction** bounds the active conversation with a soft token
  ceiling + hard byte ceiling guardrail, keeping RAM and the per-turn API
  payload flat.

## Prerequisites

- .NET 10 SDK
- An API key for an OpenAI-compatible provider (OpenAI or OpenRouter)

## Quick start

```bash
# 1. Build (solution: CleoCore + CleoMemory + CleoWeb + CleoCLI)
dotnet build -c Debug

# 2. Configure (outside the repo, never committed)
#    Create %APPDATA%\CleoAgent\env  (see env.example)
#    Create %APPDATA%\CleoAgent\config.json  (see config.example.json)

# 3. Run the agent
.\CleoCLI\bin\Debug\net10.0\CleoAgent.exe

# 4. Memory/session self-test
.\CleoCLI\bin\Debug\net10.0\CleoAgent.exe --selftest
```

## Configuration

Lives in `%APPDATA%\CleoAgent\config.json` (JSON with `//` comments allowed).
Sections:

- `agent` — `id`, `summarizeEvery`
- `model` — `provider` (`openai`/`openrouter`), `model`, `api_key`
  (`${VAR}` env interpolation; keep the literal key out of the file)
- `embedding` — `provider` (`none`=hash fallback, `openai`/`openrouter`=real),
  `model`, `api_key`
- `compaction` — `keepRecentTurns`, `softTokenCeiling`, `hardByteCeiling`

API keys are **strictly** resolved from `config.*.api_key` (themselves usually
`${ENV_VAR}` tokens) — never pasted into the repo.

## Layout (modular — 2026-09-13 restructure)

One solution (`CleoAgent.slnx`), four projects. Multi-project is a build-time
shape only: no runtime plugin loading, no behavior change vs the old
single-project host. Each module project depends on `CleoCore` only; the CLI
is the sole project that composes more than one module. (See
DESIGN-modules.md for the full module-system design.)

- `CleoCore/` — host kernel + core modules. `CleoCore.csproj`
  - `Agent/` — `AgentLoop`, events, planner.
  - `Context/` — `ContextEngine` + `IContextSource` (system, workspace).
  - `Config/` — config records + loader (comments + `${VAR}` interpolation).
  - `Env/` — environment loader.
  - `Model/` — provider interfaces + OpenAI/OpenRouter/Google providers, factory.
  - `Session/` — `SessionMessage`, `SessionStore` (JSONL + `sessions.json`
    display index), `SessionIdHandle` (live "current" session id).
  - `Tools/` — tool registry + devtools / session / work tools.
  - `Work/` — work-board state store.
- `CleoMemory/` — the memory module. `CleoMemory.csproj`
  - `Memory/` — `IMemoryRepository` (+ file / in-memory impls), documents.
  - `Embedding/` — embedding providers + factory.
  - `Tools/` — `memory_write/retrieve/forget/clear`.
  - `Context/` — `MemorySource` (dormant by design: zero auto-injection).
  - `MemorySelfTest.cs` — the memory capability's self-test.
- `CleoWeb/` — the web module. `CleoWeb.csproj`
  - `Fetch/` — fetch providers + factory (SmartReader/ReverseMarkdown local,
    jina). `Search/` — search providers + factory (jina, duckduckgo).
  - `Tools/` — `web_fetch` / `web_search`.
- `CleoCLI/` — the executable. `CleoAgent.csproj` (output binary:
  `CleoAgent.exe`) — REPL entry point (`Program.cs`).
- `tools/WebFetchProbe/` — standalone git-ignored probe; compiles the web
  provider sources directly (paths track the module layout).

## Roadmap (not yet built)

1. Deliberate **planning loop** (plan → observe → re-plan) in `AgentLoop`.
2. **Spectre.Console** CLI/UX.
3. Config-driven reflection threshold + memory similarity threshold.

## License

Private. See LICENSE if added.
