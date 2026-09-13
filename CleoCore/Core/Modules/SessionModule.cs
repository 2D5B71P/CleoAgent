using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.Core.Modules;

// The session core module: session log discovery (list/search/read/name).
// Wraps the host session store; needs the LIVE session id holder so tools can
// resolve the "current" pseudo-id.
internal sealed class SessionModule : IModule
{
    private readonly string _agentId;
    private readonly SessionIdHandle _sessionId;

    public SessionModule(string agentId, SessionIdHandle sessionId)
    {
        _agentId = agentId;
        _sessionId = sessionId;
    }

    public ModuleManifest Manifest() => ModuleManifest.Create(
        "session",
        "1.0.0",
        "Session log discovery: list_sessions, search_session_logs, " +
        "read_session_log, name_session.");

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        ctx.Tools.Register(new ListSessionsTool(_agentId, _sessionId));
        ctx.Tools.Register(new SearchSessionLogsTool(_agentId, _sessionId));
        ctx.Tools.Register(new ReadSessionLogTool(_agentId, _sessionId));
        ctx.Tools.Register(new NameSessionTool(_agentId, _sessionId));
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        // session is a kernel module; deactivation is not supported (Phase 3).
    }
}