using CleoAgent.Core;
using CleoAgent.Core.Agent;
using CleoAgent.Core.Config;
using CleoAgent.Core.Context;
using CleoAgent.Core.Env;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model;
using CleoAgent.Core.Model.Embedding;
using CleoAgent.Core.Model.Factories;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;
using CleoAgent.Core.Web;
using CleoAgent.Core.Web.Fetch;
using CleoAgent.Core.Web.Search;
using System.Text;

namespace CleoAgent.CLI
{
    internal class Program
    {
        private static readonly HttpClient s__Client = new ();

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // One-shot cancellation source per turn. Tokens cannot be reset, so a
            // fresh source is created for each prompt; the Ctrl+C handler always
            // targets the LIVE source through this holder (registered once).
            var cancelControl = new TurnCancelControl();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                // First Ctrl+C during a turn: cooperative interrupt - swallow the
                // signal and cancel the turn's token. The turn ends, the REPL
                // re-prompts, and text already streamed stays visible.
                if (cancelControl.InFlight
                    && cancelControl.Source is { IsCancellationRequested: false })
                {
                    eventArgs.Cancel = true;
                    cancelControl.Source.Cancel();
                }
                // Idle at the prompt, or the turn is already canceled (second
                // Ctrl+C): leave Cancel untouched so the OS default applies (exit).
            };
            
            EnvLoader.Load();

            if (args.Length > 0 && args[0] == "--selftest")
            {
                await MemorySelfTest.RunAsync();
                return;
            }

            AppConfig config = Config.Load();

            string agentId = config.Agent.Id;

            // Single source of truth: the API key comes from config.Model.ApiKey
            // (which may itself be resolved from an env var via ${NAME} in the
            // config file). No provider-specific env shortcut here — the host is
            // provider-agnostic.
            string apiKey = config.Model.ApiKey;

            // A value that still contains an unresolved ${...} token means the
            // referenced env var wasn't set — treat it as missing, not a literal key.
            if (apiKey.Length == 0 || apiKey.Contains("${"))
            {
                Console.Error.WriteLine("No model api_key set in config.json. Add it (e.g. \"${OPENROUTER_API_KEY}\") and restart.");
                return;
            }

            // Per-agent memory + embedding + context engine. The embedding provider
            // is selected from the [embedding] config section (none→hash, openai/
            // openrouter→real API).
            IEmbeddingProvider embedding = EmbeddingProviderFactory.Create(config.Embedding, s__Client);
            IMemoryRepository memory = new FileMemoryRepository(AgentPaths.AgentsRoot, agentId, embedding);

            var context = new ContextEngine(new IContextSource[]
            {
                new SystemInstructionsSource(),
                new WorkspaceInstructionsSource()
            });

            IModelProvider modelProvider = ModelProviderFactory.Create(
                config.Model, context, s__Client, agentId, config.Compaction);

            // Agent-driven memory: the agent writes, forgets and retrieves at will
            // via the memory_* tools (registered below). Nothing is auto-injected.

            // Modular web capability: providers selected from the [web] config
            // section via the same factory pattern as model/embedding providers.
            // "none" (default fallback) resolves to null -> that surface just
            // reports it is unconfigured (no external cost).
            IFetchProvider? fetchPrimary    = FetchProviderFactory.Create(config.Web.Fetch.Provider, s__Client, config.Web.Fetch.ProviderApiKey);
            IFetchProvider? fetchFallback   = FetchProviderFactory.Create(config.Web.Fetch.ProviderFallback, s__Client, config.Web.Fetch.FallbackProviderApiKey);
            ISearchProvider? searchPrimary  = SearchProviderFactory.Create(config.Web.Search.Provider, s__Client, config.Web.Search.ProviderApiKey);
            ISearchProvider? searchFallback = SearchProviderFactory.Create(config.Web.Search.ProviderFallback, s__Client, config.Web.Search.FallbackProviderApiKey);

            // Filled in by AgentLoop once the first model completion reveals the
            // run's session id; lets session tools resolve the "current" pseudo-id.
            var sessionHandle = new SessionIdHandle();

            ToolRegistry tools = new (
                new RunCommandTool(),
                new ReadFileTool(), 
                new WriteFileTool(),
                new ListDirectoryTool(),
                new GrepTool(),
                new GetEnvironmentInfoTool(),
                new RemoveFileTool(),
                new MoveFileTool(),
                new RenameFileTool(),
                new MakeDirectoryTool(),
                new RemoveDirectoryTool(),
                new EditFileInplaceTool(),
                new WebFetchTool(config.Web.Fetch, fetchPrimary, fetchFallback),
                new WebSearchTool(config.Web.Search, searchPrimary, searchFallback),
                new MemoryWriteTool(memory, embedding),
                new MemoryRetrieveTool(memory, embedding),
                new MemoryForgetTool(memory, embedding),
                new MemoryClearTool(memory),
                new ListSessionsTool(config.Agent.Id, sessionHandle),
                new SearchSessionLogsTool(config.Agent.Id, sessionHandle),
                new ReadSessionLogTool(config.Agent.Id, sessionHandle),
                new NameSessionTool(config.Agent.Id, sessionHandle),
                new WorkClaimTool(config.Agent.Id, sessionHandle),
                new WorkStatusTool(),
                new WorkEndTool(config.Agent.Id, sessionHandle)
            );

            var agent = new AgentLoop(
                modelProvider,
                tools,
                agentId,
                sessionId: sessionHandle,
                planning: config.Planning);

            // -----------------------------
            for (; ;)
            {
                cancelControl.Source = new CancellationTokenSource();

                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write("> ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                // Console.ReadLine() returns null on EOF (e.g. piped input). Treat
                // that as a clean shutdown instead of spinning on null forever.
                var input = Console.ReadLine();

                if (input is null)
                    break;

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                input = input.Trim();

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine();

                cancelControl.InFlight = true;
                try
                {
                    await foreach (AgentEvent evt in agent.RunAsync(input, cancelControl.Source.Token))
                    {
                        switch (evt)
                        {
                            case AgentTextDelta text:
                                Console.Write(text.Text);
                                break;

                            case AgentToolStarted tool:
                                Console.WriteLine();
								Console.ForegroundColor = ConsoleColor.Gray;
                                Console.WriteLine($"[tool {tool.ToolName}]");
                                Console.WriteLine($"● {tool.ToolDescription}");
								Console.ForegroundColor = ConsoleColor.White;
                                break;

                            case AgentPlanEvent plan:
                                Console.WriteLine();
								Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.WriteLine($"[plan] {plan.Goal}");
                                foreach (var step in plan.Steps)
                                {
                                    Console.WriteLine($"  {step.Id}. {step.Summary}");
                                }
								Console.ForegroundColor = ConsoleColor.White;
                                break;

                            case AgentReplanned replan:
                                Console.WriteLine();
								Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"[replan] {replan.Reason}");
								Console.ForegroundColor = ConsoleColor.White;
                                break;

                            case AgentToolCompleted tool:
                                if (tool.IsError)
                                    Console.WriteLine("[+ tool error]");
                                break;

                            case AgentError error:
                                Console.Error.WriteLine(error.Message);
                                break;
                        }
                    }
                }
                catch (System.OperationCanceledException)
                {
                    // Ctrl+C surfaced through the cancellation token (socket reads,
                    // tool execution, context rendering, ThrowIfCancellationRequested);
                    // the turn is over either way - fall through and report.
                }
                finally
                {
                    cancelControl.InFlight = false;
                }

                if (cancelControl.Source.IsCancellationRequested)
                {
                    Console.WriteLine();
                    Console.WriteLine("[interrupted]");
                }

                Console.WriteLine();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("────────────────────────────────────────────────────────────────────────");
				Console.ForegroundColor = ConsoleColor.White;
            }

            // -----------------------------
        }
    }

    // Mutable holder for the current turn's cancellation source so the Ctrl+C
    // handler (registered once) can always cancel the LIVE token. A fresh
    // source is created per prompt because cancellation tokens are one-shot.
    internal sealed class TurnCancelControl
    {
        public CancellationTokenSource? Source;
        public bool InFlight;
    }
}
