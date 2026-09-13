using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Session;

namespace CleoAgent.Core.Tools.Impl
{
    // Full-text search across the agent's session logs (message content, tool
    // outputs, and session names). This is the content-first way to find a
    // past conversation: the user says what they remember, the model searches,
    // then reads the matching session with read_session_log. Date filtering
    // uses each session file's last-write time (the only temporal signal in
    // the JSONL logs, which carry no per-message timestamps).
    internal sealed class SearchSessionLogsTool : IAgentTool
    {
        private static readonly int MaxHitsDefault = 20;
        private static readonly int MaxHitsMax     = 100;

        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "search_session_logs";
        public string Description =>
            "Searches this agent's session logs for text (case-insensitive): " +
            "message content, tool outputs and session names. Optionally narrow " +
            "to one session or to a date range (sessions whose last activity " +
            "falls between 'from' and 'to', yyyy-mm-dd). Use this to find a past " +
            "conversation when you remember what was said, not its id.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Text to find."
            },
            "session_id": {
              "type": "string",
              "description": "Optional: only search this session. Accepts \"current\" for the active session."
            },
            "agent_id": {
              "type": "string",
              "description": "Optional: search another agent's logs instead of this agent's."
            },
            "from": {
              "type": "string",
              "description": "Optional: only sessions with last activity on/after this date (yyyy-mm-dd)."
            },
            "to": {
              "type": "string",
              "description": "Optional: only sessions with last activity on/before this date (yyyy-mm-dd)."
            },
            "max_hits": {
              "type": "integer",
              "description": "Optional: max matches to return (1-100, default 20)."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

        public SearchSessionLogsTool(string agentId, SessionIdHandle sessionId)
        {
            _agentId = agentId;
            _sessionId = sessionId;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("query", out var queryElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: query", true);
                }
                var query = queryElement.GetString();
                if (string.IsNullOrWhiteSpace(query))
                {
                    return new ToolResult(call.Id, "Query cannot be empty.", true);
                }
                query = query.Trim();

                string agentId = _agentId;
                if (document.RootElement.TryGetProperty("agent_id", out var agentElement))
                {
                    var value = agentElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        agentId = value;
                    }
                }

                string? sessionId = null;
                if (document.RootElement.TryGetProperty("session_id", out var sessionElement))
                {
                    sessionId = sessionElement.GetString();
                }

                // Date bounds as day-keys (yyyy*10000+mm*100+dd); the nullable
                // DateTime never crosses a call boundary, which keeps this
                // compiler's flow analysis happy.
                int? fromKey = null;
                int? toKey = null;
                if (document.RootElement.TryGetProperty("from", out var fromElement))
                {
                    fromKey = ParseDayKey(fromElement.GetString(), "from");
                }
                if (document.RootElement.TryGetProperty("to", out var toElement))
                {
                    toKey = ParseDayKey(toElement.GetString(), "to");
                }

                int maxHits = System.Math.Clamp(ReadIntArgument(call, "max_hits", MaxHitsDefault), 1, MaxHitsMax);

                var store = new SessionStore(agentId);

                // Target sessions: one explicit id, or all sessions (newest first).
                var entries = new List<SessionEntry>();
                if (sessionId is not null)
                {
                    string? resolved = ResolveSessionId(sessionId);

                    if (resolved is null)
                    {
                        return new ToolResult(call.Id,
                            "No current session yet - the session id is only known after the first exchange in this run.", true);
                    }

                    if (!File.Exists(store.SessionFilePath(resolved)))
                    {
                        return new ToolResult(call.Id, $"Session not found: {resolved}", true);
                    }

                    var entry = store.GetEntry(resolved);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                else
                {
                    entries = store.ListSessionEntries().ToList();
                }

                var sb = new StringBuilder();
                int hits = 0;

                foreach (SessionEntry entry in entries)
                {
                    if (hits >= maxHits)
                    {
                        break;
                    }

                    string file = store.SessionFilePath(entry.Id);

                    // Date filter: file last-write time (local wall date) vs the
                    // requested day range, compared as day-keys.
                    if (fromKey is not null || toKey is not null)
                    {
                        int lastKey = DateKey(File.GetLastWriteTime(file));

                        if (fromKey is not null && lastKey < fromKey)
                        {
                            continue;
                        }
                        if (toKey is not null && lastKey > toKey)
                        {
                            continue;
                        }
                    }

                    // Session-name matches count as hits too.
                    if (entry.Name is not null
                        && entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendSessionHeader(sb, entry);
                        sb.Append("  [name] ").AppendLine(OneLine(entry.Name));
                        hits++;
                        if (hits >= maxHits)
                        {
                            break;
                        }
                    }

                    int lineIndex = 0;
                    await foreach (string line in File.ReadLinesAsync(file, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lineIndex++;

                        if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AppendSessionHeader(sb, entry);
                        sb.Append("  ").Append(lineIndex).Append(" ").AppendLine(FormatLine(line));
                        hits++;
                        if (hits >= maxHits)
                        {
                            break;
                        }
                    }

                    if (hits >= maxHits)
                    {
                        break;
                    }
                }

                if (hits == 0)
                {
                    return new ToolResult(call.Id, $"(no matches for \"{OneLine(query)}\")");
                }

                if (hits >= maxHits)
                {
                    sb.AppendLine($"(hit the {maxHits} match cap; narrow the query or use session_id to dig further)");
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
            string? query = GetStringArgument(call, "query");
            return string.IsNullOrWhiteSpace(query)
                ? "Searching session logs"
                : $"Searching session logs for \"{OneLine(query)}\"";
        }

        // "current"/null/missing resolves to the live run's session; anything
        // else passes through.
        private string? ResolveSessionId(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Trim() == "current")
            {
                return _sessionId.Current;
            }
            return sessionId.Trim();
        }

        private static void AppendSessionHeader(StringBuilder sb, SessionEntry entry)
        {
            sb.Append("[session] ").Append(entry.Id);
            if (entry.Name is not null)
            {
                sb.Append(" \"").Append(OneLine(entry.Name)).Append('"');
            }
            sb.AppendLine();
        }

        // Renders a matching JSONL line human-readably: prefer message content
        // (tagged by role), else tool output, else the raw line. Cap length for
        // context safety.
        private static string FormatLine(string line)
        {
            string? text = null;
            string tag = "[msg]";

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("Content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(content.GetString()))
                {
                    text = content.GetString();

                    // "Type" is serialized as the enum ordinal (0 user,
                    // 1 assistant, 2 system); unknown -> generic tag.
                    if (root.TryGetProperty("Type", out var type)
                        && type.ValueKind == JsonValueKind.Number)
                    {
                        switch (type.GetInt32())
                        {
                            case 0: tag = "[user]"; break;
                            case 1: tag = "[assistant]"; break;
                            case 2: tag = "[system]"; break;
                        }
                    }
                }
                else if (root.TryGetProperty("Output", out var output)
                    && output.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(output.GetString()))
                {
                    text = output.GetString();
                    tag = "[tool out]";
                }
            }
            catch
            {
                // fall through to raw line
            }

            return tag + " " + OneLine(text ?? line);
        }

        // "yyyy-mm-dd" -> day-key (yyyy*10000+mm*100+dd); null/blank -> null;
        // garbage throws so the caller surfaces a clear error.
        private static int? ParseDayKey(string? text, string name)
        {
            if (text is null || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var parts = text.Trim().Split("-");
            if (parts.Length != 3 || parts[0].Length != 4 || parts[1].Length != 2 || parts[2].Length != 2)
            {
                throw new FormatException($"'{name}' must be yyyy-mm-dd.");
            }

            return int.Parse(parts[0]) * 10000 + int.Parse(parts[1]) * 100 + int.Parse(parts[2]);
        }

        // Local wall-date as a sortable integer (yyyy*10000 + mm*100 + dd).
        private static int DateKey(DateTime date)
        {
            return date.Year * 10000 + date.Month * 100 + date.Day;
        }

        internal static string OneLine(string s)
        {
            var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
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