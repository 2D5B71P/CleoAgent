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

    // This module's config section (module-owned keys first, legacy top-level
    // section as fallback). Empty when the manifest declares no config surface,
    // so module code never null-checks it.
    public ConfigSection Config => _config ?? ConfigSection.Empty;

    public string AgentId => _agentId;

    public string AgentsRoot => _agentsRoot;
}