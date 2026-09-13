using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Web.Fetch;

// The "jina" fetch provider: Jina Reader (https://r.jina.ai/<url>). Renders
// JS-heavy or otherwise difficult pages server-side and returns clean markdown,
// so it is a good readability fallback for the local "default" provider.
//
// Uses the API key if one is provided (from config provider_api_key, else the
// JINA_API_KEY environment variable) for better rate limits / reliability;
// works keyless otherwise. Fails cleanly with a descriptive error if the
// endpoint itself errors.
internal sealed class JinaFetchProvider : IFetchProvider
{
    public string Name => "jina";

    private const string EndpointPrefix = "https://r.jina.ai/";

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public JinaFetchProvider(HttpClient http, string? apiKey = null)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<FetchResult> FetchAsync(string url, int maxChars, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, EndpointPrefix + url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 CleoAgent/1.0");

            string? key = ResolveKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Error(url, $"Jina (HTTP {(int)response.StatusCode} {response.ReasonPhrase})");
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            content = DefaultFetchProvider.Truncate(content, maxChars);

            return new FetchResult(url, content, null, false, null);
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

    // Resolution order: explicit config key wins, then the JINA_API_KEY env
    // var, then keyless (null) so the key-optional providers keep working.
    private string? ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            return _apiKey;
        }

        return System.Environment.GetEnvironmentVariable("JINA_API_KEY");
    }
}