using System.Text;
using System.Text.Json;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Tools.Impl
{
    // Forgets memories the agent no longer wants. Two modes:
    //   - by id:       the id returned by memory_retrieve (surgical)
    //   - by query:    semantic - forgets everything about a topic whose
    //                  similarity passes the same relevance threshold
    //                   retrieval uses. Good for "forget the wallet thing".
    // Exactly one of id/query must be provided.
    internal sealed class MemoryForgetTool : IAgentTool
    {
        private const float MinSimilarity = 0.25f;

        private readonly IMemoryRepository _repository;
        private readonly IEmbeddingProvider _embedding;

        public string Name => "memory_forget";
        public string Description =>
            "Deletes one or more memories. Pass an id (from memory_retrieve) to forget a " +
            "specific memory, or pass a query to forget everything about that topic " +
            "(semantic: only memories above the relevance threshold are deleted).";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "Id of the exact memory to forget (from memory_retrieve)."
            },
            "query": {
              "type": "string",
              "description": "Topic to forget everything about (semantic match)."
            },
            "limit": {
              "type": "integer",
              "description": "Max memories to forget in query mode (1-20, default 5)."
            }
          },
          "additionalProperties": false
        }
        """;

        public MemoryForgetTool(IMemoryRepository repository, IEmbeddingProvider embedding)
        {
            _repository = repository;
            _embedding = embedding;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                string? id = null;
                if (document.RootElement.TryGetProperty("id", out var idElement))
                {
                    id = idElement.GetString();
                }

                string? query = null;
                if (document.RootElement.TryGetProperty("query", out var queryElement))
                {
                    query = queryElement.GetString();
                }

                bool hasId = !string.IsNullOrWhiteSpace(id);
                bool hasQuery = !string.IsNullOrWhiteSpace(query);

                if (hasId == hasQuery)
                {
                    return new ToolResult(
                        call.Id,
                        "Provide exactly one of \"id\" (a specific memory) or \"query\" (forget a topic).",
                        true);
                }

                if (hasId)
                {
                    await _repository.DeleteAsync(id!, cancellationToken);
                    return new ToolResult(call.Id, $"Forgot memory {id!}.");
                }

                var embedding = await _embedding.EmbedAsync(query!, cancellationToken);

                var matches = await _repository.QueryAsync(
                    embedding, MemoryRetrieveTool.ClampLimit(MemoryRetrieveTool.ReadIntArgument(call, "limit", 5)),
                    tiers: null, minSimilarity: MinSimilarity, cancellationToken: cancellationToken);

                if (matches.Count == 0)
                {
                    return new ToolResult(call.Id, $"Nothing similar to \"{MemoryRetrieveTool.OneLine(query!)}\" was stored - no memory forgotten.");
                }

                foreach (MemoryDocument memory in matches)
                {
                    await _repository.DeleteAsync(memory.Id, cancellationToken);
                }

                var sb = new StringBuilder();
                sb.Append("Forgot ").Append(matches.Count).AppendLine(matches.Count == 1 ? " memory:" : " memories:");
                foreach (MemoryDocument memory in matches)
                {
                    sb.Append("- ").AppendLine(MemoryRetrieveTool.OneLine(memory.Text));
                }

                return new ToolResult(call.Id, sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    string? id = idElement.GetString();
                    return string.IsNullOrWhiteSpace(id) ? "Forgetting a memory" : $"Forgetting memory {id}";
                }

                string? query = MemoryRetrieveTool.GetStringArgument(call, "query");
                if (!string.IsNullOrWhiteSpace(query))
                {
                    return $"Forgetting memories about: {MemoryRetrieveTool.OneLine(query)}";
                }
            }
            catch
            {
                // fall through
            }

            return "Forgetting memories";
        }
    }
}