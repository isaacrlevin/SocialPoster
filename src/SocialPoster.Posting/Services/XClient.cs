using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SocialPoster.Posting.Services;

public sealed record XUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("entities")] XUserEntities? Entities = null)
{
    /// <summary>Profile text including expanded links (X commonly returns shortened t.co URLs).</summary>
    public string ProfileText => string.Join("\n", new[] { Description, Url }
        .Concat(Entities?.Url?.Urls?.Select(u => u.ExpandedUrl) ?? [])
        .Concat(Entities?.Description?.Urls?.Select(u => u.ExpandedUrl) ?? [])
        .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A URL expanded by X in a user's profile.</summary>
/// <param name="ExpandedUrl">The original URL behind a shortened link.</param>
public sealed record XProfileUrl([property: JsonPropertyName("expanded_url")] string? ExpandedUrl);
/// <summary>Links included in a profile field.</summary>
/// <param name="Urls">Expanded links returned by X.</param>
public sealed record XProfileUrls([property: JsonPropertyName("urls")] List<XProfileUrl>? Urls);
/// <summary>Link entities included in the profile website and description.</summary>
/// <param name="Url">Website links.</param>
/// <param name="Description">Description links.</param>
public sealed record XUserEntities(
    [property: JsonPropertyName("url")] XProfileUrls? Url,
    [property: JsonPropertyName("description")] XProfileUrls? Description);

/// <summary>X API v2 client using OAuth 1.0a user context (keys from your X developer app).</summary>
public sealed class XClient(HttpClient http, ICredentialStore secrets)
{
    private const string Api = "https://api.x.com/2";
    private const int ChunkSize = 4 * 1024 * 1024;

    private sealed record MediaError([property: JsonPropertyName("message")] string? Message);
    private sealed record ProcessingInfo(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("check_after_secs")] int? CheckAfterSecs,
        [property: JsonPropertyName("error")] MediaError? Error);
    private sealed record MediaData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("processing_info")] ProcessingInfo? ProcessingInfo);
    private sealed record MediaResponse([property: JsonPropertyName("data")] MediaData? Data);

    private sealed record UserResponse([property: JsonPropertyName("data")] XUser? Data);
    private sealed record FollowingMeta([property: JsonPropertyName("next_token")] string? NextToken);
    private sealed record FollowingResponse(
        [property: JsonPropertyName("data")] List<XUser>? Data,
        [property: JsonPropertyName("meta")] FollowingMeta? Meta,
        [property: JsonPropertyName("errors")] System.Text.Json.JsonElement? Errors);
    private sealed record TweetData([property: JsonPropertyName("id")] string Id);
    private sealed record TweetResponse([property: JsonPropertyName("data")] TweetData Data);

    public async Task<bool> IsConfiguredAsync() => await GetCredentialsAsync() is not null;

    /// <summary>Following imports require enabled X, all API credentials and API (not browser) mode.</summary>
    /// <param name="platforms">Enabled platforms; null allows all platforms.</param>
    /// <returns>Whether the local configuration permits an X API following import.</returns>
    public async Task<bool> CanImportFollowsAsync(IReadOnlyCollection<Platform>? platforms = null) =>
        (platforms is null || platforms.Contains(Platform.X)) &&
        await secrets.GetAsync(SettingKeys.XPostMethod) != "intent" &&
        await IsConfiguredAsync();

    /// <summary>Resolves the authenticated account and reads every page of accounts it follows.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Pages of followed users, with expanded profile links.</returns>
    public async IAsyncEnumerable<IReadOnlyList<XUser>> GetFollowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!await CanImportFollowsAsync())
            throw new InvalidOperationException("X following imports require API credentials and API posting mode in Accounts.");
        var me = await GetMeAsync(ct);
        if (string.IsNullOrWhiteSpace(me.Id))
            throw new InvalidOperationException("X returned no authenticated account ID.");
        string? token = null;
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var query = new Dictionary<string, string>
            {
                ["max_results"] = "1000",
                ["user.fields"] = "profile_image_url,description,url,entities",
            };
            if (token is not null) query["pagination_token"] = token;
            using var response = await SendAsync(HttpMethod.Get, $"{Api}/users/{Uri.EscapeDataString(me.Id)}/following", query, null, ct);
            await EnsureReadSuccessAsync(response, "X following import", ct);
            var page = await response.Content.ReadFromJsonAsync<FollowingResponse>(ct)
                ?? throw new InvalidOperationException("X returned no following response.");
            if (page.Errors is { ValueKind: System.Text.Json.JsonValueKind.Array } errors && errors.GetArrayLength() > 0)
                throw new InvalidOperationException("X returned errors in the following response. Check your API plan and permissions, then retry; contacts already imported are saved.");
            if (page.Data is null && page.Meta is null)
                throw new InvalidOperationException("X returned an unexpected following response without users or pagination metadata.");
            if (page.Data is { Count: > 0 }) yield return page.Data;
            token = string.IsNullOrWhiteSpace(page.Meta?.NextToken) ? null : page.Meta.NextToken;
            if (token is not null && !tokens.Add(token))
                throw new InvalidOperationException("X repeated a following pagination token. Sync stopped to avoid an endless import.");
        }
        while (token is not null);
    }

    /// <summary>Looks up an exact username. Each call counts against your X API quota.</summary>
    public async Task<XUser?> LookupUsernameAsync(string username, CancellationToken ct = default)
    {
        username = username.Trim().TrimStart('@');
        var url = $"{Api}/users/by/username/{Uri.EscapeDataString(username)}";
        var query = new Dictionary<string, string> { ["user.fields"] = "profile_image_url,description,url,entities" };
        using var response = await SendAsync(HttpMethod.Get, url, query, null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await ApiErrors.EnsureSuccessAsync(response, "X user lookup", ct);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(ct))?.Data;
    }

    public async Task<XUser> GetMeAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Api}/users/me", [], null, ct);
        await EnsureReadSuccessAsync(response, "X account lookup", ct);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(ct))?.Data
            ?? throw new InvalidOperationException("X returned no user.");
    }

    /// <returns>Web URL of the new post.</returns>
    public async Task<string> PostAsync(RenderedPost post, IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var mediaIds = new List<string>();
        foreach (var m in media)
        {
            mediaIds.Add(await UploadMediaAsync(m, ct));
        }

        object body = mediaIds.Count == 0
            ? new { text = post.PayloadText }
            : new { text = post.PayloadText, media = new { media_ids = mediaIds } };

        using var response = await SendAsync(HttpMethod.Post, $"{Api}/tweets", [], JsonContent.Create(body), ct);
        await ApiErrors.EnsureSuccessAsync(response, "X post", ct);
        var created = await response.Content.ReadFromJsonAsync<TweetResponse>(ct);
        return $"https://x.com/i/web/status/{created!.Data.Id}";
    }

    /// <summary>Chunked v2 upload (initialize, append, finalize, wait for processing); works for images, GIFs and video.</summary>
    private async Task<string> UploadMediaAsync(MediaAttachment media, CancellationToken ct)
    {
        var path = media.Path ?? throw new InvalidOperationException($"{media.FileName} has no file on disk.");
        var category = media.Kind switch { MediaKind.Gif => "tweet_gif", MediaKind.Video => "tweet_video", _ => "tweet_image" };

        using var init = await SendAsync(HttpMethod.Post, $"{Api}/media/upload/initialize", [],
            JsonContent.Create(new { media_type = media.ContentType, total_bytes = media.Size, media_category = category }), ct);
        await ApiErrors.EnsureSuccessAsync(init, $"X media upload ({media.FileName})", ct);
        var id = (await init.Content.ReadFromJsonAsync<MediaResponse>(ct))?.Data?.Id
            ?? throw new InvalidOperationException("X didn't return a media id.");

        await using (var file = File.OpenRead(path))
        {
            var buffer = new byte[ChunkSize];
            for (var segment = 0; ; segment++)
            {
                var read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (read == 0) break;

                using var form = new MultipartFormDataContent
                {
                    { new ByteArrayContent(buffer, 0, read), "media", "blob" },
                    { new StringContent(segment.ToString()), "segment_index" },
                };
                using var append = await SendAsync(HttpMethod.Post, $"{Api}/media/upload/{id}/append", [], form, ct);
                await ApiErrors.EnsureSuccessAsync(append, $"X media upload ({media.FileName})", ct);
            }
        }

        using var finalize = await SendAsync(HttpMethod.Post, $"{Api}/media/upload/{id}/finalize", [], null, ct);
        await ApiErrors.EnsureSuccessAsync(finalize, $"X media upload ({media.FileName})", ct);
        var processing = (await finalize.Content.ReadFromJsonAsync<MediaResponse>(ct))?.Data?.ProcessingInfo;

        // Video/GIF are transcoded asynchronously; the tweet can't reference them until that finishes.
        for (var attempt = 0; processing is { State: "pending" or "in_progress" }; attempt++)
        {
            if (attempt >= 60) throw new InvalidOperationException($"X is still processing {media.FileName}; try again shortly.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(processing.CheckAfterSecs ?? 2, 1, 15)), ct);

            using var status = await SendAsync(HttpMethod.Get, $"{Api}/media/upload",
                new Dictionary<string, string> { ["command"] = "STATUS", ["media_id"] = id }, null, ct);
            await ApiErrors.EnsureSuccessAsync(status, $"X media status ({media.FileName})", ct);
            processing = (await status.Content.ReadFromJsonAsync<MediaResponse>(ct))?.Data?.ProcessingInfo;
        }

        if (processing is { State: "failed" })
        {
            throw new InvalidOperationException($"X couldn't process {media.FileName}: {processing.Error?.Message ?? "unknown error"}");
        }

        if (!string.IsNullOrWhiteSpace(media.AltText) && media.Kind != MediaKind.Video)
        {
            // Alt text is a nicety; don't lose the post over it.
            using var alt = await SendAsync(HttpMethod.Post, $"{Api}/media/metadata", [],
                JsonContent.Create(new { id, metadata = new { alt_text = new { text = media.AltText.Trim() } } }), ct);
        }

        return id;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Dictionary<string, string> query, HttpContent? content, CancellationToken ct)
    {
        var creds = await GetCredentialsAsync()
            ?? throw new InvalidOperationException("X isn't connected. Add your API keys and access tokens in Settings.");

        var fullUrl = query.Count == 0
            ? url
            : url + "?" + string.Join("&", query.Select(kv => $"{OAuth1.Encode(kv.Key)}={OAuth1.Encode(kv.Value)}"));

        using var request = new HttpRequestMessage(method, fullUrl) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1.CreateAuthorizationHeader(method.Method, url, query, creds));
        return await http.SendAsync(request, ct);
    }

    private static async Task EnsureReadSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"{operation} rate limit reached (429). Wait for your X API quota to reset, then sync again; contacts already imported are saved.", null, response.StatusCode);
        await ApiErrors.EnsureSuccessAsync(response, operation, ct);
    }

    private async Task<OAuth1.Credentials?> GetCredentialsAsync()
    {
        var s = await secrets.GetManyAsync(SettingKeys.XConsumerKey, SettingKeys.XConsumerSecret, SettingKeys.XAccessToken, SettingKeys.XAccessTokenSecret);
        if (s.Values.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return new OAuth1.Credentials(s[SettingKeys.XConsumerKey]!, s[SettingKeys.XConsumerSecret]!, s[SettingKeys.XAccessToken]!, s[SettingKeys.XAccessTokenSecret]!);
    }
}
