using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Model;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Memory;

// DEPRECATED (2026-09-12): superseded by agent-driven memory tools. The host no
// longer consolidates memories automatically - the agent decides what to keep
// and forget via memory_write/memory_forget. Kept per the no-delete rule.
//
// The memory consolidation path: reads recent short-term memories, asks the LLM
// to synthesize the durable, high-level facts worth keeping long-term, embeds
// them, and stores them as long-term memories. Short-term items are left in
// place (retrieval weights tiers; promotion does not delete the source, so no
// data is lost unless explicitly cleaned).
internal sealed class MemoryReflection
{
    private readonly IModelProvider _model;
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingProvider _embedding;
    private readonly string _agentId;

    public MemoryReflection(
        IModelProvider model,
        IMemoryRepository repository,
        IEmbeddingProvider embedding,
        string agentId)
    {
        _model = model;
        _repository = repository;
        _embedding = embedding;
        _agentId = agentId;
    }

    // Reflects over up to `recent` short-term memories and stores consolidated
    // long-term facts. Returns how many long-term memories were stored.
    public async Task<int> ReflectAsync(
        int recent = 10,
        int maxFacts = 5,
        CancellationToken cancellationToken = default)
    {
        var shorts = await _repository.GetRecentAsync(MemoryTier.ShortTerm, recent, cancellationToken);

        if (shorts.Count == 0)
        {
            return 0;
        }

        string transcript = string.Join("\n", shorts.Select((m, i) => $"{i + 1}. {m.Text}"));

        string prompt = BuildPrompt(transcript, maxFacts);

        string? reply = await _model.CompleteAsync(prompt, _agentId, cancellationToken);
        if (string.IsNullOrWhiteSpace(reply))
        {
            return 0;
        }

        IReadOnlyList<string> facts = ParseFacts(reply, maxFacts);

        int stored = 0;

        foreach (string fact in facts)
        {
            var embedding = await _embedding.EmbedAsync(fact, cancellationToken);

            await _repository.UpsertAsync(
                MemoryDocument.Create(MemoryTier.LongTerm, fact, embedding),
                cancellationToken);

            stored++;
        }

        return stored;
    }

    private static string BuildPrompt(string shortTermTranscript, int maxFacts)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are the long-term memory consolidator for an AI agent.");
        sb.AppendLine("Below are some recent short-term memories. Synthesize them into the");
        sb.AppendLine($"up to {maxFacts} most durable, high-level facts worth remembering long-term.");
        sb.AppendLine("Remove redundancy; keep agent-relevant, stable facts.");
        sb.AppendLine("Return ONLY a strict JSON array of strings. No markdown, no prose.");
        sb.AppendLine();
        sb.AppendLine("--- short-term memories ---");
        sb.AppendLine(shortTermTranscript);
        sb.AppendLine("--- end ---");

        return sb.ToString();
    }

    private static IReadOnlyList<string> ParseFacts(string reply, int maxFacts)
    {
        string json = MemorySummarizer.ExtractJsonArrayOrObject(reply);

        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var result = new List<string>();

                foreach (JsonElement item in root.EnumerateArray().Take(maxFacts))
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.Add(item.GetString()!);
                    }
                }

                return result;
            }
        }
        catch (JsonException)
        {
            // fall through to empty
        }

        return Array.Empty<string>();
    }
}