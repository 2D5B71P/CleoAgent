using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Modules;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;
using CleoAgent.Core.Web;
using CleoAgent.Core.Web.Fetch;
using CleoAgent.Core.Web.Search;

namespace CleoAgent.Core.Modules;

// The web module: web_fetch + web_search tools. Owns the fetch/search
// provider selection (its config section): "default" = local Readability,
// "jina"/"duckduckgo" = API, "none" = unconfigured (no external cost).
internal sealed class WebModule : IModule
{
    private readonly HttpClient _http;

    public WebModule(HttpClient http)
    {
        _http = http;
    }

    public ModuleManifest Manifest() => ModuleManifest.Create(
        "web",
        "1.0.0",
        "HTTP fetch + search: web_fetch, web_search (local readability, jina, " +
        "duckduckgo providers via the [modules.web] config section).",
        modelRequestable: true);

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        ConfigSection cfg = ctx.Config;

        // Structured config: fetch and search are sub-blocks of [modules.web]
        // ("modules.web.fetch.*" / "modules.web.search.*"), each with their
        // own provider + fallback + optional API key.
        var fetch = new FetchConfig(
            cfg.GetIntAt("fetch", "request_timeout_s", FetchConfig.Default.RequestTimeoutS),
            cfg.GetIntAt("fetch", "max_chars", FetchConfig.Default.MaxChars),
            cfg.GetStringAt("fetch", "provider", FetchConfig.Default.Provider),
            cfg.GetStringAt("fetch", "provider_fallback", FetchConfig.Default.ProviderFallback),
            cfg.GetStringAt("fetch", "provider_api_key", FetchConfig.Default.ProviderApiKey),
            cfg.GetStringAt("fetch", "fallback_provider_api_key", FetchConfig.Default.FallbackProviderApiKey));

        var search = new SearchConfig(
            cfg.GetStringAt("search", "provider", SearchConfig.Default.Provider),
            cfg.GetStringAt("search", "provider_fallback", SearchConfig.Default.ProviderFallback),
            cfg.GetStringAt("search", "provider_api_key", SearchConfig.Default.ProviderApiKey),
            cfg.GetStringAt("search", "fallback_provider_api_key", SearchConfig.Default.FallbackProviderApiKey));

        IFetchProvider? fetchPrimary = FetchProviderFactory.Create(fetch.Provider, _http, fetch.ProviderApiKey);
        IFetchProvider? fetchFallback = FetchProviderFactory.Create(fetch.ProviderFallback, _http, fetch.FallbackProviderApiKey);
        ISearchProvider? searchPrimary = SearchProviderFactory.Create(search.Provider, _http, search.ProviderApiKey);
        ISearchProvider? searchFallback = SearchProviderFactory.Create(search.ProviderFallback, _http, search.FallbackProviderApiKey);

        ctx.Tools.Register(new WebFetchTool(fetch, fetchPrimary, fetchFallback));
        ctx.Tools.Register(new WebSearchTool(search, searchPrimary, searchFallback));
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        // No unregisterable state; providers are stateless facades over Http.
    }
}