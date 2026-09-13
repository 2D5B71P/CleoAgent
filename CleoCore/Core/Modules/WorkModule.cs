using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.Core.Modules;

// The work core module: the project-local claim board (work_claim/status/end)
// that keeps concurrent sessions from silently colliding on the same files.
internal sealed class WorkModule : IModule
{
    private readonly string _agentId;
    private readonly SessionIdHandle _sessionId;

    public WorkModule(string agentId, SessionIdHandle sessionId)
    {
        _agentId = agentId;
        _sessionId = sessionId;
    }

    public ModuleManifest Manifest() => ModuleManifest.Create(
        "work",
        "1.0.0",
        "Concurrent-session coordination: work_claim, work_status, work_end " +
        "(project-local edit-collision blackboard).");

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        ctx.Tools.Register(new WorkClaimTool(_agentId, _sessionId));
        ctx.Tools.Register(new WorkStatusTool());
        ctx.Tools.Register(new WorkEndTool(_agentId, _sessionId));
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        // work is a kernel module; deactivation is not supported (Phase 3).
    }
}