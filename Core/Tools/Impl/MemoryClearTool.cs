using System.Text;
using System.Text.Json;
using CleoAgent.Core.Memory;

namespace CleoAgent.Core.Tools.Impl
{
    // Empties the whole memory store - a hard reset. The agent must pass
    // confirm="yes" so an accidental or half-formed call can never wipe
    // everything. Returning the count lets the agent state what it did.
    internal sealed class MemoryClearTool : IAgentTool
    {
        private readonly IMemoryRepository _repository;

        public string Name => "memory_clear";
        public string Description =>
            "Wipes ALL stored memories - a hard reset. Only use this deliberately " +
            "(e.g. starting a fresh project, or the user asks you to forget everything). " +
            "Requires confirm=\"yes\".";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "confirm": {
              "type": "string",
              "description": "Must be \"yes\" to clear all memories."
            }
          },
          "required": ["confirm"],
          "additionalProperties": false
        }
        """;

        public MemoryClearTool(IMemoryRepository repository)
        {
            _repository = repository;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("confirm", out var confirmElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: confirm (must be \"yes\").", true);
                }

                string? confirm = confirmElement.GetString();

                if (confirm is null || !confirm.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return new ToolResult(call.Id, "Refusing: pass confirm=\"yes\" to clear all memories.", true);
                }

                var all = await _repository.GetRecentAsync(MemoryTier.ShortTerm, limit: 1_000_000, cancellationToken);

                foreach (MemoryDocument memory in all)
                {
                    await _repository.DeleteAsync(memory.Id, cancellationToken);
                }

                return new ToolResult(call.Id, $"Cleared {all.Count} memories. Memory is empty.");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call) => "Clearing ALL memories (hard reset)";
    }
}