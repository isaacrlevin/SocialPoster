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

        string id;
        try
        {
            id = await CreateAndPublishAsync(connection.Token, userId, post.PayloadText, post.LinkCardUrl, ct);
        }
        catch (ThreadsApiException ex) when (ex.ErrorSubcode == InvalidLinkAttachmentSubcode && !string.IsNullOrWhiteSpace(post.LinkCardUrl))
        {
            // Threads couldn't build a card from the link (its crawler failed to fetch the page, or the page has no
            // usable preview). Post the text without the card instead of failing; Threads previews the first link.
            id = await CreateAndPublishAsync(connection.Token, userId, post.PayloadText, null, ct);
        }

        var detail = await SendAsync(connection.Token, HttpMethod.Get, $"{Uri.EscapeDataString(id)}?fields=permalink", null, ct);
        return SafeUrl(RequiredString(detail, "permalink"));
    }

    /// <summary>Threads' error_subcode for "Invalid Link Attachment", returned when the link card can't be built.</summary>
    public const int InvalidLinkAttachmentSubcode = 4279047;

    private async Task<string> CreateAndPublishAsync(string token, string userId, string text, string? linkCardUrl, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["media_type"] = "TEXT",
            ["text"] = text,
        };
        // link_attachment is only valid on text posts, which are the only kind this client creates.
        if (!string.IsNullOrWhiteSpace(linkCardUrl)) fields["link_attachment"] = linkCardUrl;
        using var create = new FormUrlEncodedContent(fields);
        var container = await SendAsync(token, HttpMethod.Post, $"{userId}/threads", create, ct);

        using var publish = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = RequiredString(container, "id"),
        });
        var published = await SendAsync(token, HttpMethod.Post, $"{userId}/threads_publish", publish, ct);
        return RequiredString(published, "id");
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
            int? subcode = null;
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    // error_user_msg is the human-readable reason; message is often just "Fatal".
                    var reason = error.TryGetProperty("error_user_msg", out var userMessage) && userMessage.ValueKind == JsonValueKind.String
                        ? userMessage.GetString()
                        : error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(reason)) detail = " " + reason.Replace(token, "[redacted]", StringComparison.Ordinal);
                    if (error.TryGetProperty("error_subcode", out var sub) && sub.ValueKind == JsonValueKind.Number && sub.TryGetInt32(out var value)) subcode = value;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { /* Non-JSON/invalid error responses still include the HTTP status below. */ }
            var advice = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                ? " Check that the token is valid and has the threads_basic and threads_content_publish permissions."
                : response.StatusCode == HttpStatusCode.TooManyRequests ? " Rate limited; retry later." : "";
            if (subcode == InvalidLinkAttachmentSubcode) advice = "";
            throw new ThreadsApiException($"Threads API returned {(int)response.StatusCode}.{detail}{advice}", response.StatusCode, subcode);
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

/// <summary>A failed Threads Graph API call, carrying Threads' error_subcode when it sent one.</summary>
public sealed class ThreadsApiException(string message, HttpStatusCode statusCode, int? errorSubcode)
    : HttpRequestException(message, null, statusCode)
{
    public int? ErrorSubcode { get; } = errorSubcode;
}
