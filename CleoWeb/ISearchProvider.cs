using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Web;

// One search hit. Title/Url always present; Snippet is best-effort.
internal sealed record SearchResultItem(string Title, string Url, string? Snippet);

internal sealed record SearchResult(bool IsError, string? ErrorMessage, IReadOnlyList<SearchResultItem> Items)
{
    public static SearchResult Error(string message) => new(true, message, Array.Empty<SearchResultItem>());
}

// A web search provider, mirroring IEmbeddingProvider / IFetchProvider:
// implementations selected by a factory, registered by Name. "duckduckgo" is
// the free default; more providers (brave, bing, tavily...) slot in via the
// factory switch.
internal interface ISearchProvider
{
    string Name { get; }

    Task<SearchResult> SearchAsync(string query, int count, CancellationToken ct);
}