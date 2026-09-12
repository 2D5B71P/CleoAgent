using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Web.Search;

// The "jina" search provider: Jina's s.jina.ai search endpoint. Server-side
// search that returns results as markdown-bearing text. Like the Jina fetch
// provider it works keyless (rate-limited) but benefits from an API key
// (Bearer auth) when one is configured - see web.search.provider_api_key.
internal sealed class JinaSearchProvider : ISearchProvider
{
    public string Name => "jina";

    private const string SearchUrl = "https://s.jina.ai/";

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public JinaSearchProvider(HttpClient http, string? apiKey = null)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<SearchResult> SearchAsync(string query, int count, CancellationToken ct)
    {
        try
        {
            string url = SearchUrl + Uri.EscapeDataString(query);
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 CleoAgent/1.0");

            string? key = ResolveKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return SearchResult.Error($"Jina (HTTP {(int)response.StatusCode} {response.ReasonPhrase})");
            }

            string markdown = await response.Content.ReadAsStringAsync(ct);
            var items = ParseResults(markdown, count);

            if (items.Count == 0)
            {
                return SearchResult.Error("Jina returned no parseable results.");
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

    // Jina search returns markdown with links, e.g. headings/lines like:
    //   [Result Title](https://example.com) — snippet text
    // We extract link-title pairs (and a trailing snippet if present), taking
    // the first `count` of them. Image-markdown (`![alt](url)`) and links that
    // are clearly navigation/junk (no http scheme) are skipped.
    private static List<SearchResultItem> ParseResults(string markdown, int count)
    {
        var items = new List<SearchResultItem>();

        foreach (Match match in Regex.Matches(markdown, @"!?\[([^\]]+)\]\((https?://[^)\s]+)\)"))
        {
            string title = WebUtilityDecode(match.Groups[1].Value).Trim();
            string url = match.Groups[2].Value;

            // Image embeds come as ![alt](url) or the nested [![alt](url)](...)
            // form; their "title" is alt text, not a search hit. Detect either
            // the leading "![" marker or an alt/URL hint, and skip.
            bool isImage = match.Value.StartsWith("![", StringComparison.Ordinal)
                || title.StartsWith("Image", StringComparison.OrdinalIgnoreCase)
                || title.Contains("![", StringComparison.Ordinal)
                || title.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".png", StringComparison.OrdinalIgnoreCase);
            if (isImage || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)
                || string.Equals(title, url, StringComparison.Ordinal))
            {
                continue;
            }

            items.Add(new SearchResultItem(title, url, null));

            if (items.Count >= count) break;
        }

        return items;
    }

    private static string WebUtilityDecode(string value)
    {
        return System.Net.WebUtility.HtmlDecode(value);
    }

    // Resolution order: explicit config key wins, then the JINA_API_KEY env
    // var, then null (keyless best-effort).
    private string? ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            return _apiKey;
        }

        return System.Environment.GetEnvironmentVariable("JINA_API_KEY");
    }
}