using System;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Web;

// A single fetched page. Content is the extracted text/markdown handed back to
// the model; Title/Url are best-effort metadata. Empty Content != failure:
// check IsError to distinguish a genuinely failed fetch from an empty page.
internal sealed record FetchResult(
    string Url,
    string Content,
    string? Title,
    bool IsError,
    string? ErrorMessage)
{
    public static FetchResult Error(string url, string message) =>
        new(url, "", null, true, message);
}

// A provider that retrieves a URL and returns extracted text/markdown.
// Mirrors IEmbeddingProvider: implementations are selected by a factory and
// registered by Name. "default" is our local ReverseMarkdown converter.
internal interface IFetchProvider
{
    string Name { get; }

    Task<FetchResult> FetchAsync(string url, int maxChars, CancellationToken ct);
}