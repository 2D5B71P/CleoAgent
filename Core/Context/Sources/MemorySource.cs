using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Context;

// Injects memories relevant to the current context, retrieved from the agent's
// memory repository by embedding the current user prompt and doing a
// top-k similarity search. This is the source that makes memory "remember"
// what matters for the task at hand, rather than injecting everything.
internal sealed class MemorySource : IContextSource
{
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingProvider _embedding;

    public string Name => "Memory";

    public int Priority => 2;

    public MemorySource(IMemoryRepository repository, IEmbeddingProvider embedding)
    {
        _repository = repository;
        _embedding = embedding;
    }

    public async Task<ContextBlock?> BuildAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        string? prompt = request.UserPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var query = await _embedding.EmbedAsync(prompt, cancellationToken);

        var results = await _repository.QueryAsync(query, limit: 5, cancellationToken: cancellationToken);

        if (results.Count == 0)
        {
            return null;
        }

        // Keep only reasonably relevant memories and build the block.
        var sb = new StringBuilder();

        // order: short-term first (fresh), then long-term
        var ordered = results
            .OrderByDescending(m => m.Tier == MemoryTier.ShortTerm)
            .ThenByDescending(m => m.CreatedAt);

        foreach (MemoryDocument memory in ordered)
        {
            sb.Append("- ");
            sb.AppendLine(memory.Text.Replace("\n", " "));
        }

        return new ContextBlock(Name, sb.ToString().TrimEnd());
    }
}
