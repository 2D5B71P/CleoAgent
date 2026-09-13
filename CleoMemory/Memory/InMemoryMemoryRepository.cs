using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Memory;

// Volatile in-process repository. Real cosine-similarity retrieval is
// implemented here so the memory pipeline is fully testable without any
// external store. The file backend holds the same shape for persistence.
// Single pool (no tier splitting) - mirrors FileMemoryRepository since 2026-09-12.
internal sealed class InMemoryMemoryRepository : IMemoryRepository
{
    private readonly List<MemoryDocument> _docs = new();

    private readonly object _gate = new();

    public Task UpsertAsync(MemoryDocument doc, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            int existing = _docs.FindIndex(d => d.Id == doc.Id);

            if (existing >= 0)
            {
                _docs[existing] = doc;
            }
            else
            {
                _docs.Add(doc);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _docs.RemoveAll(d => d.Id == id);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryDocument>> GetRecentAsync(
        MemoryTier tier,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _docs
                .OrderByDescending(d => d.CreatedAt)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryDocument>>(result);
        }
    }

    public Task<IReadOnlyList<MemoryDocument>> QueryAsync(
        IReadOnlyList<float> queryEmbedding,
        int limit,
        IReadOnlyCollection<MemoryTier>? tiers = null,
        float minSimilarity = 0.0f,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Cosine similarity (vectors are pre-normalized by providers that
            // guarantee it; compute dot product).
            var result = _docs
                .Select(d => (Doc: d, Score: Cosine(d.Embedding, queryEmbedding)))
                .Where(x => x.Score >= minSimilarity)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Doc)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryDocument>>(result);
        }
    }

    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        int n = System.Math.Min(a.Count, b.Count);

        double dot = 0;
        for (int i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
        }

        return (float)dot;
    }
}
