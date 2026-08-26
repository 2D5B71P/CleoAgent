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
  only**, never values).
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
# 1. Build
dotnet build -c Debug

# 2. Configure (outside the repo, never committed)
#    Create %APPDATA%\CleoAgent\env  (see env.example)
#    Create %APPDATA%\CleoAgent\config.json  (see config.example.json)

# 3. Run the agent
dotnet run -c Debug --project CleoAgent.csproj

# 4. Memory/session self-test
dotnet run -c Debug --project CleoAgent.csproj -- --selftest
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

## Layout

- `CLI/` — REPL entry point (`Program.cs`), `MemorySelfTest`.
- `Core/Agent/` — `AgentLoop`, events.
- `Core/Context/` — `ContextEngine` + `IContextSource` (system, workspace,
  memory).
- `Core/Memory/` — repository, embeddings, summarizer, reflection.
- `Core/Session/` — `SessionMessage`, `SessionStore` (JSONL).
- `Core/Model/` — provider interfaces + OpenAI/OpenRouter providers, factories.
- `Core/Tools/` — tool registry + implementations.
- `Core/Config/` — config loading (comments + `${VAR}` interpolation).

## Roadmap (not yet built)

1. Deliberate **planning loop** (plan → observe → re-plan) in `AgentLoop`.
2. **Spectre.Console** CLI/UX.
3. Config-driven reflection threshold + memory similarity threshold.

## License

Private. See LICENSE if added.
