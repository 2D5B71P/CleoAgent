using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace CleoAgent.Core.Web.Search;

// The "duckduckgo" search provider: scrapes the DuckDuckGo html results page.
// Free and keyless (the default), but the public html endpoint has no SLA and
// may throttle/block heavy use - so it can be swapped for a keyed provider via
// config. Results are parsed defensively with AngleSharp.
//
// Note: this is *not* the (limited, instant-answer) api.duckduckgo.com endpoint;
// it's the full html results page, which carries real result links + snippets.
internal sealed class DuckDuckGoSearchProvider : ISearchProvider
{
    public string Name => "duckduckgo";

    private const string SearchUrl = "https://html.duckduckgo.com/html/?q=";

    private readonly HttpClient _http;

    public DuckDuckGoSearchProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task<SearchResult> SearchAsync(string query, int count, CancellationToken ct)
    {
        try
        {
            string url = HtmlUrl(query);
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 CleoAgent/1.0");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return SearchResult.Error($"DuckDuckGo (HTTP {(int)response.StatusCode} {response.ReasonPhrase})");
            }

            string html = await response.Content.ReadAsStringAsync(ct);
            var items = ParseResults(html, count);

            if (items.Count == 0)
            {
                // Could be a legit no-results page, or a bot/anomaly page.
                // Either way there's nothing actionable - keep a message.
                return SearchResult.Error(
                    html.Contains("anomaly", StringComparison.OrdinalIgnoreCase)
                        ? "DuckDuckGo returned an anomaly/challenge page (bot detection). Try a keyed provider."
                        : "DuckDuckGo returned no results.");
            }

            return new SearchResult(false, null, items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SearchResult.Error(ex.Message);
        }
    }

    private static string HtmlUrl(string query) =>
        SearchUrl + Uri.EscapeDataString(query);

    internal static List<SearchResultItem> ParseResults(string html, int count)
    {
        var items = new List<SearchResultItem>();

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var doc = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Each result is a <div class="result"> containing:
            //   <a class="result__a" href="...">title</a>
            //   <a class="result__snippet">snippet</a>
            foreach (var result in doc.QuerySelectorAll("div.result"))
            {
                if (items.Count >= count) break;

                var link = result.QuerySelector("a.result__a");
                if (link is null) continue;

                string title = link.TextContent.Trim();
                string href = link.GetAttribute("href") ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                string url = href;
                // DDG wraps real URLs in its redirect: try to recover the target.
                int uddg = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
                if (uddg >= 0)
                {
                    string decoded = Uri.UnescapeDataString(href.Substring(uddg + 5));
                    int amp = decoded.IndexOf('&');
                    url = amp >= 0 ? decoded.Substring(0, amp) : decoded;
                }

                var snippetNode = result.QuerySelector(".result__snippet");
                string? snippet = snippetNode?.InnerHtml;
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    // Collapse any HTML entities / tags in the snippet.
                    snippet = StripHtml(snippet);
                }

                items.Add(new SearchResultItem(title, url, snippet));
            }
        }
        catch
        {
            // Malformed HTML shouldn't crash the whole call; return what we have.
        }

        return items;
    }

    private static string StripHtml(string html)
    {
        return System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " "));
    }
}