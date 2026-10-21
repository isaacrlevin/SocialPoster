using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocialPoster.Core.Services;

public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>A web search service the agent's <c>web_search</c> tool calls into.</summary>
public interface IWebSearch
{
    string Name { get; }

    /// <exception cref="InvalidOperationException">The service rejected the request (bad key, quota, wrong URL).</exception>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default);
}

public static partial class WebSearch
{
    /// <returns>The configured service, or null when search is switched off.</returns>
    public static IWebSearch? Create(SearchSettings settings, HttpClient http) => settings.Kind switch
    {
        SearchKind.Brave => new BraveSearch(http, settings.ApiKey ?? ""),
        SearchKind.Tavily => new TavilySearch(http, settings.ApiKey ?? ""),
        SearchKind.SearXNG => new SearxngSearch(http, settings.Endpoint ?? ""),
        _ => null,
    };

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    internal static string PlainText(string? value, int max = 400)
    {
        var text = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(value ?? "", "")).ReplaceLineEndings(" ").Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>Reads the results array, dropping entries without a usable http(s) URL.</summary>
    internal static IReadOnlyList<WebSearchResult> ReadResults(JsonElement array, string title, string url, string snippet, int count)
    {
        var results = new List<WebSearchResult>();
        if (array.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var address = Get(item, url);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            results.Add(new(PlainText(Get(item, title), 200), uri.AbsoluteUri, PlainText(Get(item, snippet))));
            if (results.Count >= count) break;
        }
        return results;
    }

    private static string? Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static async Task<JsonElement> SendAsync(HttpClient http, string service, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach {service}: {ex.Message}", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var advice = (int)response.StatusCode switch
                {
                    401 or 403 => " Check the API key.",
                    429 => " Rate limited or out of quota.",
                    _ => "",
                };
                throw new InvalidOperationException($"{service} returned {(int)response.StatusCode}.{advice}");
            }
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return json.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{service} returned something that isn't JSON. Check the URL.", ex);
            }
        }
    }
}

internal sealed class BraveSearch(HttpClient http, string apiKey) : IWebSearch
{
    public string Name => "Brave Search";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?count={count}&q={Uri.EscapeDataString(query)}");
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        var data = await WebSearch.SendAsync(http, Name, request, ct);
        return data.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results)
            ? WebSearch.ReadResults(results, "title", "url", "description", count) : [];
    }
}

internal sealed class TavilySearch(HttpClient http, string apiKey) : IWebSearch
{
    public string Name => "Tavily";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query, max_results = count, search_depth = "basic" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var data = await WebSearch.SendAsync(http, Name, request, ct);
        return data.TryGetProperty("results", out var results)
            ? WebSearch.ReadResults(results, "title", "url", "content", count) : [];
    }
}

/// <summary>Self-hosted metasearch. Needs the JSON output format enabled in the instance's settings.</summary>
internal sealed class SearxngSearch(HttpClient http, string endpoint) : IWebSearch
{
    public string Name => "SearXNG";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{endpoint.TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}");
        request.Headers.Accept.ParseAdd("application/json");
        var data = await WebSearch.SendAsync(http, Name, request, ct);
        return data.TryGetProperty("results", out var results)
            ? WebSearch.ReadResults(results, "title", "url", "content", count) : [];
    }
}
