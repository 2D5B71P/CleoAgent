using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Work
{
    // One session's claim on a project: who (agent + session) is doing what,
    // which files they are touching, and when they were last heard from.
    // Heartbeat is the claim file's own mtime: any claim rewrite is a
    // heartbeat, so no timestamp parsing is needed to judge freshness.
    internal sealed record WorkClaim(
        string SessionId,
        string AgentId,
        string Task,
        List<string> Files,
        string Started,
        DateTimeOffset Heartbeat,
        string? Note,
        bool Stale);

    // Project-local coordination blackboard for concurrent agent sessions.
    //
    // Layout: <project>/agent_work/<sessionId>.md - one file per SESSION.
    // Two sessions of the same agent are still two concurrent writers, so the
    // file key must be the session id, never the agent id. Per-session files
    // also keep the coordination state itself free of write contention: each
    // session only ever rewrites ITS OWN file, and reads are fresh directory
    // scans. The blackboard is never a shared mutable file, so there is no
    // read-modify-write dance and no index to corrupt.
    //
    // Claims are ADVISORY: they prevent edit collisions through awareness,
    // not locking. A session claims the files it is about to touch; other
    // sessions see the claim before touching the same files. A claim whose
    // mtime is older than StaleAfterSeconds belongs to a session that died
    // or moved on: it is marked stale and can safely be taken over.
    //
    // The folder self-ignores (agent_work/.gitignore = "*") so claims never
    // pollute the project's git history or index.
    internal sealed class WorkStateStore
    {
        // Stale window in seconds: a claim with an mtime older than this is
        // considered abandoned. Public so the selftest can force staleness.
        public static long StaleAfterSeconds = 30 * 60;

        public static readonly string DirectoryName = "agent_work";

        private readonly string _agentId;

        public WorkStateStore(string agentId)
        {
            _agentId = agentId;
        }

        public static string DirectoryPath(string project) =>
            Path.Combine(project.Trim(), DirectoryName);

        private string ClaimPath(string project, string sessionId) =>
            Path.Combine(DirectoryPath(project), Sanitize(sessionId) + ".md");

        // Writes (or refreshes) this session's claim. On refresh the original
        // started timestamp is preserved; the rewrite itself is the new
        // heartbeat (readers use the file's mtime).
        public void WriteClaim(string project, string sessionId, string task,
            List<string> files, string? note)
        {
            string dir = DirectoryPath(project);
            Directory.CreateDirectory(dir);
            EnsureSelfIgnored(dir);

            string file = ClaimPath(project, sessionId);
            WorkClaim? existing = File.Exists(file) ? Parse(file) : null;

            string started = existing is not null && !string.IsNullOrWhiteSpace(existing.Started)
                ? existing.Started
                : DateTimeOffset.UtcNow.ToString();

            var lines = new List<string>
            {
                $"# session {sessionId} - active",
                $"agent: {_agentId}",
                $"task: {task}"
            };
            if (files is not null && files.Count > 0)
            {
                lines.Add("files: " + String.Join(", ", files));
            }
            if (note is not null && !string.IsNullOrWhiteSpace(note))
            {
                lines.Add($"note: {note}");
            }
            lines.Add($"started: {started}");

            File.WriteAllText(file, String.Join("\n", lines) + "\n");
        }

        // Deletes this session's claim. Returns true if a claim existed.
        public bool DeleteClaim(string project, string sessionId)
        {
            string file = ClaimPath(project, sessionId);
            if (!File.Exists(file))
            {
                return false;
            }
            File.Delete(file);
            return true;
        }

        // Fresh scan of every claim on the project's board (never cached),
        // newest heartbeat first, with staleness computed against now.
        public static List<WorkClaim> ReadClaims(string project)
        {
            var claims = new List<WorkClaim>();
            string dir = DirectoryPath(project);

            if (!Directory.Exists(dir))
            {
                return claims;
            }

            var now = DateTimeOffset.UtcNow;

            foreach (string file in Directory.EnumerateFiles(dir, "*.md"))
            {
                WorkClaim? claim = Parse(file);
                if (claim is null)
                {
                    continue;
                }

                var heartbeat = new DateTimeOffset(File.GetLastWriteTime(file));
                bool stale = heartbeat.AddSeconds(StaleAfterSeconds) < now;

                claims.Add(claim with { Heartbeat = heartbeat, Stale = stale });
            }

            return claims
                .OrderByDescending(claim => claim.Heartbeat)
                .ToList();
        }

        // Parses one claim file into a record, or null for unreadable/garbage.
        // Heartbeat is not stored in the file - it is the file's mtime, which
        // readers attach in ReadClaims.
        private static WorkClaim? Parse(string file)
        {
            string agentId = "";
            string task = "";
            var files = new List<string>();
            string? note = null;
            string started = "";

            try
            {
                foreach (string raw in File.ReadLines(file))
                {
                    string line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        continue;
                    }

                    var parts = line.Split(":", 2);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    if (key == "agent") agentId = value;
                    else if (key == "task") task = value;
                    else if (key == "note") note = value;
                    else if (key == "started") started = value;
                    else if (key == "files")
                    {
                        foreach (string f in value.Split(","))
                        {
                            string cleaned = f.Trim();
                            if (!string.IsNullOrWhiteSpace(cleaned))
                            {
                                files.Add(cleaned);
                            }
                        }
                    }
                }

                return new WorkClaim(
                    Path.GetFileNameWithoutExtension(file),
                    agentId, task, files, started,
                    Heartbeat: DateTimeOffset.MinValue,   // attached by ReadClaims
                    note, Stale: false);
            }
            catch
            {
                return null;
            }
        }

        // agent_work/.gitignore containing "*" makes git ignore the whole board
        // without touching the project's own .gitignore.
        private static void EnsureSelfIgnored(string dir)
        {
            string ignore = Path.Combine(dir, ".gitignore");
            if (!File.Exists(ignore))
            {
                File.WriteAllText(ignore, "*\n");
            }
        }

        // Session ids are GUIDs/tokens, but never trust them for paths.
        private static string Sanitize(string sessionId) =>
            sessionId.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
    }
}