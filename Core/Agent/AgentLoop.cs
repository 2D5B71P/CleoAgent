using CleoAgent.Core.Config;
using CleoAgent.Core.Model;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace CleoAgent.Core.Agent
{
    internal sealed class AgentLoop
    {
        private readonly IModelProvider m__Model;
        private readonly ToolRegistry   m__Tools;
        private readonly string         m__AgentId;

        private readonly Planner         m__Planner;
        private readonly PlanningConfig  m__Planning;
        private readonly SessionIdHandle? m__SessionId;

        private string? m__ContinuationToken;
        private ToolResult? lastResult;

        public AgentLoop(
            IModelProvider modelProvider,
            ToolRegistry tools,
            string? agentId = null,
            SessionIdHandle? sessionId = null,
            PlanningConfig? planning = null)
        {
            m__Model  = modelProvider;
            m__Tools  = tools;
            m__AgentId = string.IsNullOrWhiteSpace(agentId) ? "default" : agentId!;
            m__Planning    = planning ?? PlanningConfig.Default;
            m__SessionId   = sessionId;
            m__Planner     = new Planner(modelProvider, m__AgentId);
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(string input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // -- PLAN phase (deliberate loop) -----------------------------------
            AgentPlan? plan = null;
            int replansUsed = 0;

            if (m__Planning.Enabled)
            {
                plan = await m__Planner.CreatePlanAsync(input, m__Planning.MaxPlanSteps, cancellationToken);

                if (plan is not null)
                {
                    yield return new AgentPlanEvent(plan.Goal, plan.Steps);
                }
                else
                {
                    yield return new AgentError("Planning failed; falling back to reactive loop.");
                }
            }

            // -- act/observe loop -----------------------------------------------
            // The active plan (if any) is embedded into the first user prompt so it
            // lives in the session history and stays visible across tool
            // continuations. Budgets bound the work regardless of plan state.
            AgentPlan? activePlan = plan;
            var observations = new List<string>();
            bool firstPrompt = true;
            string? replanGuidance = null;   // set when we re-issue a prompt after a replan

            while (true)
            {
                ModelRequest request;

                if (replanGuidance is not null)
                {
                    // Re-plan: re-issue a fresh prompt with the revised plan + the
                    // observation that triggered it. The model then continues.
                    string replanPrompt = activePlan is not null
                        ? BuildReplanPrompt(activePlan, replanGuidance)
                        : replanGuidance + "\n\nContinue the task from here.";
                    request = ModelRequest.ForPrompt(
                        replanPrompt,
                        m__Tools.GetDefinitions(), m__ContinuationToken, m__AgentId);
                    replanGuidance = null;
                }
                else if (firstPrompt)
                {
                    string execute = m__Planning.Enabled && activePlan is not null
                        ? BuildExecutionPrompt(input, activePlan)
                        : input;
                    request = ModelRequest.ForPrompt(
                        execute, m__Tools.GetDefinitions(), m__ContinuationToken, m__AgentId);
                    firstPrompt = false;
                }
                else
                {
                    // Feed the previous tool result back for step continuation.
                    request = ModelRequest.ForToolResult(
                        lastResult!, m__Tools.GetDefinitions(), m__ContinuationToken, m__AgentId);
                    lastResult = null;
                }

                var turnText = new StringBuilder();
                ToolCall? pendingToolCall = null;
                bool completed = false;

                await foreach (ModelEvent modelEvent in m__Model.StreamAsync(request, cancellationToken))
                {
                    switch (modelEvent)
                    {
                        case ModelTextDelta text:
                            turnText.Append(text.Text);
                            yield return new AgentTextDelta(text.Text);
                            break;
                        case ModelToolCall toolCall:
                            pendingToolCall = toolCall!.Call;
                            break;
                        case ModelCompleted modelCompleted:
                            m__ContinuationToken = modelCompleted.ContinuationToken;
                            // Mirror the token into the handle so session tools can
                            // resolve the "current" session id (null until the first
                            // completion, so "current" errors before any exchange).
                            if (m__SessionId is not null && modelCompleted.ContinuationToken is not null)
                                m__SessionId.Current = modelCompleted.ContinuationToken;
                            completed = true;
                            break;
                        case ModelError error:
                            yield return new AgentError(error.Message);
                            yield break;
                    }
                }

                if (!completed)
                {
                    yield return new AgentError("Model stream ended without a completion event.");
                    yield break;
                }

                if (pendingToolCall is null)
                {
                    // Final answer OR a model-signal to re-plan (the model writes
                    // "REPLAN: <reason>" instead of answering). Only a replan if we
                    // have budget AND have actually observed something, so a model
                    // can't trivially re-plan before doing any work.
                    string finalText = turnText.ToString();

                    if (activePlan is not null
                        && replansUsed < m__Planning.MaxReplans
                        && observations.Count >= 1
                        && TryParseReplanSignal(finalText, out string? signalReason))
                    {
                        replansUsed++;
                        var revised = await m__Planner.ReplanAsync(
                            activePlan, signalReason!, m__Planning.MaxPlanSteps, cancellationToken);

                        if (revised is not null)
                        {
                            activePlan = revised;
                            observations.Clear();
                            yield return new AgentReplanned(signalReason!);
                            replanGuidance = signalReason!;   // re-issue a fresh prompt
                            continue;
                        }
                    }

                    yield return new AgentCompleted();
                    yield break;
                }

                // --- execute a tool call ---
                if (!m__Tools.TryGetValue(pendingToolCall.Name, out IAgentTool? tool))
                {
                    yield return new AgentError($"Model requested unknown tool '{pendingToolCall.Name}'.");
                    yield break;
                }

                yield return new AgentToolStarted(tool!.Name, tool.Describe(pendingToolCall));
                var execution = await tool.ExecuteAsync(pendingToolCall, cancellationToken);
                bool errored = execution.IsError;

                observations.Add($"Tool {pendingToolCall.Name}{(errored ? " ERRORED" : " ok")}: {Shorten(execution.Output)}");
                yield return new AgentToolCompleted(pendingToolCall.Name, errored);

                // Deterministic re-plan on tool error (if enabled + budget remains).
                if (errored && m__Planning.ReplanOnToolError
                    && activePlan is not null
                    && replansUsed < m__Planning.MaxReplans)
                {
                    replansUsed++;
                    string reason = $"Tool '{pendingToolCall.Name}' errored: {Shorten(execution.Output)}";
                    var revised = await m__Planner.ReplanAsync(
                        activePlan, reason, m__Planning.MaxPlanSteps, cancellationToken);

                    if (revised is not null)
                    {
                        activePlan = revised;
                        observations.Clear();
                        yield return new AgentReplanned(reason);
                        replanGuidance = reason;
                        continue;
                    }
                }

                // Otherwise store the result for the step-continuation turn.
                lastResult = execution;
            }
        }

        public void Reset()
        {
            m__ContinuationToken = null;
            if (m__SessionId is not null)
                m__SessionId.Current = null;
        }

        // Builds the first user prompt for execution: the plan rendered as
        // context, then the original task. The plan rides in the session history,
        // so tool-result continuations (which replay it) keep it visible.
        private static string BuildExecutionPrompt(string input, AgentPlan plan) =>
            "ACTIVE PLAN:\n" + plan.Render() +
            "\n\nExecute the plan above for the user's task. Work step by step " +
            "using tools as needed. If you determine the plan is wrong and you have " +
            "evidence from a tool, answer with a line starting `REPLAN: <reason>` " +
            "and nothing else."
            + "\n\nUSER TASK:\n" + input;

        // Builds a fresh prompt after a re-plan: the revised plan plus the
        // observation/reason, so the model resumes on the corrected course.
        private static string BuildReplanPrompt(AgentPlan plan, string observation) =>
            "REVISED PLAN:\n" + plan.Render() +
            "\n\nReason for revision: " + observation +
            "\n\nContinue the task from here using tools as needed.";

        // A model-signal replan: the model writes a line starting with `REPLAN:`
        // instead of answering. Returns true and the trimmed reason if so.
        private static bool TryParseReplanSignal(string text, out string? reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith("REPLAN:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            reason = trimmed.Length > "REPLAN:".Length
                ? trimmed.Substring("REPLAN:".Length).Trim()
                : "(no reason)";
            return true;
        }

        // Keeps tool observations compact for the replan prompt.
        private static string Shorten(string s, int max = 300)
        {
            if (s is null) return string.Empty;
            s = s.Replace("\n", " ").Replace("\r", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
