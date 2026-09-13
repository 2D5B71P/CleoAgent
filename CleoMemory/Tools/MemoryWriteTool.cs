using System.Text;
using System.Text.Json;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Tools.Impl
{
    // Writes a memory the agent chooses to keep. The agent drives its own
    // memory lifecycle: call this when the user says something worth keeping
    // (preferences, life events, decisions) or when the agent learns a project
    // fact it will want later. Retrieval is semantic (embedding similarity), so
    // write the memory the way it should be found ("The user's favourite hot
    // drink is tea"), not as a verbatim transcript.
    internal sealed class MemoryWriteTool : IAgentTool
    {
        private readonly IMemoryRepository _repository;
        private readonly IEmbeddingProvider _embedding;

        public string Name => "memory_write";
        public string Description =>
            "Stores a memory that persists across sessions. Use this when the user tells " +
            "you something worth keeping - preferences, personal facts, decisions, project " +
            "constraints - or when you learn something you will want later. Write it as a " +
            "self-contained fact, phrased so a future memory_retrieve can find it.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "The memory to store, as a self-contained fact."
            }
          },
          "required": ["text"],
          "additionalProperties": false
        }
        """;

        public MemoryWriteTool(IMemoryRepository repository, IEmbeddingProvider embedding)
        {
            _repository = repository;
            _embedding = embedding;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("text", out var textElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: text", true);
                }

                var text = textElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new ToolResult(call.Id, "text cannot be empty.", true);
                }

                var embedding = await _embedding.EmbedAsync(text, cancellationToken);

                await _repository.UpsertAsync(
                    MemoryDocument.Create(MemoryTier.ShortTerm, text, embedding),
                    cancellationToken);

                return new ToolResult(call.Id, $"Remembered: {OneLine(text)}");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? text = GetStringArgument(call, "text");
            return string.IsNullOrWhiteSpace(text)
                ? "Storing a memory"
                : $"Storing a memory: {OneLine(text)}";
        }

        private static string OneLine(string s)
        {
            var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= 120 ? flat : flat.Substring(0, 120) + "…";
        }

        private static string? GetStringArgument(ToolCall call, string name)
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
    }
}