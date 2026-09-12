using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Web;
using CleoAgent.Core.Web.Fetch;

namespace CleoAgent.Core.Tools.Impl
{
    // Fetches a URL and returns extracted markdown/text for the model to read.
    //
    // Pipeline (provider-driven, see config [web.fetch]):
    //   1. primary provider runs ("default" = our free local ReverseMarkdown).
    //   2. if it errors OR its output fails the readability gate, run the
    //      configured fallback provider ("jina", etc.).
    //   3. if neither produced readable content -> fatal error (IsError = true).
    //
    // Either the primary or the fallback may be "none" (disabled). If both are
    // disabled, the tool reports it is unconfigured rather than fabricating a
    // fetch.
    internal sealed class WebFetchTool : IAgentTool
    {
        private readonly FetchConfig _config;
        private readonly IFetchProvider? _primary;
        private readonly IFetchProvider? _fallback;

        public WebFetchTool(FetchConfig config, IFetchProvider? primary, IFetchProvider? fallback)
        {
            _config = config;
            _primary = primary;
            _fallback = fallback;
        }

        public string Name => "web_fetch";
        public string Description => "Fetches a URL and returns its content as readable markdown text.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Absolute URL (https://...) to fetch."
            }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("url", out var urlElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: url", true);
                }

                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return new ToolResult(call.Id, "URL cannot be empty.", true);
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    return new ToolResult(call.Id, $"Invalid or unsupported URL: {url}", true);
                }

                if (_primary is null && _fallback is null)
                {
                    return new ToolResult(call.Id,
                        "web_fetch is not configured: both web.fetch.provider and web.fetch.provider_fallback are \"none\". " +
                        "Set one in config.json to enable fetching.", true);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutS));

                // 1. Primary.
                if (_primary is not null)
                {
                    FetchResult result = await _primary.FetchAsync(url, _config.MaxChars, timeoutCts.Token);
                    if (!result.IsError && Readability.Passes(result.Content))
                    {
                        return Render(call.Id, result);
                    }

                    // Primary failed or failed the readability gate -> fall through.
                }

                // 2. Fallback.
                if (_fallback is not null)
                {
                    FetchResult result = await _fallback.FetchAsync(url, _config.MaxChars, timeoutCts.Token);
                    if (!result.IsError)
                    {
                        // The fallback is a smarter provider (Jina renders JS); its
                        // whole purpose is to succeed where readability failed. If it
                        // still returns nothing useful, that's a genuine failure.
                        if (Readability.Passes(result.Content))
                        {
                            return Render(call.Id, result);
                        }

                        return new ToolResult(call.Id, $"Fetched {url} but content was empty/unreadable.", true);
                    }

                    return new ToolResult(call.Id,
                        $"web_fetch failed on both providers. Primary: {(result.IsError ? result.ErrorMessage : "readability gate failed")}. " +
                        $"Fallback ({_fallback.Name}): {result.ErrorMessage}", true);
                }

                // 3. Primary existed but gave nothing, and there is no fallback.
                return new ToolResult(call.Id,
                    $"Fetched {url} via provider '{_primary?.Name}' but content was empty/unreadable, and no fallback provider is configured.", true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new ToolResult(call.Id, $"Fetch timed out after {_config.RequestTimeoutS}s.", true);
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        private static ToolResult Render(string callId, FetchResult result)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Title))
            {
                sb.Append("Title: ").AppendLine(result.Title);
            }
            sb.AppendLine($"Source: {result.Url}");
            sb.AppendLine();
            sb.AppendLine(result.Content);
            return new ToolResult(callId, sb.ToString());
        }

        public string Describe(ToolCall call)
        {
            string? url = GetStringArgument(call, "url");
            return string.IsNullOrWhiteSpace(url)
                ? "Fetching a web page"
                : $"Fetching {url}";
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