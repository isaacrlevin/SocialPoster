using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocialPoster.Posting.Services;

/// <summary>A federation account returned by the configured instance.</summary>
public sealed record MastodonAccount(
    string Id, string Acct, string Uri, string Url,
    [property: JsonPropertyName("display_name")] string DisplayName,
    string? Avatar, string? Note);

/// <summary>Mastodon REST client. Authentication belongs to each request and never to shared default headers.</summary>
public sealed class MastodonClient(HttpClient http, ICredentialStore secrets)
{
    private sealed record Connection(Uri Instance, string Token);

    public static Uri NormalizeInstance(string? value)
    {
        if (!System.Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Mastodon instance must be an HTTPS origin, e.g. https://mastodon.social (no path, query or credentials).");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var settings = await secrets.GetSnapshotAsync([SettingKeys.MastodonInstance, SettingKeys.MastodonAccessToken]);
        return !string.IsNullOrWhiteSpace(settings[SettingKeys.MastodonInstance]) &&
            !string.IsNullOrWhiteSpace(settings[SettingKeys.MastodonAccessToken]);
    }

    private async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        var settings = await secrets.GetSnapshotAsync([SettingKeys.MastodonInstance, SettingKeys.MastodonAccessToken], ct);
        var instance = NormalizeInstance(settings[SettingKeys.MastodonInstance]);
        var token = settings[SettingKeys.MastodonAccessToken];
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Connect Mastodon under Accounts first.");
        return new(instance, token);
    }

    /// <summary>Verifies the supplied pair before persisting either value. Changing instance requires a new token.</summary>
    public async Task<MastodonAccount> ConfigureAsync(string? instance, string? token, CancellationToken ct = default)
    {
        var uri = NormalizeInstance(instance);
        var settings = await secrets.GetSnapshotAsync([SettingKeys.MastodonInstance, SettingKeys.MastodonAccessToken], ct);
        var oldInstance = settings[SettingKeys.MastodonInstance];
        if (string.IsNullOrWhiteSpace(token))
        {
            if (oldInstance is null || NormalizeInstance(oldInstance) != uri)
                throw new InvalidOperationException("Enter a token for the new instance; saved tokens are never reused on a different server.");
            token = settings[SettingKeys.MastodonAccessToken];
        }
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("A Mastodon access token is required.");
        var connection = new Connection(uri, token.Trim());
        ValidateToken(connection.Token);
        var account = await VerifyAsync(connection, ct);
        await secrets.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.MastodonInstance] = uri.AbsoluteUri,
            [SettingKeys.MastodonAccessToken] = connection.Token,
        }, ct);
        return account;
    }

    public async Task<MastodonAccount> VerifyCredentialsAsync(CancellationToken ct = default) =>
        await VerifyAsync(await ConnectAsync(ct), ct);

    private async Task<MastodonAccount> VerifyAsync(Connection connection, CancellationToken ct) =>
        ReadAccount(await SendAsync(connection, HttpMethod.Get, "api/v1/accounts/verify_credentials", null, ct));

    public async Task<IReadOnlyList<MastodonAccount>> SearchAsync(string query, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        return await SearchAsync(connection, query, ct);
    }

    private async Task<IReadOnlyList<MastodonAccount>> SearchAsync(Connection connection, string query, CancellationToken ct)
    {
        var data = await SendAsync(connection, HttpMethod.Get,
            $"api/v2/search?type=accounts&resolve=true&limit=6&q={System.Uri.EscapeDataString(query)}", null, ct);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("accounts", out var accounts) ||
            accounts.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Mastodon returned an invalid account search response.");
        return accounts.EnumerateArray().Select(ReadAccount).ToList();
    }

    public async Task<MastodonAccount> LookupAsync(string acct, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        var normalized = MastodonIdentity.NormalizeAcct(acct, connection.Instance)
            ?? throw new InvalidOperationException("Use a Mastodon acct such as user@server.");
        // A server can have different web and federation domains. Let its lookup resolve local usernames.
        var clean = acct.Trim().TrimStart('@');
        var lookup = clean.Contains('@') ? normalized : clean;
        try
        {
            return ReadAccount(await SendAsync(connection, HttpMethod.Get,
                $"api/v1/accounts/lookup?acct={System.Uri.EscapeDataString(lookup)}", null, ct));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Lookup only finds accounts already known to this instance. Resolve remote accounts through search.
            var accounts = await SearchAsync(connection, lookup, ct);
            var matches = accounts.Where(a => clean.Contains('@')
                ? string.Equals(a.Acct, normalized, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a.Acct.Split('@')[0], clean, StringComparison.OrdinalIgnoreCase) &&
                    new Uri(a.Uri).GetLeftPart(UriPartial.Authority) == connection.Instance.GetLeftPart(UriPartial.Authority)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException($"Mastodon could not resolve a unique account for @{lookup}. Use its fully qualified user@server acct.");
            return matches[0];
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<MastodonAccount>> GetFollowsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        var me = await VerifyAsync(connection, ct);
        var next = new Uri(connection.Instance, $"api/v1/accounts/{System.Uri.EscapeDataString(me.Id)}/following?limit=80");
        var visited = new HashSet<string>();
        while (next is not null)
        {
            if (!visited.Add(next.AbsoluteUri)) throw new InvalidOperationException("Mastodon returned a repeated follow page.");
            using var request = Request(connection, HttpMethod.Get, next);
            using var response = await http.SendAsync(request, ct);
            var data = await ReadResponseAsync(response, connection.Token, ct);
            if (data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Mastodon returned an invalid following response.");
            yield return data.EnumerateArray().Select(ReadAccount).ToList();
            next = null;
            if (response.Headers.TryGetValues("Link", out var links))
                foreach (var link in links.SelectMany(l => l.Split(',')))
                    if (link.Contains("rel=\"next\"", StringComparison.Ordinal))
                    {
                        var start = link.IndexOf('<');
                        var end = link.IndexOf('>');
                        if (start < 0 || end <= start || !System.Uri.TryCreate(connection.Instance, link[(start + 1)..end], out var candidate) ||
                            candidate.GetLeftPart(UriPartial.Authority) != connection.Instance.GetLeftPart(UriPartial.Authority) ||
                            candidate.AbsolutePath != request.RequestUri!.AbsolutePath)
                            throw new InvalidOperationException("Mastodon returned an unsafe follow pagination URL.");
                        next = candidate;
                    }
        }
    }

    /// <summary>Uploads all attachments with descriptions, waits for processing, then creates one public status.</summary>
    public async Task<string> PostAsync(RenderedPost post, IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        if (post.IsEmpty || post.IsOverLimit) throw new InvalidOperationException("Mastodon post is empty or exceeds the conservative 500-character limit.");
        var errors = MediaRules.Validate(Platform.Mastodon, media);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        var connection = await ConnectAsync(ct);
        var ids = new List<string>();
        foreach (var attachment in media)
        {
            if (attachment.AltText?.Length > MediaRules.MaxAltTextLength)
                throw new InvalidOperationException("Mastodon media descriptions must be at most 1000 characters.");
            if (attachment.Path is null) throw new InvalidOperationException("Media file is unavailable.");
            await using var file = File.OpenRead(attachment.Path);
            using var form = new MultipartFormDataContent();
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
            form.Add(content, "file", Path.GetFileName(attachment.FileName));
            if (!string.IsNullOrWhiteSpace(attachment.AltText)) form.Add(new StringContent(attachment.AltText), "description");
            var data = await SendAsync(connection, HttpMethod.Post, "api/v2/media", form, ct);
            var id = RequiredString(data, "id");
            for (var attempt = 0; !Ready(data); attempt++)
            {
                if (attempt >= 60) throw new InvalidOperationException("Mastodon media processing timed out; no status was created. Retry later.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                data = await SendAsync(connection, HttpMethod.Get, $"api/v1/media/{System.Uri.EscapeDataString(id)}", null, ct,
                    allowPendingMedia: true);
            }
            ids.Add(id);
        }
        using var body = JsonContent.Create(new { status = post.PayloadText, media_ids = ids, visibility = "public" });
        var status = await SendAsync(connection, HttpMethod.Post, "api/v1/statuses", body, ct, Guid.NewGuid().ToString("N"));
        return SafeUrl(RequiredString(status, "url"));
    }

    private static bool Ready(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Undefined) return false;
        if (data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException("Mastodon media processing failed: " + error.GetString());
        return data.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString());
    }

    private static HttpRequestMessage Request(Connection connection, HttpMethod method, Uri uri)
    {
        if (uri.GetLeftPart(UriPartial.Authority) != connection.Instance.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Refusing to send Mastodon credentials to another instance.");
        ValidateToken(connection.Token);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        return request;
    }

    private async Task<JsonElement> SendAsync(Connection connection, HttpMethod method, string path,
        HttpContent? content, CancellationToken ct, string? idempotencyKey = null, bool allowPendingMedia = false)
    {
        using var request = Request(connection, method, new Uri(connection.Instance, path));
        request.Content = content;
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, ct);
        // GET media may return 206 with no JSON while processing; only this endpoint treats it as pending.
        if (allowPendingMedia && response.StatusCode == HttpStatusCode.PartialContent) return default;
        return await ReadResponseAsync(response, connection.Token, ct);
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, string token, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = "";
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    detail = " " + error.GetString()?.Replace(token, "[redacted]", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { /* Non-JSON/invalid error responses still include the HTTP status below. */ }
            var advice = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " Reconnect Mastodon under Accounts and check token permissions."
                : response.StatusCode == HttpStatusCode.TooManyRequests ? " Rate limited; retry later." : "";
            throw new HttpRequestException($"Mastodon API returned {(int)response.StatusCode}.{detail}{advice}", null, response.StatusCode);
        }
        try { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidOperationException("Mastodon returned an invalid JSON response.", ex); }
    }

    private static MastodonAccount ReadAccount(JsonElement data)
    {
        MastodonAccount account;
        try
        {
            account = data.Deserialize<MastodonAccount>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Mastodon returned an invalid account.");
        }
        catch (JsonException ex) { throw new InvalidOperationException("Mastodon returned an invalid account.", ex); }
        if (string.IsNullOrWhiteSpace(account.Id))
            throw new InvalidOperationException("Mastodon returned an incomplete stable identity.");
        if (string.IsNullOrWhiteSpace(account.Uri))
            throw new InvalidOperationException("Mastodon account response lacks its actor URI. Mastodon 4.2 or newer is required.");
        SafeUrl(account.Uri);
        SafeUrl(account.Url);
        // URI uses the actor's canonical federation domain even when the connection uses a web-domain alias.
        var acct = MastodonIdentity.NormalizeAcct(account.Acct, new Uri(account.Uri))
            ?? throw new InvalidOperationException("Mastodon returned an invalid acct.");
        return account with { Acct = acct };
    }

    private static string SafeUrl(string? value) =>
        System.Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            ? value! : throw new InvalidOperationException("Mastodon returned an invalid HTTPS URL.");

    private static void ValidateToken(string token)
    {
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidOperationException("Mastodon access token cannot contain whitespace or control characters.");
    }

    private static string RequiredString(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidOperationException($"Mastodon response is missing {key}.");
}
