using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleoAgent.Core.Session;

// A live conversation owned by one agent. History is the in-memory working
// copy (bounded by compaction); the id gives stable cross-restart identity.
public sealed class SessionRecord
{
    public required string Id { get; init; }
    public List<SessionMessage> History { get; } = new();

    // Rendered context preamble (system/workspace/memory) for the session,
    // carried so tool-result continuations keep it without re-rendering.
    public string? SystemMessage { get; set; }
}

// One row of the per-agent sessions.json index: display metadata that is NOT
// stored inside the session's JSONL (which is a pure message log). The name is
// user-assigned via the name_session tool; times/counts are maintained by the
// store as the conversation grows. DateTimeOffset values are UTC.
public sealed record SessionEntry(
    string Id,
    string? Name,             // user-assigned label; null = auto-label by first message
    DateTimeOffset? Created,
    DateTimeOffset? LastActivity,
    int MessageCount);

// Disk-backed session store. Each session is an append-only JSONL file under
// agents/<agentId>/session/<sessionId>.jsonl, with an index JSON for human
// labels (names) and cheap listing metadata. The store is the persistence
// layer; compaction policy (what to keep verbatim vs. summarize) lives in the
// provider so it can call the LLM.
internal sealed class SessionStore
{
    private readonly string _agentId;

    // Working copy of the display index (sessions.json). Kept separate from the
    // JSONL files so pre-index/foreign files reconcile naturally: listing
    // backfills rows derived from disk metadata. Multiple SessionStore
    // instances coexist (provider + one per tool call), so the index is always
    // freshly loaded from disk before any mutation (read-modify-write) - the
    // last writer converges on the latest state instead of clobbering it with
    // a stale in-memory snapshot.
    private List<SessionEntry> _index = new List<SessionEntry>();

    public SessionStore(string agentId)
    {
        _agentId = agentId;
        Directory.CreateDirectory(AgentPaths.AgentSessionDir(_agentId));
    }

    public string SessionFilePath(string sessionId) =>
        AgentPaths.SessionFile(_agentId, sessionId);

    public string IndexFilePath =>
        AgentPaths.SessionIndexFile(_agentId);

    public static string Serialize(SessionMessage message) =>
        JsonSerializer.Serialize(message);

    private static SessionMessage Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionMessage>(line)
                ?? new SessionMessage(SessionMessageType.User, Content: line);
        }
        catch (JsonException)
        {
            return new SessionMessage(SessionMessageType.User, Content: line);
        }
    }

    // Loads a session's full history from disk. Returns empty history if the
    // file doesn't exist. A session id with a missing file yields a fresh
    // session that can be persisted on first append.
    public SessionRecord Load(string sessionId)
    {
        var record = new SessionRecord { Id = sessionId };

        string file = SessionFilePath(sessionId);

        if (!File.Exists(file))
        {
            return record;
        }

        foreach (string line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            record.History.Add(Deserialize(line.Trim()));
        }

        return record;
    }

    // Appends one message to the session history AND its JSONL file, then
    // touches the display index (created/last-activity/message count).
    public void Append(SessionRecord session, SessionMessage message)
    {
        session.History.Add(message);
        File.AppendAllText(SessionFilePath(session.Id), Serialize(message) + "\n");
        TouchIndex(session.Id, session.History.Count);
    }

    // Rewrites the session file from the in-memory history. Used after
    // compaction, trimming, or explicit edits to keep disk in sync.
    public void Rewrite(SessionRecord session)
    {
        Rewrite(session, session.History);
    }

    public void Rewrite(SessionRecord session, IReadOnlyList<SessionMessage> messages)
    {
        session.History.Clear();
        session.History.AddRange(messages);

        var sb = new StringBuilder();
        foreach (SessionMessage message in messages)
        {
            sb.Append(Serialize(message));
            sb.Append('\n');
        }
        File.WriteAllText(SessionFilePath(session.Id), sb.ToString());
        TouchIndex(session.Id, messages.Count);
    }

    // Approximate serialized size of the session's history (bytes).
    public long HistoryBytes(SessionRecord session)
    {
        long total = 0;
        foreach (SessionMessage message in session.History)
        {
            total += message.SerializedLength;
        }
        return total;
    }

    // Recovering cleaned session file (e.g. error-recovery/empty).
    public void Clear(SessionRecord session)
    {
        session.History.Clear();
        if (File.Exists(SessionFilePath(session.Id)))
        {
            File.Delete(SessionFilePath(session.Id));
        }
        DropIndexEntry(session.Id);
    }

    public IEnumerable<string> ListSessions()
    {
        string dir = AgentPaths.AgentSessionDir(_agentId);

        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            yield return Path.GetFileNameWithoutExtension(file);
        }
    }

    // ------------------------------------------------------------------
    // Display index (sessions.json): names + listing metadata.
    // ------------------------------------------------------------------

    // All sessions as display entries, newest activity first. The index is
    // reconciled with disk: JSONL files without an index row get one (derived
    // from file metadata), and rows whose file vanished are dropped.
    public IEnumerable<SessionEntry> ListSessionEntries()
    {
        Reconcile();
        return _index
            .OrderByDescending(entry => entry.LastActivity ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // Display entry for one session, or null if the session file doesn't
    // exist. Reconciles first so the row (and name) is fresh.
    public SessionEntry? GetEntry(string sessionId)
    {
        Reconcile();
        int i = IndexOf(sessionId);
        return i >= 0 ? _index[i] : null;
    }

    // Assigns (or clears, via null/empty) a session's display name. Returns
    // false if the session file doesn't exist on disk.
    public bool SetName(string sessionId, string? name)
    {
        Reconcile();
        int i = IndexOf(sessionId);
        if (i < 0)
        {
            return false;
        }

        string? cleaned = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (cleaned == _index[i].Name)
        {
            return true;
        }

        _index[i] = _index[i] with { Name = cleaned };
        SaveIndex();
        return true;
    }

    private void TouchIndex(string sessionId, int messageCount)
    {
        EnsureIndex();

        int i = IndexOf(sessionId);
        var now = DateTimeOffset.UtcNow;

        if (i < 0)
        {
            _index.Add(new SessionEntry(sessionId, null, now, now, messageCount));
        }
        else
        {
            _index[i] = _index[i] with { LastActivity = now, MessageCount = messageCount };
        }

        SaveIndex();
    }

    private void DropIndexEntry(string sessionId)
    {
        EnsureIndex();

        int i = IndexOf(sessionId);
        if (i < 0)
        {
            return;
        }

        _index.RemoveAt(i);
        SaveIndex();
    }

    // Brings the in-memory index in line with the session directory: drop rows
    // for deleted files, backfill rows for orphaned files.
    private void Reconcile()
    {
        EnsureIndex();

        string dir = AgentPaths.AgentSessionDir(_agentId);

        // Ids of JSONL files currently on disk.
        var onDisk = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                onDisk.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        bool changed = false;

        // Drop rows whose session file vanished.
        for (int i = _index.Count - 1; i >= 0; i--)
        {
            if (!onDisk.Contains(_index[i].Id))
            {
                _index.RemoveAt(i);
                changed = true;
            }
        }

        // Backfill rows for files the index has never seen.
        foreach (string id in onDisk)
        {
            if (IndexOf(id) >= 0)
            {
                continue;
            }

            string file = SessionFilePath(id);

            _index.Add(new SessionEntry(
                id,
                null,
                new DateTimeOffset(File.GetCreationTime(file)),
                new DateTimeOffset(File.GetLastWriteTime(file)),
                CountLines(file)));

            changed = true;
        }

        if (changed)
        {
            SaveIndex();
        }
    }

    private int IndexOf(string sessionId)
    {
        return _index.FindIndex(entry => entry.Id == sessionId);
    }

    // Loads the current on-disk index into the working copy, always (no
    // caching): every mutation is a read-modify-write so the file converges
    // even when several store instances are in play. Absent/corrupt file
    // yields an empty list; reconcile rebuilds from disk.
    private void EnsureIndex()
    {
        _index = new List<SessionEntry>();

        string path = IndexFilePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            var rows = JsonSerializer.Deserialize<List<SessionEntry>>(json, IndexSerializerOptions);

            if (rows is not null)
            {
                _index.AddRange(rows.Where(row =>
                    row.Created is not null && row.LastActivity is not null));
            }
        }
        catch (JsonException)
        {
            // Corrupt/absent index: start empty; reconcile rebuilds from disk.
            _index = new List<SessionEntry>();
        }
    }

    // Serializes the working copy as-is (no reload - the mutation that just
    // happened must survive).
    private void SaveIndex()
    {
        Directory.CreateDirectory(AgentPaths.AgentSessionDir(_agentId));
        File.WriteAllText(IndexFilePath, JsonSerializer.Serialize(_index, IndexSerializerOptions));
    }

    // Counts non-empty lines of a session file (its message count when the
    // index has no row yet). Only used during reconciliation.
    private static int CountLines(string file)
    {
        int count = 0;
        foreach (string line in File.ReadLines(file))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }
        return count;
    }

    private static readonly JsonSerializerOptions IndexSerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}