using System;
using System.Collections.Generic;

namespace CleoAgent.Core.Memory;

// A single stored memory: a piece of text plus the metadata needed for
// retrieval and lifecycle. Embeddings are stored alongside so retrieval does
// not require re-embedding on every read.
internal sealed record MemoryDocument(
    string Id,
    MemoryTier Tier,
    string Text,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<float> Embedding,
    DateTimeOffset CreatedAt)
{
    // Short-hand factory for a document without explicit embeddings/metadata.
    public static MemoryDocument Create(
        MemoryTier tier,
        string text,
        IReadOnlyList<float> embedding,
        IReadOnlyDictionary<string, string>? metadata = null,
        DateTimeOffset? createdAt = null)
    {
        return new MemoryDocument(
            Guid.NewGuid().ToString("N"),
            tier,
            text,
            metadata ?? new Dictionary<string, string>(),
            embedding,
            createdAt ?? DateTimeOffset.UtcNow);
    }
}
