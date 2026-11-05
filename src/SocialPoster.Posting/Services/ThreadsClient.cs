using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SocialPoster.Posting.Services;

/// <summary>The Threads profile that owns the access token.</summary>
public sealed record ThreadsAccount(string Id, string Username);

/// <summary>Threads Graph API client. Authentication belongs to each request and never to shared default headers.</summary>
public sealed class ThreadsClient(HttpClient http, ICredentialStore secrets)
{
    public static readonly Uri ApiBase = new("https://graph.threads.net/v1.0/");

    private sealed record Connection(string UserId, string Token);

    public async Task<bool> IsConfiguredAsync()
    {
        var settings = await secrets.GetSnapshotAsync([SettingKeys.ThreadsUserId, SettingKeys.ThreadsAccessToken]);
        return !string.IsNullOrWhiteSpace(settings[SettingKeys.ThreadsUserId]) &&
            !string.IsNullOrWhiteSpace(settings[SettingKeys.ThreadsAccessToken]);
    }

    /// <summary>Verifies the token, then saves it together with the user id Threads reports for it.</summary>
    public async Task<ThreadsAccount> ConfigureAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            token = await secrets.GetAsync(SettingKeys.ThreadsAccessToken, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("A Threads access token is required.");
        token = token.Trim();
        var account = await VerifyAsync(token, ct);
        await secrets.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.ThreadsAccessToken] = token,
            [SettingKeys.ThreadsUserId] = account.Id,
        }, ct);
        return account;
    }

    public async Task<ThreadsAccount> VerifyCredentialsAsync(CancellationToken ct = default)
    {
        var token = await secrets.GetAsync(SettingKeys.ThreadsAccessToken, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Connect Threads under Accounts first.");
        return await VerifyAsync(token.Trim(), ct);
    }

    private async Task<ThreadsAccount> VerifyAsync(string token, CancellationToken ct)
    {
        var data = await SendAsync(token, HttpMethod.Get, "me?fields=id,username", null, ct);
        return new(RequiredString(data, "id"), RequiredString(data, "username"));
    }

    private async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        var settings = await secrets.GetSnapshotAsync([SettingKeys.ThreadsUserId, SettingKeys.ThreadsAccessToken], ct);
        var userId = settings[SettingKeys.ThreadsUserId];
        var token = settings[SettingKeys.ThreadsAccessToken];
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connect Threads under Accounts first.");
        return new(userId, token);
    }

    /// <summary>
    /// Creates a text container, publishes it, then looks up the permalink of the new thread. The post's last link
    /// (<see cref="RenderedPost.LinkCardUrl"/>) is attached as a link-preview card, so Threads shows that page's image
    /// and title like the Bluesky card; without one, Threads would preview the first link instead.
    /// </summary>
    public async Task<string> PostAsync(RenderedPost post, IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        if (post.IsEmpty || post.IsOverLimit) throw new InvalidOperationException("Threads post is empty or exceeds the 500-character limit.");
        var errors = MediaRules.Validate(Platform.Threads, media);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        var connection = await ConnectAsync(ct);
        var userId = Uri.EscapeDataString(connection.UserId);

        var fields = new Dictionary<string, string>
        {
            ["media_type"] = "TEXT",
            ["text"] = post.PayloadText,
        };
        // link_attachment is only valid on text posts, which are the only kind this client creates.
        if (!string.IsNullOrWhiteSpace(post.LinkCardUrl)) fields["link_attachment"] = post.LinkCardUrl;
        using var create = new FormUrlEncodedContent(fields);
        var container = await SendAsync(connection.Token, HttpMethod.Post, $"{userId}/threads", create, ct);

        using var publish = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = RequiredString(container, "id"),
        });
        var published = await SendAsync(connection.Token, HttpMethod.Post, $"{userId}/threads_publish", publish, ct);
        var id = RequiredString(published, "id");

        var detail = await SendAsync(connection.Token, HttpMethod.Get, $"{Uri.EscapeDataString(id)}?fields=permalink", null, ct);
        return SafeUrl(RequiredString(detail, "permalink"));
    }

    private async Task<JsonElement> SendAsync(string token, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidOperationException("Threads access token cannot contain whitespace or control characters.");
        using var request = new HttpRequestMessage(method, new Uri(ApiBase, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = "";
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    detail = " " + message.GetString()?.Replace(token, "[redacted]", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { /* Non-JSON/invalid error responses still include the HTTP status below. */ }
            var advice = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                ? " Check that the token is valid and has the threads_basic and threads_content_publish permissions."
                : response.StatusCode == HttpStatusCode.TooManyRequests ? " Rate limited; retry later." : "";
            throw new HttpRequestException($"Threads API returned {(int)response.StatusCode}.{detail}{advice}", null, response.StatusCode);
        }
        try { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidOperationException("Threads returned an invalid JSON response.", ex); }
    }

    private static string SafeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            ? value : throw new InvalidOperationException("Threads returned an invalid HTTPS URL.");

    private static string RequiredString(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidOperationException($"Threads response is missing {key}.");
}
