using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SocialPoster.Posting.Services;

public sealed record LinkCard(string? Title, string? Description, byte[]? ThumbBytes, string? ThumbMimeType);

/// <summary>
/// Scrapes a page's Open Graph / Twitter Card meta tags to build the title, description and thumbnail for a
/// Bluesky link-preview embed, the same information the official app collects when you paste a URL.
/// </summary>
public static partial class LinkCardFetcher
{
    [GeneratedRegex("""<meta\s[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    // Matches one attribute with either quote style, so an apostrophe inside a double-quoted value (or vice versa) is kept.
    [GeneratedRegex("""(?<name>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("""<title[^>]*>(?<title>[^<]*)</title>""", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTagRegex();

    private const long MaxThumbBytes = 1_000_000;

    private const string CardybEndpoint = "https://cardyb.bsky.app/v1/extract";

    public static async Task<LinkCard?> FetchAsync(HttpClient http, string url, CancellationToken ct = default) =>
        await FetchFromCardybAsync(http, url, ct) ?? await ScrapeAsync(http, url, ct);

    private static async Task<LinkCard?> ScrapeAsync(HttpClient http, string url, CancellationToken ct)
    {
        string html;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            // Some sites serve minimal/no OG tags to unrecognized clients; a browser UA gets the real markup.
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SocialPoster/1.0; +https://github.com)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            // Meta tags live in <head>; reading the whole page for huge documents wastes memory, so cap it.
            var buffer = new char[200_000];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            html = new string(buffer, 0, read);
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            // Attribute order varies (content may precede property), so collect them all per tag.
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in AttributeRegex().Matches(tag.Value))
            {
                attrs.TryAdd(a.Groups["name"].Value, a.Groups["value"].Value);
            }

            var key = (attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name"))?.Trim();
            if (!string.IsNullOrEmpty(key) && attrs.TryGetValue("content", out var content) && !meta.ContainsKey(key))
            {
                meta[key] = System.Net.WebUtility.HtmlDecode(content.Trim());
            }
        }

        var title = meta.GetValueOrDefault("og:title") ?? meta.GetValueOrDefault("twitter:title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var titleMatch = TitleTagRegex().Match(html);
            title = titleMatch.Success ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim()) : null;
        }

        var description = meta.GetValueOrDefault("og:description") ?? meta.GetValueOrDefault("twitter:description") ?? meta.GetValueOrDefault("description");
        var imageUrl = meta.GetValueOrDefault("og:image") ?? meta.GetValueOrDefault("twitter:image");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var (thumbBytes, thumbMime) = await DownloadThumbAsync(http, url, imageUrl, ct);
        return new LinkCard(title, description, thumbBytes, thumbMime);
    }

    /// <summary>Asks Bluesky's own link-preview service (what the official app uses) for the card. Returns null on
    /// any failure so the caller can fall back to scraping.</summary>
    private static async Task<LinkCard?> FetchFromCardybAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{CardybEndpoint}?url={Uri.EscapeDataString(url)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CardybResponse>(ct);
            if (result is null || !string.IsNullOrEmpty(result.Error) ||
                (string.IsNullOrWhiteSpace(result.Title) && string.IsNullOrWhiteSpace(result.Description) && string.IsNullOrWhiteSpace(result.Image)))
            {
                return null;
            }

            var (bytes, mime) = await DownloadThumbAsync(http, url, result.Image, ct);
            return new LinkCard(result.Title, result.Description, bytes, mime);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(byte[]? Bytes, string? MimeType)> DownloadThumbAsync(HttpClient http, string pageUrl, string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return (null, null);
        }

        try
        {
            var absoluteImageUrl = Uri.TryCreate(imageUrl, UriKind.Absolute, out var abs) ? abs
                : Uri.TryCreate(new Uri(pageUrl), imageUrl, out var rel) ? rel
                : null;
            if (absoluteImageUrl is null)
            {
                return (null, null);
            }

            using var imgResponse = await http.GetAsync(absoluteImageUrl, ct);
            if (imgResponse.IsSuccessStatusCode &&
                (imgResponse.Content.Headers.ContentLength is null or <= MaxThumbBytes))
            {
                var bytes = await imgResponse.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length <= MaxThumbBytes)
                {
                    return (bytes, imgResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
                }
            }
        }
        catch
        {
            // A missing/oversized thumbnail still leaves a usable title/description card.
        }

        return (null, null);
    }

    private sealed record CardybResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("image")] string? Image);
}
