using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Memory;

// Persistence/retrieval abstraction for an agent's memories. The store is
// scoped to a single AgentId; the host owns one repository per agent. Backends
// are swappable (JSON file today, a vector store like Chroma later) behind this
// interface.
internal interface IMemoryRepository
{
    Task UpsertAsync(MemoryDocument doc, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    // Returns up to `limit` documents in the given tier, most recently created
    // first. Used for streaming a summary/reflection over recent memories.
    Task<IReadOnlyList<MemoryDocument>> GetRecentAsync(
        MemoryTier tier,
        int limit,
        CancellationToken cancellationToken = default);

    // Retrieves up to `limit` documents across tiers whose embedding is nearest
    // to `queryEmbedding` (cosine similarity). `tiers` filters which pools are
    // searched; empty means search all tiers. Results below `minSimilarity`
    // (0..1) are excluded - the relevance floor for retrieval. (Tiers are a
    // legacy concept: see FileMemoryRepository - everything now lives in one
    // pool and the filter is a no-op.)
    Task<IReadOnlyList<MemoryDocument>> QueryAsync(
        IReadOnlyList<float> queryEmbedding,
        int limit,
        IReadOnlyCollection<MemoryTier>? tiers = null,
        float minSimilarity = 0.0f,
        CancellationToken cancellationToken = default);
}
