using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Web.Fetch;

// The "default" fetch provider: free, local, no key - but unlike a naive
// HTML->Markdown dump it uses SmartReader, a port of the Mozilla Readability
// algorithm (the same one powering Firefox Reader View). That gives us the
// battle-tested, web-general article extraction instead of per-site tuning:
// nav, sidebars, infoboxes and TOC are dropped by the algorithm, and the
// extracted article is converted to markdown with ReverseMarkdown.
//
// Pipeline:
//   1. Our HttpClient fetches the page (timeout / UA / size handled here).
//   2. SmartReader.Reader.ParseArticle(url, html) extracts the article.
//   3. article.IsReadable == false -> FetchResult.Error (WebFetchTool then
//      tries the configured fallback provider, e.g. jina for JS-rendered pages).
//   4. IsReadable -> ReverseMarkdown converts the clean article HTML to markdown.
//
// Pages that are JS-rendered yield no article in static HTML, so IsReadable
// will be false - that's the designed hand-off point to a rendering provider.
internal sealed class DefaultFetchProvider : IFetchProvider
{
    public string Name => "default";

    private readonly HttpClient _http;

    public DefaultFetchProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task<FetchResult> FetchAsync(string url, int maxChars, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CleoAgent/1.0");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Error(url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            string html = await response.Content.ReadAsStringAsync(ct);

            SmartReader.Article article;
            try
            {
                // ParseArticle(uri, htmlText, userAgent): parses the HTML we
                // already fetched (keeps our timeout/UA/size behavior); no
                // re-download. NOTE: the 2-arg ParseArticle(uri, userAgent)
                // overload fetches itself - do not use it here.
                article = SmartReader.Reader.ParseArticle(url, html,
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CleoAgent/1.0");
            }
            catch (Exception ex)
            {
                // SmartReader threw - malformed document etc.
                return FetchResult.Error(url, $"Readability extraction failed: {ex.Message}");
            }

            // The algorithm found no article in the static HTML (JS-rendered
            // page, login wall, or pure-navigation page). Hand off to the
            // configured fallback provider rather than returning garbage.
            if (!article.IsReadable)
            {
                return FetchResult.Error(url,
                    "No readable article found in the static HTML (page may be JS-rendered or boilerplate-only).");
            }

            // The extracted article is clean plain text (SmartReader's
            // TextContent: headings/paragraphs/line-breaks preserved, all
            // HTML/styling stripped). We use it directly rather than converting
            // article.Content with ReverseMarkdown - ReverseMarkdown.Convert on
            // an HTML fragment re-echoes unwrapped tags/attributes ("<div
            // id=readability-page-1>", svg paths, parsoid data-mw) as literal
            // text, which pollutes the output.
            string converted = string.IsNullOrWhiteSpace(article.TextContent)
                ? article.Content
                : article.TextContent;

            string content = Truncate(converted, maxChars);

            return new FetchResult(url, content, article.Title, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FetchResult.Error(url, ex.Message);
        }
    }

    internal static string Truncate(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content ?? "";
        }

        // Cut on a sensible boundary near maxChars so we don't split mid-word.
        int end = maxChars;
        while (end < content.Length && !char.IsWhiteSpace(content[end]) && end < maxChars + 40)
        {
            end++;
        }

        return content.Substring(0, end) + "\n\n[...truncated...]";
    }
}