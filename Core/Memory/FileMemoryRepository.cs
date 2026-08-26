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

// Persistent per-agent memory store backed by two JSON files:
//   <agentsRoot>\<agentId>\memory\short_term.json
//   <agentsRoot>\<agentId>\memory\long_term.json
// Embeddings are persisted so retrieval works correctly after a reload without
// re-embedding. This is the non-Chroma backend; the interface keeps backend
// swappable later.
internal sealed class FileMemoryRepository : IMemoryRepository
{
    private readonly string _directory;
    private readonly IEmbeddingProvider _embedding;

    private readonly Dictionary<MemoryTier, List<MemoryDocument>> _store = new();
    private readonly object _gate = new();

    public FileMemoryRepository(string agentsRoot, string agentId, IEmbeddingProvider embedding)
    {
        _directory = Path.Combine(agentsRoot, agentId, "memory");
        _embedding = embedding;

        _store[MemoryTier.ShortTerm] = new List<MemoryDocument>();
        _store[MemoryTier.LongTerm]  = new List<MemoryDocument>();

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
            var list = ListFor(doc.Tier);
            int existing = list.FindIndex(d => d.Id == doc.Id);

            if (existing >= 0)
                list[existing] = doc;
            else
                list.Add(doc);
        }

        await SaveAsync(doc.Tier, cancellationToken);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        MemoryTier? tier = null;

        lock (_gate)
        {
            foreach (var key in _store.Keys.ToList())
            {
                int idx = _store[key].FindIndex(d => d.Id == id);
                if (idx >= 0)
                {
                    _store[key].RemoveAt(idx);
                    tier = key;
                    break;
                }
            }
        }

        return tier is null
            ? Task.CompletedTask
            : SaveAsync(tier.Value, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryDocument>> GetRecentAsync(
        MemoryTier tier,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = ListFor(tier)
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
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var source = new List<MemoryDocument>();

            foreach (var pair in _store)
            {
                if (tiers is { Count: > 0 } && !tiers.Contains(pair.Key))
                    continue;

                source.AddRange(pair.Value);
            }

            var result = source
                .Where(d => d.Embedding is { Count: > 0 })
                .Select(d => (Doc: d, Score: Cosine(d.Embedding, queryEmbedding)))
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Doc)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryDocument>>(result);
        }
    }

    private List<MemoryDocument> ListFor(MemoryTier tier) => _store[tier];

    private Task SaveAsync(MemoryTier tier, CancellationToken cancellationToken)
    {
        List<MemoryDocument> snapshot;

        lock (_gate)
        {
            snapshot = ListFor(tier).ToList();
        }

        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, FileNameFor(tier));
        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);

        return File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private void Load()
    {
        foreach (MemoryTier tier in new[] { MemoryTier.ShortTerm, MemoryTier.LongTerm })
        {
            string path = Path.Combine(_directory, FileNameFor(tier));

            if (!File.Exists(path))
                continue;

            try
            {
                string json = File.ReadAllText(path);

                var docs = JsonSerializer.Deserialize<List<MemoryDocument>>(json, SerializerOptions);

                if (docs is not null)
                {
                    _store[tier] = docs.Where(d => d.Embedding is { Count: > 0 }).ToList();
                }
            }
            catch (JsonException)
            {
                // Corrupt/absent file: start the tier empty.
            }
        }
    }

    private static string FileNameFor(MemoryTier tier) =>
        tier == MemoryTier.ShortTerm ? "short_term.json" : "long_term.json";

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
