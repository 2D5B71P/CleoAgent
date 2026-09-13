using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Modules;

// A capability module: owns a set of tools/services/context sources and
// registers them with the host at load time. Static built-in modules
// implement this contract directly; external (agent-authored / third-party)
// modules will surface behind the same interface via ModuleManager scanning.
internal interface IModule
{
    ModuleManifest Manifest();

    // Register tools + services into ctx (the shared ToolRegistry /
    // ServiceRegistry) and do any one-time setup. Called in dependency order.
    Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default);

    // Reverse of LoadAsync: unregister what was registered. Next turn's
    // definitions drop them. May be a no-op for stateless modules.
    Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default);
}