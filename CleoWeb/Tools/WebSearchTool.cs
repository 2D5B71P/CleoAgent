using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Web;
using CleoAgent.Core.Web.Search;

namespace CleoAgent.Core.Tools.Impl
{
    // Searches the web and returns result headlines (title, url, snippet) for
    // the model to act on. Provider-driven via config [web.search]: the primary
    // provider runs first; if it errors or returns nothing, the fallback runs.
    // Both may be "none" (search disabled).
    internal sealed class WebSearchTool : IAgentTool
    {
        private readonly SearchConfig _config;
        private readonly ISearchProvider? _primary;
        private readonly ISearchProvider? _fallback;

        public WebSearchTool(SearchConfig config, ISearchProvider? primary, ISearchProvider? fallback)
        {
            _config = config;
            _primary = primary;
            _fallback = fallback;
        }

        public string Name => "web_search";
        public string Description => "Searches the web for a query and returns matching results (title, url, snippet).";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query text."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("query", out var queryElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: query", true);
                }

                var query = queryElement.GetString();
                if (string.IsNullOrWhiteSpace(query))
                {
                    return new ToolResult(call.Id, "Query cannot be empty.", true);
                }

                if (_primary is null && _fallback is null)
                {
                    return new ToolResult(call.Id,
                        "web_search is not configured: both web.search.provider and web.search.provider_fallback are \"none\". " +
                        "Set one in config.json to enable searching.", true);
                }

                const int count = 5;

                // 1. Primary.
                SearchResult? primaryResult = null;
                if (_primary is not null)
                {
                    primaryResult = await _primary.SearchAsync(query, count, cancellationToken);
                    if (!primaryResult.IsError && primaryResult.Items.Count > 0)
                    {
                        return Render(call.Id, primaryResult);
                    }
                }

                // 2. Fallback.
                if (_fallback is not null)
                {
                    SearchResult result = await _fallback.SearchAsync(query, count, cancellationToken);
                    if (!result.IsError && result.Items.Count > 0)
                    {
                        return Render(call.Id, result);
                    }

                    return new ToolResult(call.Id, FailedSearch(query, primaryResult, result), true);
                }

                // 3. Primary failed and there is no fallback.
                return new ToolResult(call.Id, FailedSearch(query, primaryResult, null), true);
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        private static ToolResult Render(string callId, SearchResult result)
        {
            var sb = new StringBuilder();
            int idx = 1;
            foreach (var item in result.Items)
            {
                sb.Append(idx++).Append(". ").AppendLine(item.Title);
                sb.Append("   ").AppendLine(item.Url);
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                {
                    sb.Append("   ").AppendLine(item.Snippet);
                }
                sb.AppendLine();
            }
            return new ToolResult(callId, sb.ToString());
        }

        private static string FailedSearch(string query, SearchResult? last, SearchResult? fallbackLast)
        {
            var errors = new List<string>();
            if (last is not null && last.IsError)
            {
                errors.Add($"primary: {last.ErrorMessage}");
            }
            if (fallbackLast is not null && fallbackLast.IsError)
            {
                errors.Add($"fallback: {fallbackLast.ErrorMessage}");
            }
            string detail = errors.Count > 0 ? string.Join("; ", errors) : "no results returned";
            return $"web_search for \"{query}\" failed: {detail}";
        }

        public string Describe(ToolCall call)
        {
            string? query = GetStringArgument(call, "query");
            return string.IsNullOrWhiteSpace(query)
                ? "Searching the web"
                : $"Searching: {query}";
        }

        private static string? GetStringArgument(ToolCall call, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return document.RootElement.TryGetProperty(name, out var element)
                    ? element.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}