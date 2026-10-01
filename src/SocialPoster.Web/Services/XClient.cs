using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SocialPoster.Core;

namespace SocialPoster.Web.Services;

public sealed record XUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl,
    [property: JsonPropertyName("description")] string? Description);

/// <summary>X API v2 client using OAuth 1.0a user context (keys from your X developer app).</summary>
public sealed class XClient(HttpClient http, SecretStore secrets)
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
    private sealed record TweetData([property: JsonPropertyName("id")] string Id);
    private sealed record TweetResponse([property: JsonPropertyName("data")] TweetData Data);

    public async Task<bool> IsConfiguredAsync() => await GetCredentialsAsync() is not null;

    /// <summary>Looks up an exact username. Each call counts against your X API quota.</summary>
    public async Task<XUser?> LookupUsernameAsync(string username, CancellationToken ct = default)
    {
        username = username.Trim().TrimStart('@');
        var url = $"{Api}/users/by/username/{Uri.EscapeDataString(username)}";
        var query = new Dictionary<string, string> { ["user.fields"] = "profile_image_url,description" };
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
        await ApiErrors.EnsureSuccessAsync(response, "X", ct);
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
