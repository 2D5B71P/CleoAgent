using System;
using System.Collections.Generic;

namespace CleoAgent.Core.Agent;

// A structured step in an agent's plan. Kept deliberately small: the model
// decides the concrete tool/args at execution time; the step is a checkpoint
// that lets the loop bound work and decide when to re-observe or re-plan.
internal sealed record PlanStep(
    int Id,
    string Summary,      // what this step is trying to accomplish
    string? Expects);    // optional: what "done" for this step looks like

internal sealed record AgentPlan(
    string Goal,
    IReadOnlyList<PlanStep> Steps)
{
    // Renders the plan as a compact, model-friendly text block injected into
    // the execution context.
    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("GOAL: ").AppendLine(Goal);
        for (int i = 0; i < Steps.Count; i++)
        {
            sb.Append("  ").Append(Steps[i].Id).Append(". ").Append(Steps[i].Summary);
            if (!string.IsNullOrWhiteSpace(Steps[i].Expects))
                sb.Append("  [expects: ").Append(Steps[i].Expects).Append(']');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
