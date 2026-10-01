using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SocialPoster.Core;

namespace SocialPoster.Web.Services;

public sealed record LinkedInOrganization(long Id, string Name, string VanityName)
{
    public string Urn => $"urn:li:organization:{Id}";
}

/// <summary>LinkedIn client using a user-provided access token and the versioned Posts API.</summary>
public sealed class LinkedInClient(HttpClient http, SecretStore secrets)
{
    public const string DefaultApiVersion = "202608";

    private sealed record UserInfo([property: JsonPropertyName("sub")] string Sub, [property: JsonPropertyName("name")] string? Name);
    private sealed record OrgLocalized([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("localizedName")] string LocalizedName, [property: JsonPropertyName("vanityName")] string? VanityName);
    private sealed record OrgSearch([property: JsonPropertyName("elements")] List<OrgLocalized> Elements);

    public async Task<bool> IsConfiguredAsync()
    {
        var token = await secrets.GetAsync(SettingKeys.LinkedInAccessToken);
        var personUrn = await secrets.GetAsync(SettingKeys.LinkedInPersonUrn);
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(personUrn);
    }

    public async Task SetAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        var token = accessToken.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Enter a LinkedIn access token.");
        }

        var user = await GetUserInfoAsync(token, ct);
        await SaveIdentityAsync(token, user, ct);
    }

    public async Task<string> VerifyAccessTokenAsync(CancellationToken ct = default)
    {
        var token = await secrets.GetAsync(SettingKeys.LinkedInAccessToken, ct)
            ?? throw new InvalidOperationException("Enter a LinkedIn access token first.");
        var user = await GetUserInfoAsync(token, ct);
        await SaveIdentityAsync(token, user, ct);
        return user.Name ?? user.Sub;
    }

    public async Task ForgetAccessTokenAsync(CancellationToken ct = default)
    {
        await secrets.SetAsync(SettingKeys.LinkedInAccessToken, null, ct);
        await secrets.SetAsync(SettingKeys.LinkedInPersonUrn, null, ct);
        await secrets.SetAsync(SettingKeys.LinkedInName, null, ct);
    }

    /// <summary>
    /// Looks up a company page by the name in its URL (linkedin.com/company/<b>vanity</b>).
    /// Needs Community Management API access; without it LinkedIn returns 403.
    /// </summary>
    public async Task<LinkedInOrganization?> LookupOrganizationAsync(string vanityName, CancellationToken ct = default)
    {
        using var request = await CreateRestRequestAsync(HttpMethod.Get,
            $"https://api.linkedin.com/rest/organizations?q=vanityName&vanityName={Uri.EscapeDataString(vanityName.Trim())}", ct);
        using var response = await http.SendAsync(request, ct);
        await ApiErrors.EnsureSuccessAsync(response, "LinkedIn organization lookup", ct);
        var org = (await response.Content.ReadFromJsonAsync<OrgSearch>(ct))?.Elements.FirstOrDefault();
        return org is null ? null : new LinkedInOrganization(org.Id, org.LocalizedName, org.VanityName ?? vanityName);
    }

    /// <returns>Web URL of the new post.</returns>
    public async Task<string> PostAsync(RenderedPost post, IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var author = await secrets.GetAsync(SettingKeys.LinkedInPersonUrn, ct)
            ?? throw new InvalidOperationException("LinkedIn isn't connected. Connect it in Settings.");

        var body = new JsonObject
        {
            ["author"] = author,
            ["commentary"] = post.PayloadText,
            ["visibility"] = "PUBLIC",
            ["distribution"] = new JsonObject
            {
                ["feedDistribution"] = "MAIN_FEED",
                ["targetEntities"] = new JsonArray(),
                ["thirdPartyDistributionChannels"] = new JsonArray(),
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReshareDisabledByAuthor"] = false,
        };

        if (media.Count > 0)
        {
            body["content"] = await BuildContentAsync(author, media, ct);
        }

        using var request = await CreateRestRequestAsync(HttpMethod.Post, "https://api.linkedin.com/rest/posts", ct);
        request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        await ApiErrors.EnsureSuccessAsync(response, "LinkedIn post", ct);
        var urn = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.FirstOrDefault() : null;
        return urn is null ? "https://www.linkedin.com/feed/" : $"https://www.linkedin.com/feed/update/{urn}/";
    }

    private async Task<JsonObject> BuildContentAsync(string owner, IReadOnlyList<MediaAttachment> media, CancellationToken ct)
    {
        if (media[0].Kind == MediaKind.Video)
        {
            var video = media[0];
            var urn = await UploadVideoAsync(owner, video, ct);
            return new JsonObject { ["media"] = new JsonObject { ["id"] = urn, ["title"] = Path.GetFileNameWithoutExtension(video.FileName) } };
        }

        var images = new List<(string Urn, string? Alt)>();
        foreach (var m in media)
        {
            images.Add((await UploadImageAsync(owner, m, ct), m.AltText?.Trim()));
        }

        JsonObject Image(string urn, string? alt)
        {
            var node = new JsonObject { ["id"] = urn };
            if (!string.IsNullOrWhiteSpace(alt)) node["altText"] = alt;
            return node;
        }

        // A single image/GIF uses `media`; two or more use `multiImage`.
        return images.Count == 1
            ? new JsonObject { ["media"] = Image(images[0].Urn, images[0].Alt) }
            : new JsonObject { ["multiImage"] = new JsonObject { ["images"] = new JsonArray(images.Select(i => (JsonNode)Image(i.Urn, i.Alt)).ToArray()) } };
    }

    private async Task<string> UploadImageAsync(string owner, MediaAttachment media, CancellationToken ct)
    {
        using var init = await CreateRestRequestAsync(HttpMethod.Post, "https://api.linkedin.com/rest/images?action=initializeUpload", ct);
        init.Content = JsonContent.Create(new { initializeUploadRequest = new { owner } });
        using var initResponse = await http.SendAsync(init, ct);
        await ApiErrors.EnsureSuccessAsync(initResponse, $"LinkedIn image upload ({media.FileName})", ct);
        var value = (await initResponse.Content.ReadFromJsonAsync<JsonObject>(ct))?["value"];
        var uploadUrl = value?["uploadUrl"]?.GetValue<string>();
        var urn = value?["image"]?.GetValue<string>();
        if (uploadUrl is null || urn is null) throw new InvalidOperationException("LinkedIn didn't return an image upload URL.");

        await using var file = File.OpenRead(media.Path ?? throw new InvalidOperationException($"{media.FileName} has no file on disk."));
        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new StreamContent(file) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await secrets.GetAsync(SettingKeys.LinkedInAccessToken, ct));
        using var putResponse = await http.SendAsync(put, ct);
        await ApiErrors.EnsureSuccessAsync(putResponse, $"LinkedIn image upload ({media.FileName})", ct);
        return urn;
    }

    /// <summary>Multipart video upload: LinkedIn hands back one pre-signed URL per byte range; the ETags are echoed back to finalize.</summary>
    private async Task<string> UploadVideoAsync(string owner, MediaAttachment media, CancellationToken ct)
    {
        using var init = await CreateRestRequestAsync(HttpMethod.Post, "https://api.linkedin.com/rest/videos?action=initializeUpload", ct);
        init.Content = JsonContent.Create(new { initializeUploadRequest = new { owner, fileSizeBytes = media.Size, uploadCaptions = false, uploadThumbnail = false } });
        using var initResponse = await http.SendAsync(init, ct);
        await ApiErrors.EnsureSuccessAsync(initResponse, $"LinkedIn video upload ({media.FileName})", ct);
        var value = (await initResponse.Content.ReadFromJsonAsync<JsonObject>(ct))?["value"];
        var urn = value?["video"]?.GetValue<string>();
        var uploadToken = value?["uploadToken"]?.GetValue<string>() ?? "";
        if (urn is null || value?["uploadInstructions"] is not JsonArray instructions) throw new InvalidOperationException("LinkedIn didn't return video upload instructions.");

        var etags = new List<string>();
        await using (var file = File.OpenRead(media.Path ?? throw new InvalidOperationException($"{media.FileName} has no file on disk.")))
        {
            foreach (var part in instructions)
            {
                var first = part!["firstByte"]!.GetValue<long>();
                var last = part["lastByte"]!.GetValue<long>();
                var bytes = new byte[last - first + 1];
                file.Seek(first, SeekOrigin.Begin);
                await file.ReadExactlyAsync(bytes, ct);

                using var put = new HttpRequestMessage(HttpMethod.Put, part["uploadUrl"]!.GetValue<string>()) { Content = new ByteArrayContent(bytes) };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var putResponse = await http.SendAsync(put, ct);
                await ApiErrors.EnsureSuccessAsync(putResponse, $"LinkedIn video upload ({media.FileName})", ct);
                etags.Add(putResponse.Headers.ETag?.Tag.Trim('"') ?? throw new InvalidOperationException("LinkedIn didn't return an ETag for a video part."));
            }
        }

        using var finalize = await CreateRestRequestAsync(HttpMethod.Post, "https://api.linkedin.com/rest/videos?action=finalizeUpload", ct);
        finalize.Content = JsonContent.Create(new { finalizeUploadRequest = new { video = urn, uploadToken, uploadedPartIds = etags } });
        using var finalizeResponse = await http.SendAsync(finalize, ct);
        await ApiErrors.EnsureSuccessAsync(finalizeResponse, $"LinkedIn video upload ({media.FileName})", ct);

        // Wait for processing so the post doesn't reference a video that isn't ready.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var statusRequest = await CreateRestRequestAsync(HttpMethod.Get, $"https://api.linkedin.com/rest/videos/{Uri.EscapeDataString(urn)}", ct);
            using var statusResponse = await http.SendAsync(statusRequest, ct);
            await ApiErrors.EnsureSuccessAsync(statusResponse, "LinkedIn video status", ct);
            switch ((await statusResponse.Content.ReadFromJsonAsync<JsonObject>(ct))?["status"]?.GetValue<string>())
            {
                case "AVAILABLE":
                    return urn;
                case "PROCESSING_FAILED":
                    throw new InvalidOperationException($"LinkedIn couldn't process {media.FileName}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        throw new InvalidOperationException($"LinkedIn is still processing {media.FileName}; try again shortly.");
    }

    private async Task<HttpRequestMessage> CreateRestRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await secrets.GetAsync(SettingKeys.LinkedInAccessToken, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("LinkedIn isn't connected. Add your access token in Settings.");
        }

        var version = await secrets.GetAsync(SettingKeys.LinkedInApiVersion, ct);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("LinkedIn-Version", string.IsNullOrWhiteSpace(version) ? DefaultApiVersion : version.Trim());
        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
        return request;
    }

    private async Task<UserInfo> GetUserInfoAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        await ApiErrors.EnsureSuccessAsync(response, "LinkedIn userinfo", ct);
        var user = await response.Content.ReadFromJsonAsync<UserInfo>(ct);
        if (user is null || string.IsNullOrWhiteSpace(user.Sub))
        {
            throw new InvalidOperationException("LinkedIn did not return a member identity. Check that the token includes the openid and profile scopes.");
        }

        return user;
    }

    private async Task SaveIdentityAsync(string token, UserInfo user, CancellationToken ct)
    {
        await secrets.SetAsync(SettingKeys.LinkedInAccessToken, token, ct);
        await secrets.SetAsync(SettingKeys.LinkedInPersonUrn, $"urn:li:person:{user.Sub}", ct);
        await secrets.SetAsync(SettingKeys.LinkedInName, user.Name, ct);
    }
}
