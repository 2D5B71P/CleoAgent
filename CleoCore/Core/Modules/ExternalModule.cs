using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.Core.Modules;

// One script-backed tool declared by an external module's module.json:
// the model-facing name/description/JSON-schema params, plus the OUT-OF-
// PROCESS execution template (impl.command argv; element 0 = executable).
internal sealed record ExternalToolSpec(
    string Name,
    string Description,
    string ParametersJson,
    IReadOnlyList<string> Command);

// An agent-authored external module discovered on disk (design s7, trust
// tier 2): module.json + scripts on disk, loaded WITHOUT compiling anything.
// Every tool is script-backed and executed OUT-OF-PROCESS (ScriptBackedTool
// spawns impl.command with the same Process primitives as run_command) - the
// agent's own code never runs inside the host process, by design.
internal sealed class ExternalModule : IModule
{
    private readonly ModuleManifest _manifest;
    private readonly IReadOnlyList<ExternalToolSpec> _tools;
    private readonly string _dir;

    public ExternalModule(ModuleManifest manifest, IReadOnlyList<ExternalToolSpec> tools, string dir)
    {
        _manifest = manifest;
        _tools = tools;
        _dir = dir;
    }

    public ModuleManifest Manifest() => _manifest;

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        foreach (ExternalToolSpec tool in _tools)
        {
            ctx.Tools.Register(new ScriptBackedTool(tool, _dir));
        }
    }

    // Script-backed modules are stateless: tool unregistration is driven by
    // ModuleManager's ownership tracking (DeactivateModule), nothing to undo
    // in-process.
    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
    }
}