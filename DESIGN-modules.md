# CleoAgent — Modular Redesign (design doc)

Status: **Draft v2 (rev 2026-09-13)**. Decisions locked: multi-project solution;
**hybrid loading** — static modules for everything we ship (linked into the exe),
external modules for agent-built and third-party code (discovered on disk at
runtime, §7); activation policy = config allowlist + model may enable anything
allowed (no user approval gate). Open questions (§11) all answered 2026-09-13 —
see resolutions below. Implemented through Phase 4 (2026-09-13): Phases 1-2
committed (bd54297, 666b3bc); Phase 3 committed (module allowlist + model-
requestable logical activation, live-verified); Phase 4 committed (external
agent-authored modules, script-backed + out-of-process, live-verified). 2026-09-13
v0.2.0: deprecated blocks REMOVED (user-authorized); legacy pre-modular config
sections deleted — `modules.<id>` is the only config surface (live config
migrated, boot deprecation notes gone).

---

## 1. Goals & non-goals

Goals:
- **Separate maintainability**: each capability area is an isolated project
  with its own source tree, config surface, and self-test.
- **Runtime capability management**: modules load/unload at runtime; the model
  can *request* a capability that isn't active ("enable web") instead of always
  seeing every tool.
- **Agent-authored modules** (stretch): the agent can write and install its own
  modules (manifest + script-backed tools) without touching host code.
- **Kill the god-factory**: `CLI/Program.cs` currently hand-wires every tool,
  provider and context source (~250 lines). Composition becomes data-driven.

Non-goals (keep it lean):
- No bloat. 4 projects max for the shipped binary. No runtime config language,
  no scripting host beyond what exists, no service-discovery network.
- No behavior change in Phase 1–2: same tools, same defaults. Everything is
  on by default exactly as today.

---

## 2. Current friction (measured)

- `Program.cs` constructs every tool/provider inline; adding a capability
  touches the composition root + imports.
- The model sees **all** tools every turn, configured or not, wanted or not
  (token + context cost, no discovery/request path).
- `ToolRegistry` is a plain Dictionary — no scoping, no load/unload.
- Tests are a single `CLI/MemorySelfTest.cs` even though memory is only one
  capability.
- Config is one flat `AppConfig` with all sections; no per-capability ownership
  (e.g. `[embedding]` conceptually belongs to memory, `[web]` to the web
  module, but all live in `Core/Config/Config.cs`).

The codebase is *already* directory-modular (`Core/Web/`, `Core/Memory/`,
`Core/Session/`, `Core/Work/`, `Core/Tools/Impl/`). This redesign formalizes
that structure into projects + a module contract. It is a restructure, not a
rewrite.

---

## 3. Architecture overview

```
                        ┌─────────────────────────────────────────┐
                        │              CleoCLI (exe)              │
                        │  REPL, event rendering, --selftest      │
                        └───────────────┬─────────────────────────┘
                                        │ builds host + loads modules per config
                        ┌───────────────▼─────────────────────────┐
                        │              CleoCore                    │
                        │  ┌────────────────────────────────────┐  │
                        │  │ HOST KERNEL (not a module)         │  │
                        │  │ AgentLoop · Model · Config ·       │  │
                        │  │ SessionStore · ToolRegistry ·      │  │
                        │  │ IModule/ModuleManager/ServiceReg   │  │
                        │  │ Kernel tools: list_modules,        │  │
                        │  │ module_enable/disable, module_status│  │
                        │  └────────────────────────────────────┘  │
                        │  ┌────────────────────────────────────┐  │
                        │  │ Core modules (in CleoCore project) │  │
                        │  │  devtools  (files + run_command)   │  │
                        │  │  session   (log tools)             │  │
                        │  │  work      (claim board)           │  │
                        │  └────────────────────────────────────┘  │
                        └───────────────┬─────────────────────────┘
                            depends on │ only Core
                        ┌───────────────▼─────────────────────────┐
                        │              CleoMemory                  │
                        │  memory module: memory_* tools,          │
                        │  embedding providers, IMemoryRepository  │
                        │  impls (file today, mongo later)         │
                        └───────────────┬─────────────────────────┘
                        ┌───────────────▼─────────────────────────┐
                        │              CleoWeb                     │
                        │  web module: web_fetch + web_search,     │
                        │  fetch/search providers, SmartReader     │
                        └─────────────────────────────────────────┘
```

**External modules** (agent-built / third-party) do not live in any project:
they are directories under `<appdata>/modules/<id>/` (host-wide) and
`agents/<id>/modules/<id>/` (per-agent) containing `module.json` + either a
script implementation or a compiled plugin DLL. The ModuleManager scans those
dirs at startup (and on model-request) and loads them dynamically. They use the
*same* `IModule` contract as static modules — only discovery differs (§7).

Project dependency rules (one direction only):
- `CleoCore` — depends on **nothing** (stdlib only). Host kernel + core modules.
- `CleoMemory` — depends on `CleoCore` only.
- `CleoWeb` — depends on `CleoCore` only.
- `CleoCLI` — depends on all; contains `Program.cs`; the only place that
  imports from more than one module project.

The "no bloat" grouping you specified maps 1:1:
- **core** = devtools (files + command), session, work — lives *inside*
  CleoCore, because those are host-coupled (devtools is the base capability,
  session tools wrap host session infra, work board wraps session tooling).
- **web** = web search + web fetch — one module in CleoWeb (two tool groups,
  one manifest; can split into `web.fetch`/`web.search` modules later without
  restructuring — see §8 activation granularity).
- **memory** = memory tools + embedding + storage backends (`IMemoryRepository`
  file impl today; **MongoDB = new repository implementation**, no contract
  change — the existing interface already abstracts storage).

Model providers (`OpenAI/OpenRouter/Google`, `IModelProvider`) stay in the host
kernel: the host *is* a model host. Same for the session store and planner.

---

## 4. Module contract

```csharp
interface IModule
{
    ModuleManifest Manifest();
    Task LoadAsync(ModuleContext ctx);      // register tools/services/context sources
    Task UnloadAsync(ModuleContext ctx);    // reverse; next turn's definitions drop them
}

interface ModuleContext
{
    ToolRegistry Tools;          // Register/Unregister
    ServiceRegistry Services;    // Get/Register by name: memory repo, embedding, fetch…
    ConfigSection Config;        // this module's config section (env-interpolated)
    AgentPaths Paths;
}
```

`ServiceRegistry` is a name-keyed locator (not a DI framework): modules publish
what they provide (`embedding`, `memory.repository`) and consume what they need.
Memory module publishes `memory` + `embedding`; devtools consumes neither;
future modules consume by name only. String keys keep it simple and JSON-able
for agent-authored modules.

**Manifest** (`module.json` beside the module source, embedded at build):

```json
{
  "id": "web",
  "version": "1.0.0",
  "description": "HTTP fetch + search (jina, duckduckgo, local readability)",
  "kind": "builtin",
  "requires": ["core"],
  "provides": ["tool.web_fetch", "tool.web_search"],
  "config_section": "web",
  "activation": {
    "default": true,
    "model_requestable": true
  }
}
```

Manifest fields do not change between static and DLL deployment (§7) — only
*discovery* changes. That is the compatibility guarantee that keeps the DLL
option cheap later.

**ModuleManager** responsibilities:
- Load all static modules declared in config (order = dependency order, from
  `requires`).
- Scan external module dirs (`<appdata>/modules/`, `agents/<id>/modules/`),
  validate their manifests, and load them too. Same pipeline, different source.
  An external module that fails validation is **refused loudly by name** — it
  must never take the host down or silently vanish.
- `Activate(id)` / `Deactivate(id)` — called by `module_enable`/`module_disable`;
  re-registers/unregisters tools and services on the live `ToolRegistry`.
- Expose `GetActiveTools()` — feeds `ToolRegistry.GetDefinitions()`, so only
  *active* modules' tools are ever sent to the model.
- Validate manifests (unknown `requires`, duplicate tool names, bad config
  section → module fails loudly with a named reason).

---

## 5. Kernel tools (always on, part of Core, never unloadable)

| Tool               | Purpose                                                    |
|--------------------|------------------------------------------------------------|
| `list_modules`     | id, version, active?, model_requestable?, description      |
| `module_enable`    | activate a dormant module (subject to allowlist, §6)        |
| `module_disable`   | deactivate                                                     |
| `module_status`    | why each module is in its state (config default / model)    |

These are how the "model can request a capability" story works: the system
prompt lists *inactive-but-requestable* modules; the model calls
`module_enable("web")`; its tools appear from the next turn.

---

## 6. Activation policy

- Config allowlist: `"modules": { "allow": ["*"], "deny": [] }` — "*" = all
  shipped modules requestable. Per-agent override later via
  `agents/<id>/config` (isolation pattern already exists).
- **No user-approval loop** (your decision): allowlist is the whole gate.
- Costly modules (web → external API spend) are *not* special-cased by default;
  operators tighten with the allowlist if they want that.
- Activation is **logical** (registration) and takes effect **next turn** — no
  mid-turn registry races; planners see module tools automatically because they
  share the ToolRegistry.
- Granularity note: finer control (e.g. "web fetch on, web search off") can be
  added as per-tool activation *within* a module later without changing the
  contract (tool registration already carries the active flag).

---

## 7. Loading strategy: static for shipped, external for everything else

Three different axes are easy to conflate:

| # | Axis                | Answers question                                   | Cost of getting wrong       |
|---|---------------------|----------------------------------------------------|-----------------------------|
| 1 | **Build-time**      | How is source organized? (1 project vs N)          | merge conflicts, dep creep  |
| 2 | **Process-loading** | When is the code mapped into memory? (statically linked into the exe vs DLLs discovered at startup) | startup fragility, ABI break |
| 3 | **Logical activation** | Which tools/services are *registered* at runtime?  | the actual feature we want  |

**The hybrid (locked):**

| Module origin    | Axis 2 mechanism | Why                                          |
|------------------|------------------|----------------------------------------------|
| Shipped builtins (devtools, session, work, memory, web) | **Static** — compiled and linked into the exe | we own them, they move with the binary, zero runtime fragility |
| Agent-authored   | **External** — on-disk module dir, loaded at runtime (script-backed first, compiled DLL a later option) | the agent must be able to grow the host without touching host code |
| Third-party      | **External** — compiled plugin DLLs discovered + loaded at runtime | shipping binary modules without recompiling the host |

Why static for shipped modules specifically: no ABI pinning for code we ship,
no dependency-collision discipline (duplicate SmartReader class identities),
no startup failure mode where a broken module bricks boot. The five current
capabilities get none of the shallow benefits of DLL-ization — they just need
clean project boundaries (axis 1) and logical activation (axis 3).

- **Multi-project is axis 1 only.** The solution can be 4 csprojs and still
  produce **one exe** with everything statically linked. Project boundaries are
  for developers (enforced deps, scoped tests, isolated git history), not for
  the runtime. This is the default .NET build shape.
- **DLL plugin loading is axis 2** — a *deployment* decision: module projects
  compile to `.dll`, the host scans `modules/` next to the exe at startup, and
  instantiates `IModule` implementations via a stable entry convention (a
  well-known static entry / class scan in the DLL). It enables adding binary
  modules without recompiling the host.
- **Model-requested activation is axis 3** and works identically whether axis 2
  is static or dynamic: `module_enable` flips a flag that registers/unregisters
  tools. *The feature you asked for does not require DLL loading.*

Why the external path is still careful (this is the part that *will* need
engineering discipline when it lands):
- **ABI/version pinning**: an external module must match the host contract
exactly; a mismatch fails at runtime, not compile time. Mitigation: manifest
`requires` pins host version; loader validates before touching code paths.
- **Dependency conflicts**: host and module both bundling e.g. SmartReader can
produce duplicate class identity — the loader must isolate module deps from
host deps (layer/shared-lib discipline) or forbid overlapping bundles.
- **Startup failure modes**: a bad module in the folder must be refused by
name in the log, never brick the host boot.
- **In-process security**: an agent-authored *compiled DLL* is arbitrary
native code in-process. The default path for agent modules is therefore
**script-backed and out-of-process** (below). Compiled DLLs are gated for
third-party/signed modules until we decide process isolation is acceptable.

**Trust tiers** (sharp guardrail written into the design):

| Tier | Code                     | Loading                     | Trust        |
|------|--------------------------|-----------------------------|--------------|
| 0    | Host kernel (AgentLoop…) | statically linked, always   | ours         |
| 1    | Shipped modules          | statically linked, config-gated | ours     |
| 2    | Agent-authored modules  | external: manifest + scripts, executed **out-of-process** via `run_command` primitives (same trust as the existing tool) | agent's own machine, already granted |
| 3    | Third-party modules     | external: compiled plugin DLLs, discovered + loaded at runtime | signed/allowlisted only |

Agent-authored code is **never** loaded into the host process by default. If a
later phase adds agent-compiled DLLs, it requires an explicit, separate
decision — not a silent widening of tier 2.

---

## 8. Module inventory & tool map

| Module   | Project     | Tools (today)                              | Services provided        | Config section |
|----------|-------------|--------------------------------------------|--------------------------|-----------------|
| devtools | CleoCore    | run_command, read/write_file, list_directory, grep, get_environment_info, remove/move/rename_file, make/remove_directory, edit_file_inplace | (none)                   | (none — core)   |
| session  | CleoCore    | list/search/read/name_session              | (none)                   | (none)          |
| work     | CleoCore    | work_claim/status/end                      | (none)                   | (none)          |
| memory   | CleoMemory  | memory_write/retrieve/forget/clear         | `memory`, `embedding`    | `modules.memory` |
| web      | CleoWeb     | web_fetch, web_search                      | (none)                   | `modules.web`   |
| *(external)* | on-disk  | whatever the module declares               | whatever it publishes    | own `module.json` |

Config ownership lives with the module: `modules.memory` in CleoMemory's
config surface, `modules.web` in CleoWeb's. **2026-09-13:** the legacy
pre-modular sections (`"embedding"`, `"web"` at root of the config file) are
OBSOLETE and were removed — `modules.<id>` is the only config surface (the
startup deprecation notes are gone with it). The web module reads structured
sub-blocks: `modules.web.fetch.*` / `modules.web.search.*` (dotted accessors
`GetStringAt("fetch", ...)`). The live `%APPDATA%\CleoAgent\config.json` was
migrated in the same change.

---

## 9. Testing strategy

- Each module project owns its self-test (memory keeps `MemorySelfTest`, moved
  to CleoMemory; a minimal web self-test verifies provider factory fallback;
  core tests host + module manager: manifest validation, allowlist,
  activate/deactivate round-trip, no tool-name collision).
- `--selftest` in CleoCLI **dispatches** to every loaded module's self-test and
  reports per-module pass/fail (a module that fails its self-test is refused
  activation at boot — fail-closed).
- All behavior parity gates in the plan below are "build 0/0 + all selftests +
  one live REPL scenario".

---

## 10. Phased plan

**Phase 0 — this document.** (done when you sign off)

**Phase 1 — Multi-project restructure (pure move, zero behavior change)**
- Split into CleoCore / CleoMemory / CleoWeb / CleoCLI; move existing files by
  ownership (imports + csproj deps fixed); also move `CLI/MemorySelfTest.cs`
  into CleoMemory.
- Split `Core/Config/Config.cs` config records per owner; add legacy-key
  fallback with deprecation note.
- Exit gate: `dotnet build` 0/0, `--selftest` all green, one live REPL run
  behaves identically. Single commit, file-move-friendly (`git mv`).

**Phase 2 — Module contract + data-driven composition**
- Add `IModule`/`ModuleManifest`/`ModuleManager`/`ServiceRegistry` + kernel
  tools stubs (status only).
- Convert each capability area into the module wrappers; `Program.cs` becomes:
  load config → build host → `ModuleManager.LoadAll(config)` → run loop.
  No tool behavior changes; all defaults `true`.
- Old hand-wired composition path: was kept as a commented `# Deprecate (date)`
  block per the house rule; **removed 2026-09-13** once the module system had
  been live-verified (Phases 2-4) — the user authorized deleting deprecated
  blocks, so rollback now means the git history, not a comment.
- Exit gate: same as Phase 1, plus `module_status` shows all 5 active and
  definitions equal today's set.

**Phase 3 ✓ Model-requestable activation (implemented 2026-09-13)**
- `list_modules`/`module_enable`/`module_disable` fully wired; definitions
  reflect only active modules; allowlist enforced from `[modules]`.
- Exit gate: live run where the model requests web in-turn and its tools appear
  next turn; another where allowlist denies a module and the tool reports the
  denial clearly.

**Phase 4 ✓ Agent-authored modules (self-extension, external) (implemented 2026-09-13)**
- External module pipeline lands: scan `agents/<id>/modules/<name>/` (and
  host-wide `%APPDATA%\modules/`), validate `module.json`, load **script-backed**
  tools (`impl.command` + JSON schema params), executed out-of-process via the
  same primitives as `run_command` (trust tier 2 — never in-process).
- Authoring guidance appended to the agent persona; manifest carries
  author/created/modified audit fields.
- Exit gate: one live turn where the agent writes a module, installs it, and
  uses its tool (verified live: greet module written turn N, discovered by the
  turn-N+1 boundary rescan, tool called and returned output).

**Phase 5 — (gated) third-party compiled plugins**
- Compiled plugin DLLs discovered + loaded at runtime for **signed/allowlisted
  third-party** modules only (trust tier 3). Same `IModule` contract — discovery
  layer only. Requires the dependency-isolation + version-pinning discipline
  from §7 before this is safe.
- Explicit non-goal: agent-compiled DLLs loaded in-process. Still out of scope
  after this phase unless a separate decision is made.

---

## 11. Open questions — RESOLVED 2026-09-13 (was: Open questions, not blocking)

1. **MongoDB — deferred.** No Mongo work before or during the restructure; the
   restructure is storage-agnostic either way. Carried contract note (decision
   from the same review): `IMemoryRepository` is storage-only — similarity
   search for `memory_retrieve` runs in-process today; a remote repository
   would move search semantics behind the interface, not beside it, so the
   contract docs must state that up front and the Mongo impl (whenever it
   lands) becomes a new `memory.repository` service registration, not a
   refactor.
2. **devtools — permanently on, non-requestable.** It is the base capability;
   activation is logical and the allowlist remains the only gate. The model
   can never re-enable a capability the operator denied — non-requestable
   means precisely that. A chat-only profile is still achievable as an
   *operator* decision via the allowlist (`deny: ["devtools"]`); the flag
   only stops the model from undoing it. System prompt advertises only
   inactive-but-requestable modules, so devtools never appears there.
3. **Per-agent module allowlist — `agents/<id>/config.json`.** Same mechanism
   as the rest of config (strict schema, JSON5, env interpolation, merge with
   host config). Merge rule: per-agent is **narrowing-only** — it can further
   restrict the host `[modules]` allow/deny, never widen it (a per-agent grant
   of something the host denied would reopen the §6 gate). `agent.md` front-
   matter rejected: no new parsing surface; machine policy stays out of prose.
   (Implementation lands Phase 3, as deferred.)
4. **External module dir layout — both.** `%APPDATA%\modules\<id>` (host-wide,
   tier 3 third-party, curated) and `agents/<id>/modules/<id>` (per-agent, tier
   2 agent-authored, isolated) as proposed in §7. Locked at this review:
   **per-agent shadows host-wide on id collision** — scan per-agent first, so
   an experiment can override a shared utility deterministically. Validation
   failures refuse *that agent* by name (never the host boot).

Phase gates adjust only in wording: Phase 3 includes the narrowing-only merge;
Phase 4 includes the shadowing precedence line. No schema or contract changes
to the core design.