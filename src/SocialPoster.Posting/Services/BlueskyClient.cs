using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SocialPoster.Posting.Services;

public sealed record BskyActor(
    [property: JsonPropertyName("did")] string Did,
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("avatar")] string? Avatar,
    [property: JsonPropertyName("description")] string? Description);

/// <summary>Bluesky / AT Protocol client. Search and follows use the public AppView and need no login.</summary>
public sealed class BlueskyClient(HttpClient http, ICredentialStore secrets)
{
    private const string PublicAppView = "https://public.api.bsky.app/xrpc/";
    private const string DefaultService = "https://bsky.social";
    private const string VideoService = "https://video.bsky.app/xrpc/";

    private sealed record ActorsResponse([property: JsonPropertyName("actors")] List<BskyActor> Actors);
    private sealed record FollowsResponse([property: JsonPropertyName("follows")] List<BskyActor> Follows, [property: JsonPropertyName("cursor")] string? Cursor);
    private sealed record ResolveResponse([property: JsonPropertyName("did")] string Did);
    private sealed record SessionResponse([property: JsonPropertyName("accessJwt")] string AccessJwt, [property: JsonPropertyName("did")] string Did, [property: JsonPropertyName("handle")] string Handle, [property: JsonPropertyName("didDoc")] JsonElement DidDoc);
    private sealed record CreateRecordResponse([property: JsonPropertyName("uri")] string Uri);

    public async Task<bool> IsConfiguredAsync() =>
        !string.IsNullOrWhiteSpace(await secrets.GetAsync(SettingKeys.BlueskyHandle)) &&
        !string.IsNullOrWhiteSpace(await secrets.GetAsync(SettingKeys.BlueskyAppPassword));

    public async Task<IReadOnlyList<BskyActor>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var url = $"{PublicAppView}app.bsky.actor.searchActorsTypeahead?q={Uri.EscapeDataString(query)}&limit={limit}";
        var result = await http.GetFromJsonAsync<ActorsResponse>(url, ct);
        return result?.Actors ?? [];
    }

    public async Task<BskyActor?> GetProfileAsync(string actor, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{PublicAppView}app.bsky.actor.getProfile?actor={Uri.EscapeDataString(actor.TrimStart('@'))}", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BskyActor>(ct) : null;
    }

    public async Task<string?> ResolveHandleAsync(string handle, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{PublicAppView}com.atproto.identity.resolveHandle?handle={Uri.EscapeDataString(handle.TrimStart('@'))}", ct);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<ResolveResponse>(ct))?.Did : null;
    }

    public async IAsyncEnumerable<IReadOnlyList<BskyActor>> GetFollowsAsync(
        string actor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? cursor = null;
        do
        {
            var url = $"{PublicAppView}app.bsky.graph.getFollows?actor={Uri.EscapeDataString(actor.TrimStart('@'))}&limit=100";
            if (cursor is not null)
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            var page = await http.GetFromJsonAsync<FollowsResponse>(url, ct);
            if (page is null || page.Follows.Count == 0)
            {
                yield break;
            }

            yield return page.Follows;
            cursor = page.Cursor;
        }
        while (cursor is not null);
    }

    public async Task<(string Did, string Handle)> VerifyLoginAsync(CancellationToken ct = default)
    {
        var (session, _) = await CreateSessionAsync(ct);
        return (session.Did, session.Handle);
    }

    /// <returns>Web URL of the new post.</returns>
    public async Task<string> PostAsync(RenderedPost post, IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var (session, service) = await CreateSessionAsync(ct);

        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = post.PayloadText,
            ["createdAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["langs"] = new JsonArray("en"),
        };

        if (post.Facets.Count > 0)
        {
            record["facets"] = new JsonArray(post.Facets.Select(ToJson).ToArray<JsonNode?>());
        }

        if (media.Count > 0)
        {
            // A post carries a single embed, so attached media replaces the link card.
            record["embed"] = await BuildMediaEmbedAsync(media, session, service, ct);
        }
        else if (post.LinkCardUrl is { } cardUrl)
        {
            // Best-effort: a failed card fetch shouldn't block publishing the post itself.
            try
            {
                var embed = await BuildExternalEmbedAsync(cardUrl, session, service, ct);
                if (embed is not null)
                {
                    record["embed"] = embed;
                }
            }
            catch
            {
                // Ignore: the post still goes out with a plain link facet.
            }
        }

        var body = new JsonObject
        {
            ["repo"] = session.Did,
            ["collection"] = "app.bsky.feed.post",
            ["record"] = record,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{service}/xrpc/com.atproto.repo.createRecord")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);

        using var response = await http.SendAsync(request, ct);
        await ApiErrors.EnsureSuccessAsync(response, "Bluesky", ct);
        var created = await response.Content.ReadFromJsonAsync<CreateRecordResponse>(ct);
        var rkey = created!.Uri.Split('/').Last();
        return $"https://bsky.app/profile/{session.Handle}/post/{rkey}";
    }

    private async Task<JsonObject> BuildMediaEmbedAsync(IReadOnlyList<MediaAttachment> media, SessionResponse session, string service, CancellationToken ct)
    {
        if (media[0].Kind == MediaKind.Video)
        {
            var video = media[0];
            var blob = await UploadVideoAsync(video, session, service, ct);
            var embed = new JsonObject { ["$type"] = "app.bsky.embed.video", ["video"] = blob };
            if (!string.IsNullOrWhiteSpace(video.AltText)) embed["alt"] = video.AltText.Trim();
            return embed;
        }

        var images = new JsonArray();
        foreach (var m in media)
        {
            images.Add(new JsonObject { ["alt"] = m.AltText?.Trim() ?? "", ["image"] = await UploadBlobAsync(m, session, service, ct) });
        }

        return new JsonObject { ["$type"] = "app.bsky.embed.images", ["images"] = images };
    }

    private async Task<JsonNode> UploadBlobAsync(MediaAttachment media, SessionResponse session, string service, CancellationToken ct)
    {
        await using var file = File.OpenRead(media.Path ?? throw new InvalidOperationException($"{media.FileName} has no file on disk."));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{service}/xrpc/com.atproto.repo.uploadBlob") { Content = new StreamContent(file) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(media.ContentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);

        using var response = await http.SendAsync(request, ct);
        await ApiErrors.EnsureSuccessAsync(response, $"Bluesky upload ({media.FileName})", ct);
        var uploaded = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return uploaded?["blob"]?.DeepClone() ?? throw new InvalidOperationException("Bluesky didn't return an uploaded blob.");
    }

    /// <summary>Videos go to Bluesky's video service (which transcodes them) using a service-auth token for the user's PDS.</summary>
    private async Task<JsonNode> UploadVideoAsync(MediaAttachment video, SessionResponse session, string service, CancellationToken ct)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var authUrl = $"{service}/xrpc/com.atproto.server.getServiceAuth?aud={Uri.EscapeDataString(PdsDid(session, service))}" +
                      $"&lxm=com.atproto.repo.uploadBlob&exp={exp}";
        using var authRequest = new HttpRequestMessage(HttpMethod.Get, authUrl);
        authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
        using var authResponse = await http.SendAsync(authRequest, ct);
        await ApiErrors.EnsureSuccessAsync(authResponse, "Bluesky video authorization", ct);
        var token = (await authResponse.Content.ReadFromJsonAsync<JsonObject>(ct))?["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Bluesky didn't return a video upload token.");

        await using var file = File.OpenRead(video.Path ?? throw new InvalidOperationException($"{video.FileName} has no file on disk."));
        using var upload = new HttpRequestMessage(HttpMethod.Post,
            $"{VideoService}app.bsky.video.uploadVideo?did={Uri.EscapeDataString(session.Did)}&name={Uri.EscapeDataString(video.FileName)}")
        {
            Content = new StreamContent(file),
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(video.ContentType);
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var uploadResponse = await http.SendAsync(upload, ct);
        // 409 means this exact video was already uploaded; the body still carries the existing job.
        if (uploadResponse.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            await ApiErrors.EnsureSuccessAsync(uploadResponse, "Bluesky video upload", ct);
        }

        var job = JobStatus(await uploadResponse.Content.ReadFromJsonAsync<JsonObject>(ct));
        var jobId = job?["jobId"]?.GetValue<string>() ?? throw new InvalidOperationException("Bluesky didn't start a video processing job.");

        // Poll until the video service has processed the file (it can take a minute or two).
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (job?["blob"] is { } blob) return blob.DeepClone();

            if (job?["state"]?.GetValue<string>() == "JOB_STATE_FAILED")
            {
                var reason = job["error"]?.GetValue<string>() ?? job["message"]?.GetValue<string>() ?? "unknown error";
                throw new InvalidOperationException($"Bluesky couldn't process the video: {reason}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var statusResponse = await http.GetAsync($"{VideoService}app.bsky.video.getJobStatus?jobId={Uri.EscapeDataString(jobId)}", ct);
            await ApiErrors.EnsureSuccessAsync(statusResponse, "Bluesky video status", ct);
            job = JobStatus(await statusResponse.Content.ReadFromJsonAsync<JsonObject>(ct));
        }

        throw new InvalidOperationException("Bluesky is still processing the video; try again shortly.");
    }

    private static JsonObject? JobStatus(JsonObject? body) => body?["jobStatus"] as JsonObject ?? body;

    /// <summary>The did:web of the user's PDS (from their DID document), which the video service needs as the token audience.</summary>
    private static string PdsDid(SessionResponse session, string service)
    {
        var endpoint = service;
        if (session.DidDoc.ValueKind == JsonValueKind.Object && session.DidDoc.TryGetProperty("service", out var services) && services.ValueKind == JsonValueKind.Array)
        {
            foreach (var svc in services.EnumerateArray())
            {
                if (svc.TryGetProperty("id", out var id) && id.GetString() == "#atproto_pds" &&
                    svc.TryGetProperty("serviceEndpoint", out var ep) && ep.GetString() is { Length: > 0 } e)
                {
                    endpoint = e;
                    break;
                }
            }
        }

        return $"did:web:{new Uri(endpoint).Host}";
    }

    /// <summary>Builds an `app.bsky.embed.external` record (the "link card") by scraping the target page's Open
    /// Graph/meta tags for a title, description and thumbnail, uploading the thumbnail as a blob.</summary>
    private async Task<JsonObject?> BuildExternalEmbedAsync(string url, SessionResponse session, string service, CancellationToken ct)
    {
        var card = await LinkCardFetcher.FetchAsync(http, url, ct);
        if (card is null)
        {
            return null;
        }

        var external = new JsonObject
        {
            ["uri"] = url,
            ["title"] = card.Title ?? "",
            ["description"] = card.Description ?? "",
        };

        if (card.ThumbBytes is { Length: > 0 })
        {
            try
            {
                using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{service}/xrpc/com.atproto.repo.uploadBlob")
                {
                    Content = new ByteArrayContent(card.ThumbBytes),
                };
                uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(card.ThumbMimeType ?? "image/jpeg");
                uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);

                using var uploadResponse = await http.SendAsync(uploadRequest, ct);
                if (uploadResponse.IsSuccessStatusCode)
                {
                    var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonObject>(ct);
                    if (uploaded?["blob"] is JsonNode blob)
                    {
                        external["thumb"] = blob.DeepClone();
                    }
                }
            }
            catch
            {
                // A missing thumbnail still leaves a usable card with title/description.
            }
        }

        return new JsonObject
        {
            ["$type"] = "app.bsky.embed.external",
            ["external"] = external,
        };
    }

    private static JsonObject ToJson(BlueskyFacet f)
    {
        var feature = f.Kind switch
        {
            BlueskyFacetKind.Mention => new JsonObject { ["$type"] = "app.bsky.richtext.facet#mention", ["did"] = f.Value },
            BlueskyFacetKind.Link => new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = f.Value },
            _ => new JsonObject { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = f.Value },
        };

        return new JsonObject
        {
            ["index"] = new JsonObject { ["byteStart"] = f.ByteStart, ["byteEnd"] = f.ByteEnd },
            ["features"] = new JsonArray(feature),
        };
    }

    private async Task<(SessionResponse Session, string Service)> CreateSessionAsync(CancellationToken ct)
    {
        var handle = await secrets.GetAsync(SettingKeys.BlueskyHandle, ct);
        var password = await secrets.GetAsync(SettingKeys.BlueskyAppPassword, ct);
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Bluesky isn't connected. Add your handle and an app password in Settings.");
        }

        var service = (await secrets.GetAsync(SettingKeys.BlueskyService, ct))?.TrimEnd('/') ?? DefaultService;
        using var response = await http.PostAsJsonAsync($"{service}/xrpc/com.atproto.server.createSession",
            new { identifier = handle.TrimStart('@'), password }, ct);
        await ApiErrors.EnsureSuccessAsync(response, "Bluesky login", ct);
        return ((await response.Content.ReadFromJsonAsync<SessionResponse>(ct))!, service);
    }
}

internal static class ApiErrors
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 600)
            {
                body = body[..600] + "…";
            }

            throw new HttpRequestException($"{what} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}", null, response.StatusCode);
        }
    }
}
