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
// module-owned resolution + nested sub-block reads (no legacy fallback since
// 2026-09-13). Invoked via `--selftest`; a failure here refuses nothing
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
        await TestExternalScanAsync();
        await TestExternalExecutionAsync();
        await TestExternalShadowingAsync();
        await TestExternalValidationAsync();

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
        // Module-owned [modules.<id>] is the ONLY config surface since
        // 2026-09-13 (legacy top-level sections were removed). Verify the
        // block resolves, absent blocks are unconfigured, and nested sub-block
        // reads (web fetch/search shape) work.
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_moduleselftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = System.IO.Path.Combine(root, "config.json");

        File.WriteAllText(configPath,
            "{\n" +
            "  \"modules\": {\n" +
            "    \"memory\": { \"provider\": \"hashish\", \"api_key\": \"legacy-key\" },\n" +
            "    \"web\": {\n" +
            "      \"fetch\": { \"provider\": \"jina\", \"max_chars\": 12345 },\n" +
            "      \"search\": { \"provider\": \"duckduckgo\" }\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        string? previousOverride = CleoAgent.Core.Config.Config.ConfigPathOverride;
        try
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = configPath;

            // modules.memory block resolves with its keys.
            var memory = ConfigSection.ForModule("memory");
            bool memoryOk = memory is not null
                && memory.GetString("provider") == "hashish"
                && memory.GetString("api_key") == "legacy-key"
                && memory.IsConfigured;

            // modules.web fetch/search sub-blocks resolve via the dotted reads.
            var web = ConfigSection.ForModule("web");
            bool webOk = web is not null
                && web.GetStringAt("fetch", "provider") == "jina"
                && web.GetIntAt("fetch", "max_chars", 0) == 12345
                && web.GetStringAt("search", "provider") == "duckduckgo"
                && web.IsConfigured;

            // A module with no [modules.<id>] block is unconfigured, never null
            // (the caller substitutes ConfigSection.Empty); reads fall back.
            var empty = ConfigSection.ForModule("devtools");
            bool emptySafe = empty is not null
                && !empty.IsConfigured
                && empty.GetString("provider", "fallback") == "fallback";

            Console.WriteLine(memoryOk && webOk && emptySafe
                ? "  OK: config section module-owned only + nested sub-block reads + empty."
                : string.Format(
                    "  FAIL: config section. memory={0}, web={1}, empty={2}",
                    memoryOk, webOk, emptySafe));
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

    // ---- Phase 4: external agent-authored modules (design s7, tier 2) -----

    // A valid module dir on disk (module.json + a script the tool invokes
    // out-of-process) is discovered by ScanExternal, registered, and loaded
    // like any builtin: tools appear, module shows active.
    private static async Task TestExternalScanAsync()
    {
        var root = TempExternalRoot("ext_scan");
        WriteModule(root, "echo_mod",
            "{\n" +
            "  \"id\": \"echo_mod\",\n" +
            "  \"kind\": \"external\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"description\": \"Echoes args back\",\n" +
            "  \"author\": \"selftest\",\n" +
            "  \"created\": \"2026-09-13\",\n" +
            "  \"modified\": \"2026-09-13\",\n" +
            "  \"requires\": [\"core\"],\n" +
            "  \"tools\": [\n" +
            "    {\n" +
            "      \"name\": \"echo_args\",\n" +
            "      \"description\": \"Return the args JSON unchanged\",\n" +
            "      \"parameters\": { \"type\": \"object\" },\n" +
            "      \"impl\": { \"command\": [\"cmd\", \"/v:on\", \"/c\", \"set /p L=& echo GOT:!L!\"] }\n" +
            "    }\n" +
            "  ]\n" +
            "}\n");

        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));

        var failures = manager.ScanExternal(root, Path.Combine(root, "host"));
        bool scanOk = failures.Count == 0 && manager.IsRegistered("echo_mod");

        var loadFailures = await manager.LoadAllAsync();
        bool loadOk = loadFailures.Count == 0 && manager.IsActive("echo_mod");
        bool toolOk = tools.Names.Contains("echo_args");

        bool authorOk = false;
        foreach (ModuleStatus status in manager.Status())
        {
            if (status.Id == "echo_mod")
            {
                authorOk = status.Manifest.Author == "selftest" && status.Manifest.Created == "2026-09-13";
            }
        }

        Console.WriteLine(scanOk && loadOk && toolOk && authorOk
            ? "  OK: external module scanned, loaded, tool registered, audit fields carried."
            : string.Format(
                "  FAIL: external scan. scanOk={0}, loadOk={1}, toolOk={2}, authorOk={3} | scan failures: {4}",
                scanOk, loadOk, toolOk, authorOk, ListToString(failures)));
    }

    // The script-backed tool runs OUT-OF-PROCESS: stdin gets the args JSON,
    // stdout (exit 0) is the result, non-zero exit becomes an error.
    private static async Task TestExternalExecutionAsync()
    {
        var root = TempExternalRoot("ext_exec");
        WriteModule(root, "echo_mod",
            "{\n" +
            "  \"id\": \"echo_mod\",\n" +
            "  \"kind\": \"external\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"description\": \"Echoes args back\",\n" +
            "  \"tools\": [\n" +
            "    {\n" +
            "      \"name\": \"echo_args\",\n" +
            "      \"description\": \"Return the args JSON unchanged\",\n" +
            "      \"impl\": { \"command\": [\"cmd\", \"/v:on\", \"/c\", \"set /p L=& echo GOT:!L!\"] }\n" +
            "    },\n" +
            "    {\n" +
            "      \"name\": \"fail_tool\",\n" +
            "      \"description\": \"Exits non-zero\",\n" +
            "      \"impl\": { \"command\": [\"cmd\", \"/c\", \"exit /b 7\"] }\n" +
            "    }\n" +
            "  ]\n" +
            "}\n");

        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));
        var failures = manager.ScanExternal(root, Path.Combine(root, "host"));
        await manager.LoadAllAsync();

        var okResult = await tools.ExecuteAsync(
            new ToolCall("c1", "echo_args", "{\"who\":\"selftest\",\"n\":42}"));
        var failResult = await tools.ExecuteAsync(
            new ToolCall("c2", "fail_tool", "{}"));

        bool ok = failures.Count == 0
            && !okResult.IsError
            && okResult.Output == "GOT:{\"who\":\"selftest\",\"n\":42}"
            && failResult.IsError
            && failResult.Output.Contains("7");

        Console.WriteLine(ok
            ? "  OK: script tool executes out-of-process (stdin args, stdout result, exit code error)."
            : string.Format("  FAIL: external execution. okResult=[{0}] errResult=[{1}]",
                okResult.Output, failResult.Output));
    }

    // Per-agent shadows host-wide on id collision (resolution 4): the same
    // module id in both roots registers once - the per-agent copy wins and the
    // host-wide copy is reported as a collision refusal.
    private static async Task TestExternalShadowingAsync()
    {
        var root = TempExternalRoot("ext_shadow");
        var agentRoot = Path.Combine(root, "agent");
        var hostRoot = Path.Combine(root, "host");

        WriteModule(agentRoot, "dup_mod",
            "{\n" +
            "  \"id\": \"dup_mod\",\n" +
            "  \"version\": \"1.1.0\",\n" +
            "  \"description\": \"per-agent copy\",\n" +
            "  \"tools\": [{\"name\": \"agent_tool\", \"description\": \"a\", \"impl\": {\"command\": [\"cmd\", \"/c\", \"echo agent\"]}}]\n" +
            "}\n");

        WriteModule(hostRoot, "dup_mod",
            "{\n" +
            "  \"id\": \"dup_mod\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"description\": \"host-wide copy (should be shadowed)\",\n" +
            "  \"tools\": [{\"name\": \"host_tool\", \"description\": \"h\", \"impl\": {\"command\": [\"cmd\", \"/c\", \"echo host\"]}}]\n" +
            "}\n");

        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));
        var failures = manager.ScanExternal(agentRoot, hostRoot);

        bool once = tools.Names.Count == 0;
        bool agentWins = manager.IsRegistered("dup_mod")
            && tools.Names.Count == 0
            && manager.Status().Count == 1;

        await manager.LoadAllAsync();

        bool loadedAgent = manager.IsActive("dup_mod")
            && tools.Names.Contains("agent_tool")
            && !tools.Names.Contains("host_tool");
        bool noFailure = failures.Count == 0;   // host-wide copy skipped silently, not an error

        Console.WriteLine(once && agentWins && loadedAgent && noFailure
            ? "  OK: per-agent external module shadows host-wide; no collision error."
            : string.Format("  FAIL: shadowing. failures: {0}, tools: {1}",
                ListToString(failures), ListToString(tools.Names)));
    }

    // Validation failures refuse that module BY NAME - bad JSON, id mismatch,
    // wrong kind, no tools, reserved tool name - and the host continues with
    // whatever is valid.
    private static async Task TestExternalValidationAsync()
    {
        var root = TempExternalRoot("ext_validation");

        WriteModule(root, "bad_json", "{ not json");
        WriteModule(root, "id_mismatch", "{\"id\": \"other\", \"version\": \"1.0.0\", \"tools\": []}");
        WriteModule(root, "compiled_kind", "{\"id\": \"compiled_kind\", \"kind\": \"compiled\", \"version\": \"1.0.0\", \"tools\": []}");
        WriteModule(root, "no_tools", "{\"id\": \"no_tools\", \"version\": \"1.0.0\"}");
        WriteModule(root, "reserved_tool", "{\"id\": \"reserved_tool\", \"version\": \"1.0.0\", \"tools\": [{\"name\": \"module_status\", \"description\": \"x\", \"impl\": {\"command\": [\"cmd\"]}}]}");

        var tools = new ToolRegistry();
        var manager = new ModuleManager(Context(tools));
        var failures = manager.ScanExternal(root, Path.Combine(root, "host"));
        await manager.LoadAllAsync();

        bool refusedByName = failures.Count >= 4;
        bool noneRegistered = !manager.IsRegistered("bad_json")
            && !manager.IsRegistered("id_mismatch")
            && !manager.IsRegistered("compiled_kind")
            && !manager.IsRegistered("no_tools")
            && !manager.IsRegistered("reserved_tool");
        bool hostContinues = manager.ActiveCount() == 0;   // no crash, nothing bogus loaded

        Console.WriteLine(refusedByName && noneRegistered && hostContinues
            ? "  OK: invalid externals refused by name (bad json/id/kind/tools/reserved); host unaffected."
            : string.Format("  FAIL: external validation. failures: {0}", ListToString(failures)));
    }

    // Creates a unique temp dir for one external-module test run.
    private static string TempExternalRoot(string tag)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "cleoagent_ext_" + tag + "_" + Guid.NewGuid().ToString("N"));
        return root;
    }

    private static void WriteModule(string root, string moduleId, string moduleJson)
    {
        string dir = Path.Combine(root, moduleId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "module.json"), moduleJson);
    }

    private static string ListToString(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (string item in items)
        {
            if (!first)
            {
                sb.Append(", ");
            }
            sb.Append(item);
            first = false;
        }
        return sb.ToString();
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
            defaultActive, modelRequestable);
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