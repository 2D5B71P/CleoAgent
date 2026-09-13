using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CleoAgent.Core.Modules;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.CLI;

// Composition self-test (Phase 2 exit gate): builds the REAL host wiring -
// all five built-in modules + kernel tools - against a temp config and
// asserts: no load failures, all 5 active, tool definitions equal today's
// set plus the kernel tools, memory/web services registered, and the
// module_status / list_modules tools render.
internal static class CompositionSelfTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n-- Composition (Phase 2 gate) --");

        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_composition_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = System.IO.Path.Combine(root, "config.json");

        File.WriteAllText(configPath,
            "{\n" +
            "  \"agent\": { \"id\": \"selftest\" },\n" +
            "  \"embedding\": { \"provider\": \"none\", \"model\": \"\", \"api_key\": \"\" },\n" +
            "  \"web\": { \"fetch\": { \"provider\": \"none\" }, \"search\": { \"provider\": \"none\" } }\n" +
            "}\n");

        string? previousOverride = CleoAgent.Core.Config.Config.ConfigPathOverride;
        try
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = configPath;

            var tools = new ToolRegistry();
            var services = new ServiceRegistry();
            var agentId = "selftest";
            var manager = new ModuleManager(new ModuleContext(tools, services, agentId, root));
            var sessionHandle = new SessionIdHandle();

            manager.Register(new DevtoolsModule());
            manager.Register(new SessionModule(agentId, sessionHandle));
            manager.Register(new WorkModule(agentId, sessionHandle));
            manager.Register(new MemoryModule(new HttpClient()));
            manager.Register(new WebModule(new HttpClient()));

            // Kernel tools: always on, registered by the host (as in Program.cs).
            tools.Register(new ListModulesTool(manager));
            tools.Register(new ModuleStatusTool(manager));
            tools.Register(new ModuleEnableTool(manager));
            tools.Register(new ModuleDisableTool(manager));

            var failures = await manager.LoadAllAsync();
            bool noFailures = failures.Count == 0;
            bool allActive = manager.ActiveCount() == 5;

            // Definitions must equal today's set (25 module tools) + kernel (4).
            bool definitionsOk = tools.Names.Count == 29;
            foreach (string expected in ExpectedToolNames())
            {
                if (!tools.Names.Contains(expected))
                {
                    definitionsOk = false;
                }
            }

            bool servicesOk = services.Contains("memory") && services.Contains("embedding");

            // Kernel tools render: module_status shows 5 active; list_modules lists 5.
            var statusResult = await tools.ExecuteAsync(new ToolCall("t1", "module_status", "{}"));
            bool statusOk = !statusResult.IsError && statusResult.Output.Contains("Active 5 of 5");

            var listResult = await tools.ExecuteAsync(new ToolCall("t2", "list_modules", "{}"));
            bool listOk = !listResult.IsError && listResult.Output.Contains("5 module(s)");

            bool allOk = noFailures && allActive && definitionsOk && servicesOk && statusOk && listOk;

            Console.WriteLine(allOk
                ? "  OK: 5/5 modules active; " + tools.Names.Count + " tool definitions (25 module + 4 kernel); services registered; kernel tools render."
                : string.Format(
                    "  FAIL: noFailures={0}, allActive={1}, definitionsOk={2} ({3} tools), servicesOk={4}, statusOk={5}, listOk={6} | load failures: {7} | names: {8}",
                    noFailures, allActive, definitionsOk, tools.Names.Count, servicesOk, statusOk, listOk,
                    ListToString(failures), ListToString(tools.Names)));
        }
        finally
        {
            CleoAgent.Core.Config.Config.ConfigPathOverride = previousOverride;
        }
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

    private static IReadOnlyList<string> ExpectedToolNames()
    {
        var names = new List<string>();

        // devtools (12)
        names.Add("run_command");
        names.Add("read_file");
        names.Add("write_file");
        names.Add("list_directory");
        names.Add("grep");
        names.Add("get_environment_info");
        names.Add("remove_file");
        names.Add("move_file");
        names.Add("rename_file");
        names.Add("make_dir");
        names.Add("remove_dir");
        names.Add("edit_file_inplace");

        // session (4)
        names.Add("list_sessions");
        names.Add("search_session_logs");
        names.Add("read_session_log");
        names.Add("name_session");

        // work (3)
        names.Add("work_claim");
        names.Add("work_status");
        names.Add("work_end");

        // memory (4)
        names.Add("memory_write");
        names.Add("memory_retrieve");
        names.Add("memory_forget");
        names.Add("memory_clear");

        // web (2)
        names.Add("web_fetch");
        names.Add("web_search");

        // kernel (4)
        names.Add("list_modules");
        names.Add("module_status");
        names.Add("module_enable");
        names.Add("module_disable");

        return names;
    }
}