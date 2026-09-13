using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Session;

namespace CleoAgent.Core.Tools.Impl
{
    // Lists the agent's persisted conversation sessions with their display
    // metadata: name (if the user named one), created, last activity and
    // message count. Sessions are created per CLI run; name_session gives them
    // a remembered label. Listing is the entry point for finding "other"
    // sessions - the model then reads (read_session_log) or searches
    // (search_session_logs) a specific one.
    internal sealed class ListSessionsTool : IAgentTool
    {
        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "list_sessions";
        public string Description =>
            "Lists this agent's past conversation sessions: id, name (if set), " +
            "when it started, last activity and message count. Use this to find " +
            "which session to read (read_session_log) or search " +
            "(search_session_logs). Sessions are per CLI run; the most recent " +
            "is listed first.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "agent_id": {
              "type": "string",
              "description": "Optional: list another agent's sessions instead of this agent's."
            }
          },
          "additionalProperties": false
        }
        """;

        public ListSessionsTool(string agentId, SessionIdHandle sessionId)
        {
            _agentId = agentId;
            _sessionId = sessionId;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                string agentId = _agentId;
                if (document.RootElement.TryGetProperty("agent_id", out var agentElement))
                {
                    var value = agentElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        agentId = value;
                    }
                }

                var store = new SessionStore(agentId);
                var entries = store.ListSessionEntries().ToList();

                if (entries.Count == 0)
                {
                    return new ToolResult(call.Id, $"No sessions found for agent \"{agentId}\" yet.");
                }

                var sb = new StringBuilder();
                sb.Append(entries.Count).Append(entries.Count == 1 ? " session:" : " sessions:").AppendLine();

                foreach (SessionEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    sb.Append("[session] ").AppendLine(entry.Name ?? "(unnamed)");
                    sb.Append("  id:       ").AppendLine(entry.Id);
                    sb.Append("  created:  ").AppendLine(Format(entry.Created));
                    sb.Append("  last:     ").AppendLine(Format(entry.LastActivity));
                    sb.Append("  messages: ").Append(entry.MessageCount).AppendLine();
                }

                return new ToolResult(call.Id, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? agentId = GetStringArgument(call, "agent_id");
            return string.IsNullOrWhiteSpace(agentId) || agentId == _agentId
                ? "Listing sessions"
                : $"Listing sessions for {agentId}";
        }

        private static string Format(DateTimeOffset? time)
        {
            // DateTimeOffset.ToString() renders local wall time for UTC values
            // when the offset matches the machine zone; for others it shows the
            // offset, which is honest for cross-agent listings.
            return time?.ToString() ?? "(unknown)";
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