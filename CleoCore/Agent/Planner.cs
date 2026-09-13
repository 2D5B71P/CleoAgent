using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Model;

namespace CleoAgent.Core.Agent;

// Generates and revises structured plans using a model call (CompleteAsync, so
// it is a non-tool, ephemeral exchange). Plans are returned as strict JSON:
//
//   { "goal": "...", "steps": [ { "id": 1, "summary": "...", "expects": "..." } ] }
//
// Parsing is defensive: fence-wrapped, stray prose, and missing fields all
// degrade to null/partial rather than throwing.
internal sealed class Planner
{
    private readonly IModelProvider _model;
    private readonly string _agentId;

    public Planner(IModelProvider model, string agentId)
    {
        _model = model;
        _agentId = agentId;
    }

    // Creates an initial plan for `task` (a user turn), capped at maxSteps.
    public async Task<AgentPlan?> CreatePlanAsync(string task, int maxSteps, CancellationToken ct)
    {
        string prompt = BuildCreatePrompt(task, maxSteps);
        string? reply = await _model.CompleteAsync(prompt, _agentId, ct);
        return string.IsNullOrWhiteSpace(reply) ? null : ParsePlan(reply, maxSteps);
    }

    // Re-plans: given the current plan plus an observation/error, produces a
    // revised plan that keeps the same goal. maxSteps caps the new plan.
    public async Task<AgentPlan?> ReplanAsync(
        AgentPlan current,
        string observation,
        int maxSteps,
        CancellationToken ct)
    {
        string prompt = BuildReplanPrompt(current, observation, maxSteps);
        string? reply = await _model.CompleteAsync(prompt, _agentId, ct);
        return string.IsNullOrWhiteSpace(reply) ? null : ParsePlan(reply, maxSteps);
    }

    private static string BuildCreatePrompt(string task, int maxSteps) =>
        "You are a planning component for an AI agent. Break the following task into a " +
        $"concise plan of at most {maxSteps} steps. Each step is a short checkpoint, not a " +
        "tool call; the agent decides tools during execution. Keep the goal explicit.\n\n" +
        "Return ONLY strict JSON:\n" +
        "{\"goal\":\"...\", \"steps\":[{\"id\":1,\"summary\":\"...\",\"expects\":\"...\"}]}\n" +
        "No markdown, no prose.\n\n--- task ---\n" + task + "\n--- end ---";

    private static string BuildReplanPrompt(AgentPlan current, string observation, int maxSteps) =>
        "You are a planning component for an AI agent. The plan below made progress but an " +
        "observation or error requires revising the REMAINING work. Keep the same goal; " +
        $"produce a revised plan of at most {maxSteps} steps.\n\n" +
        "Return ONLY strict JSON:\n" +
        "{\"goal\":\"...\", \"steps\":[{\"id\":1,\"summary\":\"...\",\"expects\":\"...\"}]}\n" +
        "No markdown, no prose.\n\n--- current plan ---\n" + current.Render() +
        "\n\n--- observation / error ---\n" + observation +
        "\n\n--- end ---";

    private static AgentPlan? ParsePlan(string reply, int maxSteps)
    {
        string json = ExtractJson(reply);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string goal = root.TryGetProperty("goal", out JsonElement g) ? g.GetString() ?? "" : "";

            var steps = new List<PlanStep>();

            if (root.TryGetProperty("steps", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (JsonElement item in s.EnumerateArray())
                {
                    if (steps.Count >= maxSteps) break;
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string summary = item.TryGetProperty("summary", out JsonElement su) ? su.GetString() ?? "" : "";
                    string expects = item.TryGetProperty("expects", out JsonElement ex) ? ex.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(summary)) continue;

                    int id = item.TryGetProperty("id", out JsonElement idp) && idp.ValueKind == JsonValueKind.Number
                        ? idp.GetInt32() : idx;

                    steps.Add(new PlanStep(id, summary, string.IsNullOrWhiteSpace(expects) ? null : expects));
                    idx++;
                }
            }

            if (steps.Count == 0)
            {
                return null;
            }

            return new AgentPlan(string.IsNullOrWhiteSpace(goal) ? "(no goal)" : goal, steps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Pulls the first {...} object out of a model reply, tolerating code fences
    // and stray prose (mirrors MemorySummarizer's tolerant extraction).
    private static string ExtractJson(string reply)
    {
        string trimmed = reply.Trim();

        int fence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            trimmed = trimmed.Substring(fence + 3);
            int fenceEnd = trimmed.IndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) trimmed = trimmed.Substring(0, fenceEnd);
        }

        int start = trimmed.IndexOf('{');
        if (start < 0) return trimmed;
        int end = trimmed.LastIndexOf('}');
        if (end <= start) return trimmed;

        return trimmed.Substring(start, end - start + 1);
    }
}