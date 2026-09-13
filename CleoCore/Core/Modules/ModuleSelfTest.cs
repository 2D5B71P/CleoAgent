using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Tools;

namespace CleoAgent.Core.Modules;

// Kernel self-test for the module contract (design s9): manifest validation,
// dependency-order loading, unknown-requires refusal, tool-name collision
// with rollback, load/unload round-trip, service registry, config-section
// legacy fallback. Invoked via `--selftest`; a failure here refuses nothing
// at boot (fail-closed wiring arrives with external modules) but must be
// fixed before the Phase 2 gate closes.
internal static class ModuleSelfTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n-- Module system --");

        await TestDependencyOrderAsync();
        await TestUnknownRequiresAsync();
        await TestToolCollisionAsync();
        await TestUnloadRoundTripAsync();
        await TestServiceRegistryAsync();
        await TestConfigSectionAsync();

        Console.WriteLine("  OK: module system self-test complete.");
    }

    private static async Task TestDependencyOrderAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));

        var toolsA = new List<string>();
        toolsA.Add("tool_a");
        var toolsB = new List<string>();
        toolsB.Add("tool_b");
        var requiresB = new List<string>();
        requiresB.Add("a");

        manager.Register(new FakeModule("b", toolsB, log, requiresB));
        manager.Register(new FakeModule("a", toolsA, log));

        var failures = await manager.LoadAllAsync();

        bool ok = failures.Count == 0
            && manager.ActiveCount() == 2
            && log.Count == 2
            && log[0] == "load:a"
            && log[1] == "load:b"
            && tools.Names.Contains("tool_a")
            && tools.Names.Contains("tool_b");

        Console.WriteLine(ok
            ? "  OK: dependency order (b requires a; a loaded first) + activation."
            : "  FAIL: dependency order. " + Describe(failures, log));
    }

    private static async Task TestUnknownRequiresAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));

        var requiresGhost = new List<string>();
        requiresGhost.Add("ghost");
        var toolsA = new List<string>();
        toolsA.Add("tool_a2");

        manager.Register(new FakeModule("a", toolsA, log, requiresGhost));

        var failures = await manager.LoadAllAsync();

        bool refused = failures.Count == 1
            && failures[0].Contains("ghost")
            && !manager.IsActive("a")
            && log.Count == 0;   // LoadAsync never ran

        Console.WriteLine(refused
            ? "  OK: unknown requires refused by name; module not loaded; host continues."
            : "  FAIL: unknown requires. " + Describe(failures, log));
    }

    private static async Task TestToolCollisionAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));

        var dup = new List<string>();
        dup.Add("dup_tool");
        var yTools = new List<string>();
        yTools.Add("dup_tool");
        yTools.Add("y_only");

        manager.Register(new FakeModule("x", dup, log));
        manager.Register(new FakeModule("y", yTools, log));

        var failures = await manager.LoadAllAsync();

        bool collisionNamed = failures.Count == 1 && failures[0].Contains("dup_tool");
        bool xSurvived = manager.IsActive("x") && tools.Names.Contains("dup_tool");
        bool yRejected = !manager.IsActive("y") && !tools.Names.Contains("y_only");  // rolled back

        Console.WriteLine(collisionNamed && xSurvived && yRejected
            ? "  OK: tool collision named; offending module rejected; partial tools rolled back."
            : "  FAIL: collision. " + Describe(failures, log));
    }

    private static async Task TestUnloadRoundTripAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var ctx = new ModuleContext(tools, new ServiceRegistry(), "agent", "root");
        var module = new FakeModule("u", NamesOf("u_tool"), log);

        await module.LoadAsync(ctx);
        bool loaded = tools.Names.Contains("u_tool");

        await module.UnloadAsync(ctx);
        bool unloaded = !tools.Names.Contains("u_tool");

        Console.WriteLine(loaded && unloaded
            ? "  OK: load/unload round-trip (tools registered then unregistered)."
            : string.Format("  FAIL: round-trip. loaded={0}, unloaded={1}", loaded, unloaded));
    }

    private static async Task TestServiceRegistryAsync()
    {
        var services = new ServiceRegistry();
        var marker = new object();

        services.Register("svc", marker);
        bool getOk = services.Get("svc") == marker;
        bool containsOk = services.Contains("svc");
        bool namesOk = services.Names.Contains("svc");
        bool missingOk = !services.Contains("nope");

        Console.WriteLine(getOk && containsOk && namesOk && missingOk
            ? "  OK: service registry register/get/contains/names."
            : "  FAIL: service registry.");
    }

    private static async Task TestConfigSectionAsync()
    {
        // Legacy top-level section fallback + module-owned precedence.
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_moduleselftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = System.IO.Path.Combine(root, "config.json");

        File.WriteAllText(configPath,
            "{\n" +
            "  \"embedding\": { \"provider\": \"hashish\", \"api_key\": \"legacy-key\" },\n" +
            "  \"modules\": { \"embedding\": { \"provider\": \"owned\", \"api_key\": \"owned-key\" } }\n" +
            "}\n");

        string? previousOverride = CleoAgent.Core.Config.Config.ConfigPathOverride;
        try
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = configPath;

            // Module-owned [modules.embedding] block wins over the legacy
            // top-level [embedding] section.
            var owned = ConfigSection.ForModule("embedding", "embedding");
            bool ownedWins = owned is not null
                && owned.GetString("provider") == "owned"
                && owned.GetString("api_key") == "owned-key"
                && owned.IsConfigured;

            // A module with no [modules.<id>] block falls back to its legacy
            // top-level section.
            var legacy = ConfigSection.ForModule("memory", "embedding");
            bool legacyFallsBack = legacy is not null
                && legacy.GetString("provider") == "hashish"
                && legacy.GetString("api_key") == "legacy-key"
                && legacy.IsConfigured;

            // Modules without a config surface get null from ForModule (the
            // caller substitutes ConfigSection.Empty); assert the contract.
            var empty = ConfigSection.ForModule("devtools", null);
            bool emptySafe = empty is null;

            Console.WriteLine(ownedWins && legacyFallsBack && emptySafe
                ? "  OK: config section module-owned precedence + legacy fallback + empty."
                : string.Format(
                    "  FAIL: config section. owned={0}, legacy={1}, empty={2}",
                    ownedWins, legacyFallsBack, emptySafe));
        }
        finally
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = previousOverride;
        }
    }

    private static ModuleContext Context(ToolRegistry tools) =>
        new ModuleContext(tools, new ServiceRegistry(), "selftest-agent", "C:\\selftest-root");

    private static List<string> NamesOf(string name)
    {
        var list = new List<string>();
        list.Add(name);
        return list;
    }

    private static string Describe(IReadOnlyList<string> failures, List<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("failures=");
        AppendList(sb, failures);
        sb.Append(", log=");
        AppendList(sb, log);
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, IReadOnlyList<string> items)
    {
        bool first = true;
        foreach (string item in items)
        {
            if (!first)
            {
                sb.Append("; ");
            }
            sb.Append(item);
            first = false;
        }
    }
}

// Minimal module used to exercise the manager: registers one tool per name in
// `toolNames`, appends "load:<id>" to `log` on load. Requires are honored by
// the manager (tests pass them through the ctor).
internal sealed class FakeModule : IModule
{
    private readonly string _id;
    private readonly List<string> _toolNames;
    private readonly List<string> _loadLog;
    private readonly ModuleManifest _manifest;

    public FakeModule(string id, List<string> toolNames, List<string> loadLog, IReadOnlyList<string>? requires = null)
    {
        _id = id;
        _toolNames = toolNames;
        _loadLog = loadLog;
        _manifest = ModuleManifest.Create(id, "1.0.0", "fake test module", requires, toolNames);
    }

    public ModuleManifest Manifest() => _manifest;

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        _loadLog.Add("load:" + _id);
        foreach (string name in _toolNames)
        {
            ctx.Tools.Register(new FakeTool(name));
        }
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        foreach (string name in _toolNames)
        {
            ctx.Tools.Unregister(name);
        }
    }
}

internal sealed class FakeTool : IAgentTool
{
    private readonly string _name;

    public FakeTool(string name)
    {
        _name = name;
    }

    public string Name => _name;
    public string Description => "fake test tool " + _name;
    public string ParametersJson =>
        "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
        new ToolResult(call.Id, "ok");

    public string Describe(ToolCall call) => "Fake tool " + _name;
}