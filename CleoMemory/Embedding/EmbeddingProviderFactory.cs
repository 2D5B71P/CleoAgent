using System;
using System.Net.Http;
using CleoAgent.Core.Config;
using CleoAgent.Core.Model.Embedding;

namespace CleoAgent.Core.Model.Factories;

// Builds an IEmbeddingProvider from the [embedding] config section. Keeps the
// provider selection declarative (driven by config.Embedding.Provider) rather
// than hardcoded in composition. "none" resolves to the offline hash fallback.
internal static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(
        EmbeddingConfig config,
        HttpClient httpClient)
    {
        switch (config.Provider.Trim().ToLowerInvariant())
        {
            case "openai":
            case "openrouter":
                if (string.IsNullOrWhiteSpace(config.Model))
                {
                    throw new InvalidOperationException(
                        "Embedding provider is OpenAI but no model is set in config [embedding].model.");
                }
                if (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey.Contains("${"))
                {
                    throw new InvalidOperationException(
                        "Embedding provider is OpenAI but no api_key is set in config [embedding].api_key.");
                }

                // OpenRouter serves the OpenAI embeddings contract at a different
                // base; point at it when that provider is selected.
                string endpoint = config.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
                    ? "https://openrouter.ai/api/v1/embeddings"
                    : "https://api.openai.com/v1/embeddings";

                return new OpenAIEmbeddingProvider(httpClient, config.ApiKey, config.Model, endpoint);

            case "none":
            case "":
                return new HashEmbeddingProvider();

            default:
                throw new InvalidOperationException(
                    $"Unknown embedding provider '{config.Provider}'. Supported: none, openai, openrouter.");
        }
    }
}