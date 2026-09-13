using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Session;

namespace CleoAgent.Core.Tools.Impl
{
    // Reads one session's conversation history from its JSONL log, oldest
    // first, with paging. The session id comes from list_sessions or
    // search_session_logs; "current" refers to the conversation in progress.
    internal sealed class ReadSessionLogTool : IAgentTool
    {
        private static readonly int LimitDefault = 50;
        private static readonly int LimitMax     = 500;

        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "read_session_log";
        public string Description =>
            "Reads a past session's conversation history, oldest first: user, " +
            "assistant, system and tool messages. Use the id from list_sessions " +
            "or search_session_logs; \"current\" reads the active session. Paged " +
            "with limit/offset for long conversations.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "session_id": {
              "type": "string",
              "description": "The session to read. Accepts \"current\" for the active session."
            },
            "agent_id": {
              "type": "string",
              "description": "Optional: read another agent's session instead of this agent's."
            },
            "limit": {
              "type": "integer",
              "description": "Optional: max messages to return (1-500, default 50)."
            },
            "offset": {
              "type": "integer",
              "description": "Optional: skip this many messages from the start (default 0)."
            }
          },
          "required": ["session_id"],
          "additionalProperties": false
        }
        """;

        public ReadSessionLogTool(string agentId, SessionIdHandle sessionId)
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
                string? sessionId = sessionElement.GetString();

                string? resolvedId = ResolveSessionId(sessionId);
                if (resolvedId is null)
                {
                    return new ToolResult(call.Id,
                        "No current session yet - the session id is only known after the first exchange in this run.", true);
                }

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
                if (!File.Exists(store.SessionFilePath(resolvedId)))
                {
                    return new ToolResult(call.Id, $"Session not found: {resolvedId}", true);
                }

                int limit  = System.Math.Clamp(ReadIntArgument(call, "limit", LimitDefault), 1, LimitMax);
                int offset = System.Math.Max(ReadIntArgument(call, "offset", 0), 0);

                var record = store.Load(resolvedId);
                var messages = record.History;

                int start = System.Math.Min(offset, messages.Count);
                int end   = System.Math.Min(start + limit, messages.Count);

                var entry = store.GetEntry(resolvedId);

                var sb = new StringBuilder();
                sb.Append("[session] ").Append(resolvedId);
                if (entry is not null && entry.Name is not null)
                {
                    sb.Append(" \"").Append(entry.Name).Append('"');
                }
                sb.AppendLine();
                sb.Append("— ").Append(messages.Count).Append(messages.Count == 1 ? " message" : " messages");
                sb.Append(end > start ? $" (showing {start + 1}–{end})" : " (no messages in range)").AppendLine();

                for (int i = start; i < end; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sb.Append("  [").Append(i + 1).Append("] ").AppendLine(FormatMessage(messages[i]));
                }

                if (end < messages.Count)
                {
                    sb.Append("(more below - use offset=").Append(end).AppendLine(" to continue)");
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
            string? sessionId = GetStringArgument(call, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return "Reading a session log";
            }
            return sessionId == "current"
                ? "Reading the current session log"
                : $"Reading session log {sessionId}";
        }

        private string? ResolveSessionId(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Trim() == "current")
            {
                return _sessionId.Current;
            }
            return sessionId.Trim();
        }

        internal static string FormatMessage(SessionMessage message)
        {
            const int cap = 600;

            switch (message.Type)
            {
                case SessionMessageType.User:
                    return "[user] " + OneLine(message.Content, cap);

                case SessionMessageType.Assistant:
                    return "[assistant] " + OneLine(message.Content, cap);

                case SessionMessageType.System:
                    return "[system] " + OneLine(message.Content, cap);

                case SessionMessageType.FunctionCall:
                    return "[tool call] " + OneLine(message.Name ?? "(?)", 80)
                        + "(" + OneLine(message.Arguments, cap) + ")";

                case SessionMessageType.FunctionOutput:
                    return "[tool out] " + OneLine(message.Output, cap);

                default:
                    return "[msg] " + OneLine(message.Content, cap);
            }
        }

        internal static string OneLine(string? s, int cap)
        {
            if (s is null)
            {
                return "(null)";
            }

            var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= cap ? flat : flat.Substring(0, cap) + "…";
        }

        internal static string? GetStringArgument(ToolCall call, string name)
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

        internal static int ReadIntArgument(ToolCall call, string name, int fallback)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                if (document.RootElement.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number)
                {
                    return element.GetInt32();
                }
            }
            catch
            {
                // fall through
            }

            return fallback;
        }
    }
}