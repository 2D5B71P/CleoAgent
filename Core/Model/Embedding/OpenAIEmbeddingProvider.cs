using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Model.Embedding;

// Real embedding provider backed by an OpenAI-compatible embeddings endpoint
// (OpenAI, or OpenRouter's /embeddings, etc.). Resolves text into dense vectors
// used for meaningful similarity retrieval. Configurable endpoint + model so it
// is provider-agnostic like the rest of the host.
internal sealed class OpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    public OpenAIEmbeddingProvider(HttpClient httpClient, string apiKey, string model, string? endpoint = null)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = model;
        _endpoint = endpoint ?? "https://api.openai.com/v1/embeddings";
    }

    public async Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        object payload = new
        {
            model = _model,
            input = text
        };

        message.Content = JsonContent.Create(payload);

        using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Embedding endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Embeddings response missing 'data' array.");
            }

            // Take the first (and only, for our single-input calls) embedding.
            JsonElement item = data[0];

            if (!item.TryGetProperty("embedding", out JsonElement embedding) ||
                embedding.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Embeddings response missing 'embedding' vector.");
            }

            var result = new float[embedding.GetArrayLength()];

            int i = 0;
            foreach (JsonElement value in embedding.EnumerateArray())
            {
                result[i++] = value.GetSingle();
            }

            return result;
        }
    }
}