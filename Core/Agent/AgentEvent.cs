using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Agent
{
    internal abstract record AgentEvent;

    internal sealed record AgentTextDelta(
        string Text) : AgentEvent;

    // A structured plan was produced for the current turn (planning loop).
    internal sealed record AgentPlanEvent(
        string Goal,
        IReadOnlyList<PlanStep> Steps) : AgentEvent;

    // A plan step is about to start (or ended) — surfaced for UX/debugging.
    internal sealed record AgentStepStarted(
        int StepId,
        string Summary) : AgentEvent;

    internal sealed record AgentStepCompleted(
        int StepId,
        bool IsError) : AgentEvent;

    // The agent changed approach mid-plan (model-signal or error-triggered).
    internal sealed record AgentReplanned(
        string Reason) : AgentEvent;

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
