using CleoAgent.Core.Memory;
using CleoAgent.Core.Model;
using CleoAgent.Core.Tools;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace CleoAgent.Core.Agent
{
    internal sealed class AgentLoop
    {
        private readonly IModelProvider m__Model;
        private readonly ToolRegistry   m__Tools;
        private readonly string         m__AgentId;
        private readonly MemorySummarizer? m__Summarizer;
        private readonly MemoryReflection? m__Reflection;
        private readonly IMemoryRepository? m__Repository;
        private readonly int             m__SummarizeEvery;
        private readonly int             m__ReflectAfter;

        private readonly StringBuilder   m__TurnLog = new();
        private int                      m__TurnCount;

        private string? m__ContinuationToken;

        public AgentLoop(
            IModelProvider modelProvider,
            ToolRegistry tools,
            string? agentId = null,
            MemorySummarizer? summarizer = null,
            int summarizeEvery = 8,
            MemoryReflection? reflection = null,
            int reflectAfter = 12,
            IMemoryRepository? repository = null)
        {
            m__Model  = modelProvider;
            m__Tools  = tools;
            m__AgentId = string.IsNullOrWhiteSpace(agentId) ? "default" : agentId!;
            m__Summarizer    = summarizer;
            m__Reflection    = reflection;
            m__Repository    = repository;
            m__SummarizeEvery = Math.Max(1, summarizeEvery);
            m__ReflectAfter   = Math.Max(1, reflectAfter);
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(string input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ModelRequest request = ModelRequest.ForPrompt(input, m__Tools.GetDefinitions(), m__ContinuationToken, m__AgentId);

            var turnText = new StringBuilder();
            m__TurnLog.AppendLine($"USER: {input}");

            while (true)
            {
                ToolCall? pendingToolCall   = null;
                bool completed              = false;

                await foreach (ModelEvent modelEvent in m__Model.StreamAsync(request, cancellationToken))
                {
                    switch (modelEvent)
                    {
                        case ModelTextDelta text:
                            turnText.Append(text.Text);
                            yield return new AgentTextDelta(text.Text);
                            break;

                        case ModelToolCall toolCall:
                            pendingToolCall = toolCall!.Call;
                            break;

                        case ModelCompleted modelCompleted:
                            m__ContinuationToken = modelCompleted.ContinuationToken;
                            completed = true;
                            break;

                        case ModelError error:
                            yield return new AgentError(error.Message);
                            yield break;
                    }
                }

                if (!completed)
                {
                    yield return new AgentError("Model stream ended without a completion event.");
                    yield break;
                }

                if (pendingToolCall is null)
                {
                    // The turn produced a final answer (no more tool calls). Log the
                    // assistant text and, on the summarizer cadence, distill the
                    // recent conversation into short-term memories.
                    m__TurnLog.AppendLine($"ASSISTANT: {turnText}");
                    m__TurnCount++;

                    await MaybeSummarizeAsync(cancellationToken);

                    yield return new AgentCompleted();
                    yield break;
                }

                if (!m__Tools.TryGetValue(pendingToolCall.Name, out IAgentTool? tool))
                {
                    yield return new AgentError($"Model requested unknown tool '{pendingToolCall.Name}'.");
                    yield break;
                }

                yield return new AgentToolStarted(tool!.Name, tool.Describe(pendingToolCall));

                var execution = await tool.ExecuteAsync(pendingToolCall, cancellationToken);

                yield return new AgentToolCompleted(pendingToolCall.Name, execution.IsError);

                request = ModelRequest.ForToolResult(execution, m__Tools.GetDefinitions(), m__ContinuationToken, m__AgentId);
            }
        }

        public void Reset()
        {
            m__ContinuationToken = null;
        }

        private async Task MaybeSummarizeAsync(CancellationToken cancellationToken)
        {
            if (m__Summarizer is null || m__TurnCount < m__SummarizeEvery)
            {
                return;
            }

            string conversation = m__TurnLog.ToString().Trim();

            if (conversation.Length == 0)
            {
                return;
            }

            int stored = await m__Summarizer.SummarizeAsync(conversation, maxPairs: 3, cancellationToken);

            if (stored > 0)
            {
                Console.Error.WriteLine($"[memory] stored {stored} short-term memory(ies) from this conversation.");
            }

            // Reset the rolling window after summarizing.
            m__TurnLog.Clear();
            m__TurnCount = 0;

            // Promotion: once enough short-term memories accumulate, consolidate
            // them into durable long-term facts.
            await MaybeReflectAsync(cancellationToken);
        }

        private async Task MaybeReflectAsync(CancellationToken cancellationToken)
        {
            if (m__Reflection is null)
            {
                return;
            }

            var shorts = await m__RepositoryShortCountAsync(cancellationToken);

            if (shorts < m__ReflectAfter)
            {
                return;
            }

            int promoted = await m__Reflection.ReflectAsync(recent: m__ReflectAfter, maxFacts: 5, cancellationToken);

            if (promoted > 0)
            {
                Console.Error.WriteLine($"[memory] promoted {promoted} long-term memory(ies).");
            }
        }

        private async Task<int> m__RepositoryShortCountAsync(CancellationToken cancellationToken)
        {
            if (m__Repository is null)
            {
                return 0;
            }

            var shorts = await m__Repository.GetRecentAsync(MemoryTier.ShortTerm, limit: 1000, cancellationToken);
            return shorts.Count;
        }
    }
}