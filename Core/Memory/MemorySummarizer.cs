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

// The memory write path: given a window of recent conversation, asks the LLM to
// distill it into the few highest-value question/answer pairs, embeds each, and
// stores them as short-term memories. This is the "every handful of messages,
// reflect and store" mechanism.
internal sealed class MemorySummarizer
{
    private readonly IModelProvider _model;
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingProvider _embedding;
    private readonly string _agentId;

    public MemorySummarizer(
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

    // Summarizes `conversation` (already rendered as a readable transcript) into
    // up to `maxPairs` short-term memories. Returns how many were stored.
    public async Task<int> SummarizeAsync(
        string conversation,
        int maxPairs = 3,
        CancellationToken cancellationToken = default)
    {
        string prompt = BuildPrompt(_agentId, conversation, maxPairs);

        string? reply = await _model.CompleteAsync(prompt, _agentId, cancellationToken);

        if (string.IsNullOrWhiteSpace(reply))
        {
            return 0;
        }

        IReadOnlyList<QaPair> pairs = ParsePairs(reply, maxPairs);

        int stored = 0;

        foreach (QaPair pair in pairs)
        {
            var text = $"Q: {pair.Question}\nA: {pair.Answer}";

            var embedding = await _embedding.EmbedAsync(text, cancellationToken);

            await _repository.UpsertAsync(
                MemoryDocument.Create(MemoryTier.ShortTerm, text, embedding),
                cancellationToken);

            stored++;
        }

        return stored;
    }

    private static string BuildPrompt(string agentName, string conversation, int maxPairs)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"You are the memory system for an AI agent named \"{agentName}\".");
        sb.AppendLine("You already know a lot from your own context: your name, your role,");
        sb.AppendLine("your capabilities, your persona. Never re-store those — it's redundant.");
        sb.AppendLine();
        sb.AppendLine("Read the recent conversation and decide what is genuinely WORTH remembering");
        sb.AppendLine("-- facts that add real value and that you would not already know:");
        sb.AppendLine("  • things about the user and their world: preferences, life events,");
        sb.AppendLine("    decisions, problems, urgent or high-stakes situations");
        sb.AppendLine("  • project facts, constraints, and choices that matter later");
        sb.AppendLine();
        sb.AppendLine("Rank by priority. Something the user NEEDS you to remember later -- a lost");
        sb.AppendLine("wallet, a big decision, a strong preference, an ongoing problem -- outranks");
        sb.AppendLine("pleasantries, and outranks anything you already know about yourself.");
        sb.AppendLine($"Keep at most the top {maxPairs}. Be specific: quote the user's own words where you can.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY strict JSON: an array of objects with \"question\" and \"answer\" fields.");
        sb.AppendLine("No markdown, no prose, no surrounding text.");
        sb.AppendLine();
        sb.AppendLine("--- conversation ---");
        sb.AppendLine(conversation);
        sb.AppendLine("--- end ---");

        return sb.ToString();
    }

    private static IReadOnlyList<QaPair> ParsePairs(string reply, int maxPairs)
    {
        string json = ExtractJson(reply);

        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<QaPair>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            var result = new List<QaPair>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray().Take(maxPairs))
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!item.TryGetProperty("question", out JsonElement q) ||
                        !item.TryGetProperty("answer", out JsonElement a))
                        continue;

                    string? question = q.GetString();
                    string? answer = a.GetString();

                    if (!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(answer))
                    {
                        result.Add(new QaPair(question, answer));
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Sometimes models wrap in { "pairs": [...] }.
                if (root.TryGetProperty("pairs", out JsonElement pairs) && pairs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in pairs.EnumerateArray().Take(maxPairs))
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!item.TryGetProperty("question", out JsonElement q) ||
                            !item.TryGetProperty("answer", out JsonElement a)) continue;

                        string? question = q.GetString();
                        string? answer = a.GetString();

                        if (!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(answer))
                        {
                            result.Add(new QaPair(question, answer));
                        }
                    }
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<QaPair>();
        }
    }

    // Tolerates the model wrapping the JSON in a code fence or extra prose.
    private static string ExtractJson(string reply) => ExtractJsonArrayOrObject(reply);

    // Shared by MemorySummarizer and MemoryReflection: pulls the first array or
    // object out of a model reply, tolerating code fences and stray prose.
    internal static string ExtractJsonArrayOrObject(string reply)
    {
        string trimmed = reply.Trim();

        int fence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            // Strip the first fence and everything before it.
            trimmed = trimmed.Substring(fence + 3);

            int end = trimmed.IndexOf("```", StringComparison.Ordinal);
            if (end >= 0)
            {
                trimmed = trimmed.Substring(0, end);
            }
        }

        int start = trimmed.IndexOf('[');
        if (start >= 0)
        {
            int end = trimmed.LastIndexOf(']');
            if (end > start)
            {
                return trimmed.Substring(start, end - start + 1);
            }
        }

        int objStart = trimmed.IndexOf('{');
        if (objStart >= 0)
        {
            int objEnd = trimmed.LastIndexOf('}');
            if (objEnd > objStart)
            {
                return trimmed.Substring(objStart, objEnd - objStart + 1);
            }
        }

        return trimmed;
    }

    private sealed record QaPair(string Question, string Answer);
}