using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SocialPoster.Core.Services;

/// <summary>
/// The <c>web_fetch</c> tool for models that have no browsing of their own. A model chooses the URL, so this must
/// never reach the user's own machine or network: every connection is validated at connect time (which also defeats
/// DNS rebinding), redirects are followed by hand and re-validated, and only ports 80/443 are allowed.
/// </summary>
public sealed partial class SafeWebFetcher : IDisposable
{
    public const int MaxRedirects = 4;
    public const int MaxBodyBytes = 1_000_000;
    public const int MaxTextChars = 15_000;

    private readonly HttpClient _http;
    private readonly bool _allowPrivate;

    public SafeWebFetcher() : this(false) { }

    /// <param name="allowPrivateNetworks">For tests only: lets the fetcher reach loopback so it can talk to a local server.</param>
    internal SafeWebFetcher(bool allowPrivateNetworks)
    {
        _allowPrivate = allowPrivateNetworks;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // A proxy would be connected to instead of the target, bypassing the address check below.
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SocialPoster/1.0)");
    }

    /// <summary>True for addresses on the public internet; false for loopback, private, link-local, CGNAT, multicast and similar.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
            return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224 ||
                     (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     (b[0] == 198 && b[1] is 18 or 19));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal ||
                     ip.IsIPv6Teredo || (b[0] == 0x20 && b[1] == 0x02) /* 6to4 can embed a private IPv4 */);
        }
        return false;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0) throw new HttpRequestException($"Couldn't resolve {host}.");
        if (!_allowPrivate && addresses.Any(a => !IsPublicAddress(a)))
            throw new HttpRequestException($"Blocked: {host} points at a private or local address.");

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new HttpRequestException($"Couldn't connect to {host}.", last);
    }

    /// <summary>
    /// Fetches a public page and returns its readable text. Failures a model can reason about (404, blocked host,
    /// non-text content, timeouts) come back as text rather than exceptions, so a missing profile can be reported as missing.
    /// </summary>
    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return "Error: not a valid absolute URL.";
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (UrlProblem(uri) is { } problem) return $"Error: {problem}";
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.1");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {uri}";

                var type = response.Content.Headers.ContentType?.MediaType ?? "text/html";
                var isHtml = type is "text/html" or "application/xhtml+xml";
                if (!isHtml && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("json", StringComparison.OrdinalIgnoreCase) && !type.Contains("xml", StringComparison.OrdinalIgnoreCase))
                    return $"Error: {uri} is {type}, not a readable text page.";

                var body = await ReadLimitedAsync(response, ct);
                var text = isHtml ? HtmlToText(body) : body.Trim();
                if (text.Length > MaxTextChars) text = text[..MaxTextChars] + "\n[truncated]";
                return text.Length == 0 ? $"HTTP 200 for {uri}, but the page had no readable text." : text;
            }
            return "Error: too many redirects.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: couldn't fetch {uri}: {ex.Message}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Error: {uri} took too long to respond.";
        }
    }

    private string? UrlProblem(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return "only http and https URLs can be fetched.";
        if (uri.UserInfo.Length != 0) return "URLs with embedded credentials are not allowed.";
        if (!_allowPrivate && uri.Port is not (80 or 443)) return "only the standard web ports (80 and 443) can be fetched.";
        return null;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxBodyBytes && (read = await stream.ReadAsync(chunk, ct)) > 0)
            buffer.Write(chunk, 0, (int)Math.Min(read, MaxBodyBytes - buffer.Length));
        Encoding encoding = Encoding.UTF8;
        if (response.Content.Headers.ContentType?.CharSet is { } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* Unknown charset names fall back to UTF-8. */ }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|template)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex("""(?<name>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|tr|h[1-6]|section|article|header|footer|table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\s*\n\s*(\n\s*)*")]
    private static partial Regex BlankLinesRegex();

    private static readonly HashSet<string> UsefulMeta = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "og:title", "og:description", "og:site_name", "twitter:title", "twitter:description",
    };

    /// <summary>
    /// Reduces HTML to plain text, keeping the title and description meta tags first: profile pages
    /// (X, LinkedIn) often put the name and bio only there.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var sb = new StringBuilder();
        if (TitleRegex().Match(html) is { Success: true } title && Clean(title.Groups["t"].Value) is { Length: > 0 } t)
            sb.AppendLine($"Title: {t}");

        foreach (Match meta in MetaRegex().Matches(html))
        {
            string? name = null, content = null;
            foreach (Match attribute in AttributeRegex().Matches(meta.Value))
            {
                var key = attribute.Groups["name"].Value;
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase) || key.Equals("property", StringComparison.OrdinalIgnoreCase))
                    name = attribute.Groups["value"].Value;
                else if (key.Equals("content", StringComparison.OrdinalIgnoreCase))
                    content = attribute.Groups["value"].Value;
            }
            if (name is not null && UsefulMeta.Contains(name) && Clean(content ?? "") is { Length: > 0 } value)
                sb.AppendLine($"{name}: {value}");
        }
        if (sb.Length > 0) sb.AppendLine();

        var body = NoiseRegex().Replace(html, " ");
        body = BlockRegex().Replace(body, "\n");
        body = TagRegex().Replace(body, " ");
        body = WebUtility.HtmlDecode(body);
        body = SpacesRegex().Replace(body, " ");
        body = BlankLinesRegex().Replace(body, "\n");
        sb.Append(body.Trim());
        return sb.ToString();

        static string Clean(string value) => SpacesRegex().Replace(WebUtility.HtmlDecode(value).ReplaceLineEndings(" "), " ").Trim();
    }

    public void Dispose() => _http.Dispose();
}
