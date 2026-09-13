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
    string Id)
{
    public static AgentConfig Default => new("Cleo");
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

// Modular web capability (web_fetch / web_search tools). Each surface is a
// provider + optional fallback, mirroring how model/embedding providers are
// selected — "default" is our free local implementation and "none" disables.
//
//   "modules": { "web": {
//     "fetch":  { "request_timeout_s": 30, "max_chars": 40000, "provider": "default", "provider_fallback": "jina" },
//     "search": { "provider": "duckduckgo" }
//   } }
//
// request_timeout_s bounds each fetch; max_chars caps the markdown handed back
// to the model (context-window guard). provider_fallback is tried only when the
// primary provider's output fails the readability test (or the primary errors).
internal sealed record FetchConfig(
    int RequestTimeoutS,
    int MaxChars,
    string Provider,
    string ProviderFallback,
    string ProviderApiKey,
    string FallbackProviderApiKey)
{
    public static FetchConfig Default => new(30, 40_000, "default", "none", "", "");
}

internal sealed record SearchConfig(
    string Provider,
    string ProviderFallback,
    string ProviderApiKey,
    string FallbackProviderApiKey)
{
    public static SearchConfig Default => new("duckduckgo", "none", "", "");
}

internal sealed record AppConfig(
    AgentConfig Agent,
    ModelConfig Model,
    CompactionConfig Compaction,
    PlanningConfig Planning)
{
    public static AppConfig Default => new(
        AgentConfig.Default,
        ModelConfig.Default,
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

    // ---- module config surface (Phase 2) ----------------------------------
    // Each module reads its own section through ConfigSection — the
    // module-owned "modules.<id>" block (since 2026-09-13 the ONLY surface;
    // legacy top-level fallback removed). The
    // accessors below re-parse per call: config.json is tiny and read at boot,
    // so caching buys nothing and would risk staleness under test overrides.

    private static string? RawJsonText() => RawJsonTextAt(ConfigPath);

    // Reads a config file as comment-stripped JSON text. The per-agent
    // allowlist narrowing (agents/<id>/config.json) reuses this same parser so
    // per-agent files get the identical JSON5 + ${ENV} treatment as the host.
    private static string? RawJsonTextAt(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return StripJsonComments(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    // Resolves a dotted section path ("modules.memory" or "embedding") to the
    // object member. Returns false when any segment is missing or not an object.
    private static bool ResolveSection(JsonElement root, string path, out JsonElement section)
    {
        // Assign up front: this dialect's flow analysis cannot prove the loop
        // assigns `section` on every path, so out-params must be pre-initialized.
        section = root;
        JsonElement current = root;
        int start = 0;

        while (start < path.Length)
        {
            int dot = path.IndexOf('.', start);
            string segment = dot < 0 ? path.Substring(start) : path.Substring(start, dot - start);

            if (!current.TryGetProperty(segment, out JsonElement next) || next.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            current = next;

            if (dot < 0)
            {
                section = current;
                return true;
            }

            start = dot + 1;
        }

        return false;
    }

    // Whether the module-owned "modules.<id>" block exists (as an object) in
    // the current config file. 2026-09-13: this is the ONLY config surface;
    // the legacy top-level sections were removed with the pre-modular config.
    public static bool SectionExists(string sectionPath)
    {
        string? valid = RawJsonText();
        if (valid is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valid);
            return ResolveSection(document.RootElement, sectionPath, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? SectionString(string sectionPath, string key)
    {
        string? valid = RawJsonText();
        if (valid is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valid);

            if (!ResolveSection(document.RootElement, sectionPath, out JsonElement section))
            {
                return null;
            }

            if (!section.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return Interpolate(value.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int? SectionInt(string sectionPath, string key)
    {
        string? valid = RawJsonText();
        if (valid is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valid);

            if (!ResolveSection(document.RootElement, sectionPath, out JsonElement section))
            {
                return null;
            }

            if (!section.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return value.GetInt32();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool? SectionBool(string sectionPath, string key)
    {
        string? valid = RawJsonText();
        if (valid is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valid);

            if (!ResolveSection(document.RootElement, sectionPath, out JsonElement section))
            {
                return null;
            }

            if (!section.TryGetProperty(key, out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // String-array key under a dotted section path (e.g. "modules" → "allow":
    // ["*"]). Null when the section/key is absent or not a string array -
    // callers treat null as "no restriction" (the allowlist default).
    public static IReadOnlyList<string>? SectionStrings(string sectionPath, string key) =>
        SectionStringsAt(ConfigPath, sectionPath, key);

    // Same as SectionStrings, but reading an explicit file path (used for the
    // per-agent allowlist file agents/<id>/config.json). Same parser, same
    // interpolation, same comment stripping as the host config.
    public static IReadOnlyList<string>? SectionStringsAt(string path, string sectionPath, string key)
    {
        string? valid = RawJsonTextAt(path);
        if (valid is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valid);

            if (!ResolveSection(document.RootElement, sectionPath, out JsonElement section))
            {
                return null;
            }

            if (!section.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString() ?? "");
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TrySection(JsonElement root, string name, out JsonElement section) =>
        root.TryGetProperty(name, out section) && section.ValueKind == JsonValueKind.Object;

    // Removes // line and /* */ block comments so config.json may be written with
    // the same self-documenting comments as the example template. String-aware so
    // comment markers inside quoted values (e.g. "https://...") are preserved.
    // Internal (not private) so external module.json files get the same JSON5
    // tolerance as config.json (Phase 4): the agent authors them the same way.
    internal static string StripJsonComments(string json)
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
