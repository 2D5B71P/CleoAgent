using System;
using System.Net.Http;
using CleoAgent.Core.Config;

namespace CleoAgent.Core.Web.Fetch;

// Builds an IFetchProvider from the web.fetch config, mirroring how
// EmbeddingProviderFactory / ModelProviderFactory select providers. The
// provider name in config drives which concrete fetch provider is built, so no
// provider-specific code lives in the CLI and new providers slot in with a new
// case + class.
//
// "none" / "" resolves to null - the caller (WebFetchTool) treats a null
// provider as "this surface is disabled".
internal static class FetchProviderFactory
{
    public static IFetchProvider? Create(string provider, HttpClient httpClient, string? apiKey = null)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "":
            case "none":
                return null;

            case "default":
                return new DefaultFetchProvider(httpClient);

            case "jina":
                return new JinaFetchProvider(httpClient, apiKey);

            default:
                throw new InvalidOperationException(
                    $"Unknown web fetch provider '{provider}'. Supported: none, default, jina.");
        }
    }
}