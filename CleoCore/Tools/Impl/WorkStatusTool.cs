using System;
using System.Collections.Generic;
using System.Text.Json;
using CleoAgent.Core.Work;

namespace CleoAgent.Core.Tools.Impl
{
    // Lists every session's claim on a project's coordination board, newest
    // heartbeat first, with staleness computed against now. Read-only: no
    // claim is ever written here. The output exists so sessions see each
    // other before editing - the whole point of the board.
    internal sealed class WorkStatusTool : IAgentTool
    {
        public string Name => "work_status";
        public string Description =>
            "Lists who is working on a project right now by reading its " +
            "coordination board (agent_work/ in the project folder). Call it " +
            "BEFORE editing files in a project you share with other sessions: " +
            "it shows each active session, what task it is doing, which files " +
            "it has claimed, and when it was last heard from. Claims whose " +
            "heartbeat is older than 30 minutes are STALE - their owner is " +
            "gone and the files can safely be taken over. The board is " +
            "advisory: it prevents collisions through awareness, not locking.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Path to the project folder whose agent_work board to read."
            }
          },
          "required": ["project"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                string? project = document.RootElement.TryGetProperty("project", out var projectElement)
                    ? projectElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(project))
                {
                    return new ToolResult(call.Id, "Missing required argument: project", true);
                }

                var claims = WorkStateStore.ReadClaims(project);

                if (claims.Count == 0)
                {
                    return new ToolResult(call.Id,
                        $"The board is empty: no session is claiming work in {WorkStateStore.DirectoryPath(project)}. Safe to edit freely.");
                }

                var report = new List<string>
                {
                    $"{claims.Count} claim(s) on {WorkStateStore.DirectoryPath(project)}:"
                };
                foreach (var c in claims)
                {
                    report.Add("  " + WorkClaimTool.ClaimLine(c));
                    if (c.Note is not null && !string.IsNullOrWhiteSpace(c.Note))
                    {
                        report.Add("      note: " + c.Note);
                    }
                }
                report.Add("");
                report.Add("STALE claims (heartbeat older than 30 min) are abandoned - their owner");
                report.Add("is gone and anyone may take the claimed files over.");

                return new ToolResult(call.Id, String.Join("\n", report));
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? project = GetStringArgument(call, "project");
            return string.IsNullOrWhiteSpace(project)
                ? "Checking who is working on a project"
                : $"Checking who is working in {WorkClaimTool.Truncate(project, 60)}";
        }

        private static string? GetStringArgument(ToolCall call, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return document.RootElement.TryGetProperty(name, out var element)
                    ? element.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}