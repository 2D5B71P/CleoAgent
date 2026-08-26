using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

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

// Disk-backed session store. Each session is an append-only JSONL file under
// agents/<agentId>/session/<sessionId>.jsonl, with an index JSON for listing
// and cleanup. The store is the persistence layer; compaction policy (what to
// keep verbatim vs. summarize) lives in the provider so it can call the LLM.
internal sealed class SessionStore
{
    private readonly string _agentId;

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

    // Appends one message to the session history AND its JSONL file.
    public void Append(SessionRecord session, SessionMessage message)
    {
        session.History.Add(message);
        File.AppendAllText(SessionFilePath(session.Id), Serialize(message) + "\n");
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

    // Recorders cleaned session file (e.g. error-recovery/empty).
    public void Clear(SessionRecord session)
    {
        session.History.Clear();
        if (File.Exists(SessionFilePath(session.Id)))
        {
            File.Delete(SessionFilePath(session.Id));
        }
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
}