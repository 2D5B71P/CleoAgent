using CleoAgent.Core;
using CleoAgent.Core.Agent;
using CleoAgent.Core.Config;
using CleoAgent.Core.Context;
using CleoAgent.Core.Env;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model;
using CleoAgent.Core.Model.Embedding;
using CleoAgent.Core.Model.Factories;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;
using System.Text;

namespace CleoAgent.CLI
{
    internal class Program
    {
        private static readonly HttpClient s__Client = new ();

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                if (cts is { IsCancellationRequested: false })
                {
                    eventArgs.Cancel = true;
                    cts.Cancel();
                }
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
                new WorkspaceInstructionsSource(),
                new MemorySource(memory, embedding)
            });

            IModelProvider modelProvider = ModelProviderFactory.Create(
                config.Model, context, s__Client, agentId, config.Compaction);

            // Memory write path: distill recent conversation into short-term
            // memories, then periodically consolidate into long-term facts.
            var summarizer = new MemorySummarizer(modelProvider, memory, embedding, agentId);
            var reflection = new MemoryReflection(modelProvider, memory, embedding, agentId);

            ToolRegistry tools = new (
                new RunCommandTool(),
                new ReadFileTool(), 
                new WriteFileTool(),
                new ListDirectoryTool(),
                new GrepTool(),
                new GetEnvironmentInfoTool()
            );

            var agent = new AgentLoop(
                modelProvider,
                tools,
                agentId,
                summarizer,
                summarizeEvery: config.Agent.SummarizeEvery,
                reflection: reflection,
                reflectAfter: 12,
                repository: memory);

            // -----------------------------
            for (; ;)
            {
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

                await foreach (AgentEvent evt in agent.RunAsync(input, cts.Token))
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

                        case AgentToolCompleted tool:
                            if (tool.IsError)
                                Console.WriteLine("[+ tool error]");
                            break;

                        case AgentError error:
                            Console.Error.WriteLine(error.Message);
                            break;
                    }
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
}
