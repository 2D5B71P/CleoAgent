using System;
using System.Net.Http;

namespace CleoAgent.Core.Web.Search;

// Builds an ISearchProvider from the web.search config, mirroring FetchProviderFactory.
// "none" / "" resolves to null (search surface disabled). New search providers
// (brave, bing, tavily, serper...) slot in with a new case + class.
internal static class SearchProviderFactory
{
    public static ISearchProvider? Create(string provider, HttpClient httpClient, string? apiKey = null)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "":
            case "none":
                return null;

            case "duckduckgo":
                return new DuckDuckGoSearchProvider(httpClient);

            case "jina":
                return new JinaSearchProvider(httpClient, apiKey);

            default:
                throw new InvalidOperationException(
                    $"Unknown web search provider '{provider}'. Supported: none, duckduckgo, jina.");
        }
    }
}