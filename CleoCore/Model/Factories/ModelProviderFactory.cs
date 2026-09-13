using System;
using System.Net.Http;
using CleoAgent.Core.Config;
using CleoAgent.Core.Context;
using CleoAgent.Core.Model.Providers;

namespace CleoAgent.Core.Model.Factories;

// Builds an IModelProvider from the [model] config section, mirroring how
// EmbeddingProviderFactory selects the embedding provider. Keeps the host
// provider-agnostic: the provider name in config drives which provider is
// constructed, so no provider-specific code lives in the CLI.
internal static class ModelProviderFactory
{
    public static IModelProvider Create(
        ModelConfig config,
        ContextEngine? context,
        HttpClient httpClient,
        string? agentId = null,
        CompactionConfig? compaction = null)
    {
        switch (config.Provider.Trim().ToLowerInvariant())
        {
            case "openrouter":
                return new OpenRouterModelProvider(httpClient, config.ApiKey, config.Model, context, agentId, compaction);

            case "openai":
                return new OpenAIModelProvider(httpClient, config.ApiKey, config.Model, context);

            default:
                throw new InvalidOperationException(
                    $"Unknown model provider '{config.Provider}'. Supported: openai, openrouter.");
        }
    }
}