using System;

namespace CleoAgent.Core.Session;

// Mutable holder for the CURRENT run's live session id. The id only exists
// after the first model completion, so session tools (which are constructed
// once at startup) resolve the "current" pseudo-id through this handle instead
// of holding plumbing details. AgentLoop updates it on every completion and
// clears it on Reset(). Not thread-safe by design; the CLI is single-threaded.
internal sealed class SessionIdHandle
{
    public string? Current { get; set; }
}