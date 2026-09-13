using System;
using System.Collections.Generic;
using System.Text.Json;
using CleoAgent.Core.Session;
using CleoAgent.Core.Work;

namespace CleoAgent.Core.Tools.Impl
{
    // Claims (or refreshes) THIS session's slot on a project's coordination
    // board: "I am working on task X, touching files Y". Other sessions read
    // the board (work_status) before touching the same files, so claims
    // prevent edit collisions through awareness. The claim is advisory, keyed
    // to the session id (two sessions of the same agent stay distinct
    // writers), and expires by heartbeat: claiming again refreshes it.
    //
    // The response doubles as the awareness payload: it reports who else is
    // active on the board and warns specifically when the files just claimed
    // overlap with another session's ACTIVE claim.
    internal sealed class WorkClaimTool : IAgentTool
    {
        private static readonly int TaskMax = 300;
        private static readonly int NoteMax = 2000;

        private readonly string _agentId;
        private readonly SessionIdHandle _sessionId;

        public string Name => "work_claim";
        public string Description =>
            "Claims or refreshes this session's slot on a project's " +
            "coordination board (agent_work/ in the project folder). Call it " +
            "when you start working in a project, naming the task and the " +
            "files you will touch, so other sessions see you there and avoid " +
            "colliding with your edits. Files is a comma-separated list " +
            "(\"p.py, utils.py\") or empty. Claiming again refreshes your " +
            "heartbeat. The board is advisory - awareness, not locking - and " +
            "claims older than 30 minutes are stale (owner gone, safe to " +
            "take over). Responses also list other active sessions and warn " +
            "about file overlaps.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Path to the project folder whose agent_work board to claim on."
            },
            "task": {
              "type": "string",
              "description": "One-line description of what this session is doing (max 300 chars)."
            },
            "files": {
              "type": "string",
              "description": "Comma-separated list of files this session will touch, or empty."
            },
            "note": {
              "type": "string",
              "description": "Optional free-form note for other sessions (max 2000 chars)."
            }
          },
          "required": ["project", "task"],
          "additionalProperties": false
        }
        """;

        public WorkClaimTool(string agentId, SessionIdHandle sessionId)
        {
            _agentId = agentId;
            _sessionId = sessionId;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                string? project = OptionalString(document.RootElement, "project");
                string? task = OptionalString(document.RootElement, "task");
                string? files = OptionalString(document.RootElement, "files");
                string? note = OptionalString(document.RootElement, "note");

                if (string.IsNullOrWhiteSpace(project))
                {
                    return new ToolResult(call.Id, "Missing required argument: project", true);
                }
                if (string.IsNullOrWhiteSpace(task))
                {
                    return new ToolResult(call.Id, "Missing required argument: task", true);
                }
                if (task.Trim().Length > TaskMax)
                {
                    return new ToolResult(call.Id, $"Task too long (max {TaskMax} characters).", true);
                }
                if (note is not null && note.Length > NoteMax)
                {
                    return new ToolResult(call.Id, $"Note too long (max {NoteMax} characters).", true);
                }

                string? sessionId = _sessionId.Current;
                if (sessionId is null)
                {
                    return new ToolResult(call.Id,
                        "No current session yet - the session id is only known after the first exchange in this run.", true);
                }

                var fileList = new List<string>();
                if (files is not null)
                {
                    foreach (string f in files.Split(","))
                    {
                        string cleaned = f.Trim();
                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            fileList.Add(cleaned);
                        }
                    }
                }

                var store = new WorkStateStore(_agentId);
                store.WriteClaim(project, sessionId, task.Trim(), fileList, note);

                // Awareness payload: who else is on the board, and does this
                // claim collide with an ACTIVE claim on the same files?
                var all = WorkStateStore.ReadClaims(project);
                var others = all.Where(c => c.SessionId != sessionId).ToList();

                var report = new List<string>
                {
                    $"Claimed \"{task.Trim()}\" in {project}",
                    $"Session {sessionId} · agent {_agentId}" +
                        (fileList.Count > 0 ? $" · files: {String.Join(", ", fileList)}" : ""),
                    "Heartbeat: now (stale after 30 min)"
                };

                if (others.Count == 0)
                {
                    report.Add("No other sessions are working on this board.");
                }
                else
                {
                    report.Add($"");
                    report.Add($"{others.Count} other session(s) on this board:");
                    foreach (var c in others)
                    {
                        report.Add("  " + ClaimLine(c));
                    }

                    var colliding = others.Where(c =>
                            !c.Stale && c.Files.Any(f => fileList.Contains(f)))
                        .ToList();
                    if (colliding.Count > 0)
                    {
                        report.Add("");
                        foreach (var c in colliding)
                        {
                            string shared = String.Join(", ", c.Files.Where(f => fileList.Contains(f)));
                            report.Add($"COLLISION WARNING: {shared} is actively claimed by " +
                                $"session {c.SessionId} - coordinate before editing it.");
                        }
                    }
                }

                return new ToolResult(call.Id, String.Join("\n", report));
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? task = GetStringArgument(call, "task");
            string? project = GetStringArgument(call, "project");
            return string.IsNullOrWhiteSpace(task)
                ? "Claiming work in a project"
                : string.IsNullOrWhiteSpace(project)
                    ? $"Claiming: {Truncate(task, 60)}"
                    : $"Claiming in {Truncate(project, 40)}: {Truncate(task, 40)}";
        }

        internal static string ClaimLine(WorkClaim c)
        {
            string since = TimeText(c.Heartbeat);
            string marker = c.Stale
                ? $"[STALE - no heartbeat since {since}, safe to take over]"
                : $"[active since {since}]";
            string files = c.Files.Count > 0 ? $" (files: {String.Join(", ", c.Files)})" : "";
            return $"{marker} session {c.SessionId} · {c.AgentId} - \"{Truncate(c.Task, 80)}\"{files}";
        }

        // Local clock time of a heartbeat, HH:MM. No duration math needed -
        // the status line is informative, staleness is computed by the store.
        internal static string TimeText(DateTimeOffset t)
        {
            string hh = t.Hour < 10 ? $"0{t.Hour}" : $"{t.Hour}";
            string mm = t.Minute < 10 ? $"0{t.Minute}" : $"{t.Minute}";
            return $"{hh}:{mm}";
        }

        internal static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string? OptionalString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var element)
                ? element.GetString()
                : null;
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