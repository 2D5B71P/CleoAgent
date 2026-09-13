using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

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
        await TestAllowlistBootGateAsync();
        await TestRequestEnableNextTurnAsync();
        await TestRequestDisableNextTurnAsync();
        await TestRequestRefusalsAsync();
        await TestAllowlistNarrowingAsync();
        await TestEnableCascadesDependenciesAsync();

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

    // Phase 3: a module denied by the operator allowlist is not loaded at boot
    // (registered-but-inactive), is not requestable, and stays out no matter
    // what the model asks.
    private static async Task TestAllowlistBootGateAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var deny = NamesOf("denied_mod");
        var manager = new ModuleManager(
            Context(tools),
            new ModuleAllowlist(true, new List<string>(), deny));

        manager.Register(new FakeModule("allowed_mod", NamesOf("allowed_tool"), log, null, true, true));
        manager.Register(new FakeModule("denied_mod", NamesOf("denied_tool"), log, null, true, true));

        var failures = await manager.LoadAllAsync();

        bool hostSurvived = failures.Count == 0
            && manager.IsActive("allowed_mod")
            && tools.Names.Contains("allowed_tool");
        bool deniedStayedOut = !manager.IsActive("denied_mod")
            && !tools.Names.Contains("denied_tool")
            && !manager.IsRequestable("denied_mod");

        Console.WriteLine(hostSurvived && deniedStayedOut
            ? "  OK: allowlist deny keeps a module out at boot; host unaffected; model cannot enable it."
            : "  FAIL: boot gate. " + Describe(failures, log));
    }

    // Phase 3: a dormant (default-off) requestable module activates via
    // module_enable semantics - but only NEXT TURN (queued, not mid-turn).
    private static async Task TestRequestEnableNextTurnAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools), ModuleAllowlist.AllowEverything);

        // Dormant: defaultActive=false so boot skips it; requestable=true.
        manager.Register(new FakeModule("dormant", NamesOf("dormant_tool"), log, null, false, true));
        await manager.LoadAllAsync();

        string? refusal = manager.RequestEnable("dormant");
        bool queued = refusal is null;
        bool stillInactiveNow = !manager.IsActive("dormant");   // next-turn semantics
        bool noToolsMidTurn = !tools.Names.Contains("dormant_tool");

        var applyFailures = await manager.ApplyPendingAsync();
        bool activeNextTurn = applyFailures.Count == 0
            && manager.IsActive("dormant")
            && tools.Names.Contains("dormant_tool");

        Console.WriteLine(queued && stillInactiveNow && noToolsMidTurn && activeNextTurn
            ? "  OK: request-enable queues; module activates next turn with its tools."
            : "  FAIL: enable. " + Describe(applyFailures, log));
    }

    // Phase 3: deactivation unregisters the module's tools + services from the
    // live registries (logical activation = registration), next turn.
    private static async Task TestRequestDisableNextTurnAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var services = new ServiceRegistry();
        var ctx = new ModuleContext(tools, services, "selftest-agent", "C:\\selftest-root");
        var manager = new ModuleManager(ctx, ModuleAllowlist.AllowEverything);

        manager.Register(new FakeModule(
            "svc_mod", NamesOf("svc_tool"), log, null, true, true, NamesOf("svc_service")));
        await manager.LoadAllAsync();

        bool loaded = manager.IsActive("svc_mod")
            && tools.Names.Contains("svc_tool")
            && services.Contains("svc_service");

        string? refusal = manager.RequestDisable("svc_mod");
        bool queued = refusal is null;

        var applyFailures = await manager.ApplyPendingAsync();
        bool goneNextTurn = manager.ActiveCount() == 0
            && !tools.Names.Contains("svc_tool")
            && !services.Contains("svc_service");

        // Re-enable round-trips: a disabled module can be requested again.
        string? reRefusal = manager.RequestEnable("svc_mod");
        var reapplyFailures = await manager.ApplyPendingAsync();
        bool restored = reRefusal is null && reapplyFailures.Count == 0
            && manager.IsActive("svc_mod")
            && tools.Names.Contains("svc_tool")
            && services.Contains("svc_service");

        Console.WriteLine(loaded && queued && goneNextTurn && restored
            ? "  OK: request-disable unregisters tools+services next turn; enable round-trips."
            : "  FAIL: disable. " + Describe(applyFailures, log));
    }

    // Phase 3: clear refusals - unknown id, already active, always-on modules
    // (not model-requestable), and deny that no config/edit can reopen.
    private static async Task TestRequestRefusalsAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var deny = new List<string>();
        deny.Add("denied_mod");
        // devtools-like: always-on, not model-requestable.
        var manager = new ModuleManager(
            Context(tools),
            new ModuleAllowlist(true, new List<string>(), deny));

        manager.Register(new FakeModule("always_on", NamesOf("ao_tool"), log, null, true, false));
        manager.Register(new FakeModule("denied_mod", NamesOf("denied_tool"), log, null, true, true));
        await manager.LoadAllAsync();

        bool unknownRefused = manager.RequestEnable("ghost") is not null;
        bool activeRefused = manager.RequestEnable("always_on") is not null;   // already active
        bool alwaysOnRefused = manager.RequestDisable("always_on") is not null;  // not requestable
        bool deniedRefused = manager.RequestEnable("denied_mod") is not null;    // deny list

        // Kernel-tool layer: module_enable on a denied module reports the
        // denial in the tool result (exit-gate requirement: "the tool reports
        // the denial clearly").
        var enableTool = new ModuleEnableTool(manager);
        var toolResult = await enableTool.ExecuteAsync(
            new ToolCall("ref1", "module_enable", "{\"module_id\":\"denied_mod\"}"),
            cancellationToken: default);
        bool toolReportsDenial = toolResult.IsError
            && toolResult.Output.Contains("operator allowlist")
            && toolResult.Output.Contains("cannot be enabled");

        Console.WriteLine(unknownRefused && activeRefused && alwaysOnRefused && deniedRefused && toolReportsDenial
            ? "  OK: refusals are loud and specific (unknown/active/always-on/denied); module_enable tool reports the denial."
            : "  FAIL: refusals.");
    }

    // Phase 3: per-agent allowlist narrowing (resolution 3): the per-agent file
    // can only further restrict host allow/deny - a per-agent grant can never
    // reopen a host denial, and intersect narrows the allow list.
    private static async Task TestAllowlistNarrowingAsync()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_allowlist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string hostPath = System.IO.Path.Combine(root, "host-config.json");

        File.WriteAllText(hostPath,
            "{\n" +
            "  \"modules\": { \"allow\": [\"*\"], \"deny\": [\"host_denied\"] }\n" +
            "}\n");

        string? previousOverride = CleoAgent.Core.Config.Config.ConfigPathOverride;
        try
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = hostPath;

            // Host config with NO [modules] section at all (= the default
            // production config): must resolve to allow-everything, deny-
            // nothing. Regression: absent allow lists were once treated as
            // EMPTY (denying every module at boot) instead of "*" (no
            // restriction).
            File.WriteAllText(hostPath,
                "{\n" +
                "  \"agent\": { \"id\": \"selftest\" }\n" +
                "}\n");

            var absent = ModuleAllowlist.LoadFromConfig("agentA", root);
            bool absentMeansAllowAll = absent.IsAllowed("devtools")
                && absent.IsAllowed("web")
                && !absent.IsDenied("session")
                && !absent.IsDenied("anything");

            // Restore a host [modules] section for the narrowing checks below.
            File.WriteAllText(hostPath,
                "{\n" +
                "  \"modules\": { \"allow\": [\"*\"], \"deny\": [\"host_denied\"] }\n" +
                "}\n");

            // Agent file narrows allow: only devtools + session allowed.
            string agentDir = System.IO.Path.Combine(root, "agentA");
            Directory.CreateDirectory(agentDir);
            File.WriteAllText(System.IO.Path.Combine(agentDir, "config.json"),
                "{\n" +
                "  \"modules\": { \"allow\": [\"devtools\", \"session\"], \"deny\": [\"session\"] }\n" +
                "}\n");

            var narrowed = ModuleAllowlist.LoadFromConfig("agentA", root);
            bool allowNarrowed = narrowed.IsAllowed("devtools")      // in agent allow
                && !narrowed.IsAllowed("web");                        // host "*" narrowed to agent list
            bool denyUnion = narrowed.IsDenied("session")            // agent deny
                && narrowed.IsDenied("host_denied");                 // host deny survives

            // Agent file tries to WIDEN (allow a host-denied module): no-op.
            File.WriteAllText(System.IO.Path.Combine(agentDir, "config.json"),
                "{\n" +
                "  \"modules\": { \"allow\": [\"host_denied\"] }\n" +
                "}\n");

            var reopened = ModuleAllowlist.LoadFromConfig("agentA", root);
            bool noWiden = !reopened.IsAllowed("host_denied");

            Console.WriteLine(absentMeansAllowAll && allowNarrowed && denyUnion && noWiden
                ? "  OK: absent [modules] = allow all; per-agent allowlist narrows allow + unions deny; cannot widen host deny."
                : string.Format(
                    "  FAIL: narrowing. absentMeansAllowAll={0}, allowNarrowed={1}, denyUnion={2}, noWiden={3}",
                    absentMeansAllowAll, allowNarrowed, denyUnion, noWiden));
        }
        finally
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = previousOverride;
        }
    }

    // Phase 3: enabling a module cascades to its dormant dependencies, loaded
    // first (dependency order preserved on the enable path).
    private static async Task TestEnableCascadesDependenciesAsync()
    {
        var log = new List<string>();
        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools), ModuleAllowlist.AllowEverything);

        var requiresBase = NamesOf("base");
        manager.Register(new FakeModule("base", NamesOf("base_tool"), log, null, false, true));   // dormant dependency
        manager.Register(new FakeModule("top", NamesOf("top_tool"), log, requiresBase, false, true));
        await manager.LoadAllAsync();

        string? refusal = manager.RequestEnable("top");
        var applyFailures = await manager.ApplyPendingAsync();
        bool bothActive = refusal is null && applyFailures.Count == 0
            && manager.IsActive("top")
            && manager.IsActive("base")
            && tools.Names.Contains("top_tool")
            && tools.Names.Contains("base_tool");

        Console.WriteLine(bothActive
            ? "  OK: enable cascades to dormant dependencies (loaded first)."
            : "  FAIL: cascade. " + Describe(applyFailures, log));
    }

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
// `toolNames` (plus one service per name in `serviceNames`, when given),
// appends "load:<id>" to `log` on load. Requires are honored by the manager
// (tests pass them through the ctor). Boot/manifest knobs (defaultActive,
// modelRequestable) let tests drive Phase 3 activation policy.
internal sealed class FakeModule : IModule
{
    private readonly string _id;
    private readonly List<string> _toolNames;
    private readonly List<string>? _serviceNames;
    private readonly List<string> _loadLog;
    private readonly ModuleManifest _manifest;

    public FakeModule(
        string id,
        List<string> toolNames,
        List<string> loadLog,
        IReadOnlyList<string>? requires = null,
        bool defaultActive = true,
        bool modelRequestable = false,
        List<string>? serviceNames = null)
    {
        _id = id;
        _toolNames = toolNames;
        _serviceNames = serviceNames;
        _loadLog = loadLog;
        _manifest = ModuleManifest.Create(
            id, "1.0.0", "fake test module", requires, toolNames,
            null, defaultActive, modelRequestable);
    }

    public ModuleManifest Manifest() => _manifest;

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        _loadLog.Add("load:" + _id);
        foreach (string name in _toolNames)
        {
            ctx.Tools.Register(new FakeTool(name));
        }
        if (_serviceNames is not null)
        {
            foreach (string name in _serviceNames)
            {
                ctx.Services.Register(name, new object());
            }
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