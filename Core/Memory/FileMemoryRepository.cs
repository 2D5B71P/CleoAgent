using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Memory;

// Persistent per-agent memory store backed by a single JSON file:
//   <agentsRoot>\<agentId>\memory\memories.json
// Embeddings are persisted so retrieval works correctly after a reload without
// re-embedding.
//
// NOTE (2026-09-12): tiers (short/long-term) are legacy from the old
// summarizer/reflection design. Everything now lives in ONE pool - the agent
// decides what to keep and forget via memory tools. MemoryTier is still on the
// document (vestigial) and the tier args above are ignored; the old
// short_term.json / long_term.json files from the summarizer era are left
// untouched on disk and deliberately NOT loaded (clean slate).
internal sealed class FileMemoryRepository : IMemoryRepository
{
    private static readonly string FileName = "memories.json";

    private readonly string _directory;
    private readonly IEmbeddingProvider _embedding;

    private readonly List<MemoryDocument> _store = new();
    private readonly object _gate = new();

    public FileMemoryRepository(string agentsRoot, string agentId, IEmbeddingProvider embedding)
    {
        _directory = Path.Combine(agentsRoot, agentId, "memory");
        _embedding = embedding;

        Load();
    }

    public async Task UpsertAsync(MemoryDocument doc, CancellationToken cancellationToken = default)
    {
        if (doc.Embedding is not { Count: > 0 })
        {
            // Documents must carry embeddings for retrieval; compute if absent.
            var embedding = await _embedding.EmbedAsync(doc.Text, cancellationToken);
            doc = doc with { Embedding = embedding };
        }

        lock (_gate)
        {
            int existing = _store.FindIndex(d => d.Id == doc.Id);

            if (existing >= 0)
                _store[existing] = doc;
            else
                _store.Add(doc);
        }

        await SaveAsync(cancellationToken);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        bool removed;

        lock (_gate)
        {
            int idx = _store.FindIndex(d => d.Id == id);
            removed = idx >= 0;

            if (removed)
            {
                _store.RemoveAt(idx);
            }
        }

        return removed
            ? SaveAsync(cancellationToken)
            : Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryDocument>> GetRecentAsync(
        MemoryTier tier,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _store
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
            var result = _store
                .Where(d => d.Embedding is { Count: > 0 })
                .Select(d => (Doc: d, Score: Cosine(d.Embedding, queryEmbedding)))
                .Where(x => x.Score >= minSimilarity)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Doc)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryDocument>>(result);
        }
    }

    private Task SaveAsync(CancellationToken cancellationToken)
    {
        List<MemoryDocument> snapshot;

        lock (_gate)
        {
            snapshot = _store.ToList();
        }

        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, FileName);
        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);

        return File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private void Load()
    {
        string path = Path.Combine(_directory, FileName);

        if (!File.Exists(path))
            return;

        try
        {
            string json = File.ReadAllText(path);

            var docs = JsonSerializer.Deserialize<List<MemoryDocument>>(json, SerializerOptions);

            if (docs is not null)
            {
                _store.AddRange(docs.Where(d => d.Embedding is { Count: > 0 }));
            }
        }
        catch (JsonException)
        {
            // Corrupt/absent file: start empty.
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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}