using System;
using System.Text.Json;
using CleoAgent.Core.Session;
using CleoAgent.Core.Work;

namespace CleoAgent.Core.Tools.Impl
{
    // Releases THIS session's claim on a project's coordination board by
    // deleting its own agent_work/<sessionId>.md file. Only ever touches the
    // caller's own file - other sessions' claims are never modified here.
    internal sealed class WorkEndTool : IAgentTool
    {
        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "work_end";
        public string Description =>
            "Releases this session's claim on a project's coordination board " +
            "(agent_work/ in the project folder): call it when you are done " +
            "with a task so other sessions stop seeing your files as " +
            "claimed. Only ever affects your own session's claim - nobody " +
            "else's. No-op (with a message) if you have no claim there.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Path to the project folder whose agent_work board to release the claim from."
            }
          },
          "required": ["project"],
          "additionalProperties": false
        }
        """;

        public WorkEndTool(string agentId, SessionIdHandle sessionId)
        {
            _agentId = agentId;
            _sessionId = sessionId;
        }

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

                string? sessionId = _sessionId.Current;
                if (sessionId is null)
                {
                    return new ToolResult(call.Id,
                        "No current session yet - the session id is only known after the first exchange in this run.", true);
                }

                var store = new WorkStateStore(_agentId);
                bool released = store.DeleteClaim(project, sessionId);

                return new ToolResult(call.Id, released
                    ? $"Released this session's claim in {project}."
                    : $"No claim to release: this session has no file at {WorkStateStore.DirectoryPath(project)}.");
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
                ? "Releasing a work claim"
                : $"Releasing work claim in {WorkClaimTool.Truncate(project, 60)}";
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