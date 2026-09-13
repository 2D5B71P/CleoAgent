using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Config;
using CleoAgent.Core.Tools;

namespace CleoAgent.Core.Modules;

// Per-module handle handed to IModule.LoadAsync: the shared registries, host
// identity/paths, and THIS module's config section. ModuleManager builds one
// context per module (sharing the single ToolRegistry + ServiceRegistry) so
// config ownership stays with the module.
internal sealed class ModuleContext
{
    private readonly ToolRegistry _tools;
    private readonly ServiceRegistry _services;
    private readonly ConfigSection? _config;
    private readonly string _agentId;
    private readonly string _agentsRoot;

    public ModuleContext(
        ToolRegistry tools,
        ServiceRegistry services,
        string agentId,
        string agentsRoot,
        ConfigSection? config = null)
    {
        _tools = tools;
        _services = services;
        _agentId = agentId;
        _agentsRoot = agentsRoot;
        _config = config;
    }

    // The shared tool registry - each module registers its tools here.
    public ToolRegistry Tools => _tools;

    // The shared locator - modules publish services ("memory", "embedding")
    // for other modules / future phases to consume by name.
    public ServiceRegistry Services => _services;

    // This module's config section: the module-owned "modules.<id>" block
    // (2026-09-13: the legacy top-level fallback was removed). Empty when the
    // section is absent, so module code never null-checks it.
    public ConfigSection Config => _config ?? ConfigSection.Empty;

    public string AgentId => _agentId;

    public string AgentsRoot => _agentsRoot;
}