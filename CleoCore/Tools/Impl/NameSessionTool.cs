using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Session;

namespace CleoAgent.Core.Tools.Impl
{
    // Assigns a human-readable label to a session so list_sessions (and
    // search_session_logs, which also matches names) can find it by what it
    // was about, not by its opaque id. Naming is scoped to this agent's own
    // sessions. Empty name clears the label back to the auto one.
    internal sealed class NameSessionTool : IAgentTool
    {
        private static readonly int NameMax = 200;

        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "name_session";
        public string Description =>
            "Names a session so it can be found later by label. Accepts the " +
            "active session as \"current\" or a past session's id (from " +
            "list_sessions/search_session_logs). An empty name clears the " +
            "label. Names show up in list_sessions and are searchable via " +
            "search_session_logs.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "session_id": {
              "type": "string",
              "description": "The session to name. Accepts \"current\" for the active session."
            },
            "name": {
              "type": "string",
              "description": "The label to remember it by. Empty string clears it."
            }
          },
          "required": ["session_id", "name"],
          "additionalProperties": false
        }
        """;

        public NameSessionTool(string agentId, SessionIdHandle sessionId)
        {
            _agentId = agentId;
            _sessionId = sessionId;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("session_id", out var sessionElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: session_id", true);
                }

                if (!document.RootElement.TryGetProperty("name", out var nameElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: name", true);
                }

                string? sessionId = ResolveSessionId(sessionElement.GetString());
                if (sessionId is null)
                {
                    return new ToolResult(call.Id,
                        "No current session yet - the session id is only known after the first exchange in this run.", true);
                }

                string? name = nameElement.GetString();
                if (name is not null && name.Trim().Length > NameMax)
                {
                    return new ToolResult(call.Id, $"Name too long (max {NameMax} characters).", true);
                }

                var store = new SessionStore(_agentId);

                bool found = store.SetName(sessionId, name);
                if (!found)
                {
                    return new ToolResult(call.Id, $"Session not found: {sessionId}", true);
                }

                string? cleaned = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

                return new ToolResult(call.Id, cleaned is null
                    ? $"Cleared name for session {sessionId}."
                    : $"Named session {sessionId} \"{cleaned}\".");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? sessionId = GetStringArgument(call, "session_id");
            return string.IsNullOrWhiteSpace(sessionId)
                ? "Naming a session"
                : sessionId == "current"
                    ? "Naming the current session"
                    : $"Naming session {sessionId}";
        }

        private string? ResolveSessionId(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Trim() == "current")
            {
                return _sessionId.Current;
            }
            return sessionId.Trim();
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