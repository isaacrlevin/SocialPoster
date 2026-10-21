namespace SocialPoster.Posting.Services;

public sealed record PublishResult(Platform Platform, bool Success, string? Url, string? Error, bool OpensInBrowser = false);

public sealed record PlatformStatus(bool X, bool Bluesky, bool LinkedIn, bool Mastodon = false, bool Threads = false)
{
    public bool IsConnected(Platform p) => p switch
    {
        Platform.X => X,
        Platform.Bluesky => Bluesky,
        Platform.LinkedIn => LinkedIn,
        Platform.Mastodon => Mastodon,
        Platform.Threads => Threads,
        _ => false,
    };
}

/// <summary>
/// Posts to every selected platform at once. Each platform is rendered, validated (length and media rules) and
/// published independently, so one failing platform never stops the others; failures come back as results, not exceptions.
/// </summary>
public sealed class SocialPublisher(
    BlueskyClient bluesky,
    XClient x,
    LinkedInClient linkedIn,
    MastodonClient mastodon,
    ThreadsClient threads,
    ICredentialStore credentials)
{
    /// <summary>Reports which of <paramref name="platforms"/> (all when null) have credentials configured.</summary>
    public async Task<PlatformStatus> GetStatusAsync(IReadOnlyCollection<Platform>? platforms = null)
    {
        bool Selected(Platform p) => platforms is null || platforms.Contains(p);
        return new(
            Selected(Platform.X) && (await IsXIntentEnabledAsync() || await x.IsConfiguredAsync()),
            Selected(Platform.Bluesky) && await bluesky.IsConfiguredAsync(),
            Selected(Platform.LinkedIn) && await linkedIn.IsConfiguredAsync(),
            Selected(Platform.Mastodon) && await mastodon.IsConfiguredAsync(),
            Selected(Platform.Threads) && await threads.IsConfiguredAsync());
    }

    /// <summary>True when X posts should go through a browser "intent" link instead of the paid X API.</summary>
    public async Task<bool> IsXIntentEnabledAsync() => await credentials.GetAsync(SettingKeys.XPostMethod) == "intent";

    /// <summary>Posts the same text to each platform, with no contact mentions.</summary>
    public Task<IReadOnlyList<PublishResult>> PublishTextAsync(string text, IEnumerable<Platform> platforms,
        IReadOnlyList<MediaAttachment>? media = null, CancellationToken ct = default) =>
        PublishAsync(platforms.Distinct().ToDictionary(p => p, _ => text), null, media, ct);

    /// <summary>Renders each platform's template (resolving <c>{@key}</c> mentions through <paramref name="resolve"/>) and publishes it.</summary>
    /// <param name="templates">Template to publish for each selected platform.</param>
    /// <param name="resolve">Looks up mention targets by key; null leaves every mention unresolved.</param>
    public Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyDictionary<Platform, string> templates,
        ContactResolver? resolve = null, IReadOnlyList<MediaAttachment>? media = null, CancellationToken ct = default)
    {
        resolve ??= _ => null;
        return PublishAsync(templates.Select(kv => PostRenderer.Render(kv.Key, kv.Value, resolve)).ToList(), media, ct);
    }

    /// <summary>Publishes already-rendered posts, one per platform, concurrently.</summary>
    public async Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<RenderedPost> posts,
        IReadOnlyList<MediaAttachment>? media = null, CancellationToken ct = default)
    {
        media ??= [];
        var xIntent = posts.Any(p => p.Platform == Platform.X) && await IsXIntentEnabledAsync();

        var tasks = posts.Select(async rendered =>
        {
            var platform = rendered.Platform;
            if (rendered.IsEmpty) return new PublishResult(platform, false, null, "Post is empty.");
            if (rendered.IsOverLimit) return new PublishResult(platform, false, null, $"Post is {rendered.Length}/{rendered.Limit} characters.");

            var mediaErrors = MediaRules.Validate(platform, media);
            if (mediaErrors.Count > 0) return new PublishResult(platform, false, null, string.Join(" ", mediaErrors));

            if (platform == Platform.X && xIntent)
            {
                // No API call at all: hand the user a pre-filled composer and let them click Post themselves.
                // (Intent links can't carry files, so any media has to be attached by hand there.)
                return new PublishResult(Platform.X, true, XIntent.BuildUrl(rendered.PayloadText), null, OpensInBrowser: true);
            }

            try
            {
                var url = platform switch
                {
                    Platform.X => await x.PostAsync(rendered, media, ct),
                    Platform.Bluesky => await bluesky.PostAsync(rendered, media, ct),
                    Platform.LinkedIn => await linkedIn.PostAsync(rendered, media, ct),
                    Platform.Mastodon => await mastodon.PostAsync(rendered, media, ct),
                    Platform.Threads => await threads.PostAsync(rendered, media, ct),
                    _ => throw new ArgumentOutOfRangeException(nameof(posts)),
                };
                return new PublishResult(platform, true, url, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
            {
                return new PublishResult(platform, false, null, ex.Message);
            }
        });

        return await Task.WhenAll(tasks);
    }
}
