using System;
using System.IO;

namespace CleoAgent.Core;

// Resolves agent-scoped locations under %APPDATA%\CleoAgent\agents\<agentId>.
// Each agent owns its instructions and its memory store, keeping multi-agent
// state isolated. This is deliberately NOT the global %APPDATA%\CleoAgent root.
internal static class AgentPaths
{
    private static readonly string s_AppData;

    static AgentPaths()
    {
        string? appData = System.Environment.GetEnvironmentVariable("APPDATA");

        s_AppData = appData is { Length: > 0 }
            ? appData
            : Path.Combine(
                System.Environment.GetEnvironmentVariable("USERPROFILE")
                    ?? System.Environment.CurrentDirectory,
                "AppData", "Roaming");
    }

    public static string AgentsRoot =>
        Path.Combine(s_AppData, "CleoAgent", "agents");

    public static string AgentDir(string agentId) =>
        Path.Combine(AgentsRoot, agentId);

    public static string AgentInstructionsFile(string agentId) =>
        Path.Combine(AgentDir(agentId), "agent.md");

    public static string AgentMemoryDir(string agentId) =>
        Path.Combine(AgentDir(agentId), "memory");

    // Session store for active/in-flight conversations. Each session is one
    // JSONL file (one message per line) under agents/<agentId>/session.
    public static string AgentSessionDir(string agentId) =>
        Path.Combine(AgentDir(agentId), "session");

    public static string SessionFile(string agentId, string sessionId) =>
        Path.Combine(AgentSessionDir(agentId), $"{sessionId}.jsonl");

    public static string SessionIndexFile(string agentId) =>
        Path.Combine(AgentSessionDir(agentId), "sessions.json");
}
