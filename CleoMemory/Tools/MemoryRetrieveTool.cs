using System.Text;
using System.Text.Json;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Tools.Impl
{
    // Retrieves memories the agent already stored. Query is optional: an empty
    // query returns the most recent memories (an overview of what the agent
    // knows), while a non-empty query does semantic top-k retrieval with a
    // relevance threshold - below the threshold the tool says "nothing stored
    // about that" instead of returning junk.
    internal sealed class MemoryRetrieveTool : IAgentTool
    {
        // Relevance floor for semantic retrieval (cosine on normalized
        // embeddings). Related text usually scores well above this; unrelated
        // text usually well below.
        private const float MinSimilarity = 0.25f;

        private static readonly int LimitMax = 20;

        private readonly IMemoryRepository _repository;
        private readonly IEmbeddingProvider _embedding;

        public string Name => "memory_retrieve";
        public string Description =>
            "Recalls what the agent has stored in its persistent memory. Pass a query " +
            "to find memories about a topic (semantic search; returns nothing below a " +
            "relevance threshold). With no query, returns the most recent memories as " +
            "an overview. Use this before answering anything that might depend on prior " +
            "sessions - user facts, past decisions, project constraints.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "What to look for. Empty/omitted returns the most recent memories."
            },
            "limit": {
              "type": "integer",
              "description": "Max results (1-20, default 5)."
            }
          },
          "additionalProperties": false
        }
        """;

        public MemoryRetrieveTool(IMemoryRepository repository, IEmbeddingProvider embedding)
        {
            _repository = repository;
            _embedding = embedding;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                string? query = null;
                if (document.RootElement.TryGetProperty("query", out var queryElement))
                {
                    query = queryElement.GetString();
                }

                int limit = ClampLimit(ReadIntArgument(call, "limit", 5));

                if (string.IsNullOrWhiteSpace(query))
                {
                    return await RecentAsync(call.Id, limit, cancellationToken);
                }

                query = query.Trim();

                var embedding = await _embedding.EmbedAsync(query, cancellationToken);

                var results = await _repository.QueryAsync(
                    embedding, limit, tiers: null, minSimilarity: MinSimilarity, cancellationToken: cancellationToken);

                if (results.Count == 0)
                {
                    return new ToolResult(call.Id, $"No memories match \"{OneLine(query)}\". Nothing stored about that.");
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Memories matching \"{OneLine(query)}\":");

                int i = 1;
                foreach (MemoryDocument memory in results)
                {
                    sb.Append(i++).Append(". ").AppendLine(OneLine(memory.Text));
                    sb.Append("   [id: ").Append(memory.Id).AppendLine("]");
                }

                return new ToolResult(call.Id, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        private async Task<ToolResult> RecentAsync(string callId, int limit, CancellationToken cancellationToken)
        {
            var recent = await _repository.GetRecentAsync(MemoryTier.ShortTerm, limit, cancellationToken);

            if (recent.Count == 0)
            {
                return new ToolResult(callId, "No memories stored yet. Use memory_write to remember something.");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Recent memories ({recent.Count}):");

            int i = 1;
            foreach (MemoryDocument memory in recent)
            {
                sb.Append(i++).Append(". ").AppendLine(OneLine(memory.Text));
                sb.Append("   [id: ").Append(memory.Id).AppendLine("]");
            }

            return new ToolResult(callId, sb.ToString().TrimEnd());
        }

        public string Describe(ToolCall call)
        {
            string? query = GetStringArgument(call, "query");
            return string.IsNullOrWhiteSpace(query)
                ? "Retrieving recent memories"
                : $"Retrieving memories about: {OneLine(query)}";
        }

        internal static int ClampLimit(int limit) => System.Math.Clamp(limit, 1, LimitMax);

        internal static string OneLine(string s)
        {
            var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
        }

        internal static string? GetStringArgument(ToolCall call, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return document.RootElement.TryGetProperty(name, out var element)
                    ? element.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        internal static int ReadIntArgument(ToolCall call, string name, int fallback)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                if (document.RootElement.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number)
                {
                    return element.GetInt32();
                }
            }
            catch
            {
                // fall through
            }

            return fallback;
        }
    }
}