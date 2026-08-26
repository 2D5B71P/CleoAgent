using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleoAgent.Core.Config;

// Strongly-typed application configuration loaded from
// %APPDATA%\CleoAgent\config.json. Sections map to the [agent]/[model]/[embedding]
// blocks the user sketched. Missing sections fall back to defaults so the file
// is fully optional.
internal sealed record AgentConfig(
    string Id,
    int SummarizeEvery)
{
    public static AgentConfig Default => new("Cleo", 8);
}

internal sealed record ModelConfig(
    string Provider,
    string Model,
    string ApiKey)
{
    public static ModelConfig Default => new("openrouter", "openai/gpt-5-nano", "");
}

internal sealed record EmbeddingConfig(
    string Provider,
    string Model,
    string ApiKey)
{
    public static EmbeddingConfig Default => new("none", "", "");
}

// Session compaction policy. Keeps the active conversation bounded so both
// resident RAM and the per-turn API payload stay flat no matter how long the
// session runs. Old turns are rolled into an LLM-generated "so far" summary;
// the most recent (KeepRecentTurns) turns stay verbatim.
internal sealed record CompactionConfig(
    int KeepRecentTurns,     // raw turns kept verbatim at the head of the window
    int SoftTokenCeiling,    // trim once the conversation exceeds ~this many tokens
    int HardByteCeiling)     // hard guardrail: never let the serialized history exceed this
{
    public static CompactionConfig Default => new(20, 16_000, 64_000);
}

// Deliberate planning policy. When enabled, each user turn is handled as
// plan -> act/observe -> (re-plan) instead of a purely reactive tool loop.
// Guards keep the agent from trivially or endlessly changing approach:
//   - maxPlanSteps          hard cap on the number of steps in a plan
//   - maxToolCallsPerStep   hard cap on tool executions within one step
//   - replanOnToolError     re-plan deterministically when a tool errors
//   - maxReplans            hard budget for model-signal replans per turn
internal sealed record PlanningConfig(
    bool Enabled,
    int MaxPlanSteps,
    int MaxToolCallsPerStep,
    bool ReplanOnToolError,
    int MaxReplans)
{
    public static PlanningConfig Default => new(false, 6, 8, true, 2);
}

internal sealed record AppConfig(
    AgentConfig Agent,
    ModelConfig Model,
    EmbeddingConfig Embedding,
    CompactionConfig Compaction,
    PlanningConfig Planning)
{
    public static AppConfig Default => new(
        AgentConfig.Default,
        ModelConfig.Default,
        EmbeddingConfig.Default,
        CompactionConfig.Default,
        PlanningConfig.Default);
}

internal static class Config
{
    private static readonly string s_AppData;

    static Config()
    {
        string? appData = System.Environment.GetEnvironmentVariable("APPDATA");

        s_AppData = appData is { Length: > 0 }
            ? appData
            : Path.Combine(
                System.Environment.GetEnvironmentVariable("USERPROFILE")
                    ?? System.Environment.CurrentDirectory,
                "AppData", "Roaming");
    }

    // Test hook: lets the self-test point the loader at a temp file. Null in
    // normal runtime.
    public static string? ConfigPathOverride { get; set; }

    public static string ConfigPath =>
        ConfigPathOverride ?? Path.Combine(s_AppData, "CleoAgent", "config.json");

    // Deserialize config.json, starting from defaults and overlaying whatever
    // sections are present. Any parse problem returns defaults rather than
    // crashing the host.
    public static AppConfig Load()
    {
        AppConfig result = AppConfig.Default;

        if (!File.Exists(ConfigPath))
        {
            return result;
        }

        try
        {
            string raw = File.ReadAllText(ConfigPath);
            string valid = StripJsonComments(raw);

            using JsonDocument document = JsonDocument.Parse(valid);
            JsonElement root = document.RootElement;

            if (TrySection(root, "agent", out JsonElement agent))
            {
                var agentConfig = result.Agent;

                if (agent.TryGetProperty("id", out JsonElement id))
                {
                    agentConfig = agentConfig with { Id = Interpolate(id.GetString()) ?? agentConfig.Id };
                }

                if (agent.TryGetProperty("summarizeEvery", out JsonElement se) && se.ValueKind == JsonValueKind.Number)
                {
                    agentConfig = agentConfig with { SummarizeEvery = Math.Max(1, se.GetInt32()) };
                }

                result = result with { Agent = agentConfig };
            }

            if (TrySection(root, "model", out JsonElement model))
            {
                result = result with
                {
                    Model = new ModelConfig(
                        ReadString(model, "provider", result.Model.Provider),
                        ReadString(model, "model", result.Model.Model),
                        ReadString(model, "api_key", result.Model.ApiKey))
                };
            }

            if (TrySection(root, "embedding", out JsonElement embedding))
            {
                result = result with
                {
                    Embedding = new EmbeddingConfig(
                        ReadString(embedding, "provider", result.Embedding.Provider),
                        ReadString(embedding, "model", result.Embedding.Model),
                        ReadString(embedding, "api_key", result.Embedding.ApiKey))
                };
            }

            if (TrySection(root, "compaction", out JsonElement compaction))
            {
                var compactionConfig = result.Compaction;

                if (compaction.TryGetProperty("keepRecentTurns", out JsonElement kt) && kt.ValueKind == JsonValueKind.Number)
                    compactionConfig = compactionConfig with { KeepRecentTurns = Math.Max(1, kt.GetInt32()) };

                if (compaction.TryGetProperty("softTokenCeiling", out JsonElement st) && st.ValueKind == JsonValueKind.Number)
                    compactionConfig = compactionConfig with { SoftTokenCeiling = Math.Max(1, st.GetInt32()) };

                if (compaction.TryGetProperty("hardByteCeiling", out JsonElement hb) && hb.ValueKind == JsonValueKind.Number)
                    compactionConfig = compactionConfig with { HardByteCeiling = Math.Max(1, hb.GetInt32()) };

                result = result with { Compaction = compactionConfig };
            }

            if (TrySection(root, "planning", out JsonElement planning))
            {
                var planningConfig = result.Planning;

                if (planning.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True)
                    planningConfig = planningConfig with { Enabled = true };
                if (planning.TryGetProperty("enabled", out en) && en.ValueKind == JsonValueKind.False)
                    planningConfig = planningConfig with { Enabled = false };

                if (planning.TryGetProperty("maxPlanSteps", out JsonElement ps) && ps.ValueKind == JsonValueKind.Number)
                    planningConfig = planningConfig with { MaxPlanSteps = Math.Max(1, ps.GetInt32()) };

                if (planning.TryGetProperty("maxToolCallsPerStep", out JsonElement mt) && mt.ValueKind == JsonValueKind.Number)
                    planningConfig = planningConfig with { MaxToolCallsPerStep = Math.Max(1, mt.GetInt32()) };

                if (planning.TryGetProperty("replanOnToolError", out JsonElement roe) && roe.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    planningConfig = planningConfig with { ReplanOnToolError = roe.GetBoolean() };

                if (planning.TryGetProperty("maxReplans", out JsonElement mr) && mr.ValueKind == JsonValueKind.Number)
                    planningConfig = planningConfig with { MaxReplans = Math.Max(0, mr.GetInt32()) };

                result = result with { Planning = planningConfig };
            }
        }
        catch (JsonException)
        {
            // Corrupt config falls back to defaults.
        }

        return result;
    }

    private static bool TrySection(JsonElement root, string name, out JsonElement section) =>
        root.TryGetProperty(name, out section) && section.ValueKind == JsonValueKind.Object;

    // Removes // line and /* */ block comments so config.json may be written with
    // the same self-documenting comments as the example template. String-aware so
    // comment markers inside quoted values (e.g. "https://...") are preserved.
    private static string StripJsonComments(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                sb.Append(c);

                if (c == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                // Line comment: skip to end of line.
                while (i < json.Length && json[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                // Block comment: skip to */.
                i += 2;
                while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string ReadString(JsonElement section, string name, string fallback)
    {
        if (section.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return Interpolate(value.GetString()) ?? fallback;
        }

        return fallback;
    }

    // Replaces ${NAME} tokens with the value of the process environment variable
    // NAME (e.g. "${OPENROUTER_API_KEY}"). This lets config.json reference secrets
    // that live in the env layer instead of embedding them. Unresolved tokens are
    // left as-is. Trailing/leading whitespace in the result is trimmed.
    private static string? Interpolate(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${"))
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '$' && i + 1 < value.Length && value[i + 1] == '{')
            {
                int close = value.IndexOf('}', i + 2);
                if (close > i + 2)
                {
                    string name = value.Substring(i + 2, close - (i + 2));
                    string? resolved = System.Environment.GetEnvironmentVariable(name);
                    sb.Append(resolved ?? value.Substring(i, close - i + 1));
                    i = close;
                    continue;
                }
            }

            sb.Append(value[i]);
        }

        string result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
