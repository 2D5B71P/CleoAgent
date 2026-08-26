using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Agent
{
    internal abstract record AgentEvent;

    internal sealed record AgentTextDelta(
        string Text) : AgentEvent;

    internal sealed record AgentToolStarted(
        string ToolName,
        string ToolDescription) : AgentEvent;

    internal sealed record AgentCompleted : AgentEvent;

    internal sealed record AgentToolCompleted(
        string ToolName,
        bool IsError) : AgentEvent;

    internal sealed record AgentError(
        string Message) : AgentEvent;
}
