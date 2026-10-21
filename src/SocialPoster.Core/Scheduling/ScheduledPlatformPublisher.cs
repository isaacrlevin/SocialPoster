using SocialPoster.Core.Services;

namespace SocialPoster.Core.Scheduling;

/// <summary>Reuses existing platform clients, with an attempt-local immutable credential store.</summary>
public sealed class ScheduledPlatformPublisher(IHttpClientFactory http, SecretStore secrets) : IScheduledPlatformPublisher
{
    public async Task<string> PublishAsync(ScheduledPlatform platform, ScheduledCredentials credentials,
        IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var frozen = secrets.Freeze(credentials.Settings);
        // AddHttpClient<T> uses the simple type display name for these non-generic typed clients.
        var clientType = platform.Platform switch
        {
            Platform.X => typeof(XClient),
            Platform.Bluesky => typeof(BlueskyClient),
            Platform.LinkedIn => typeof(LinkedInClient),
            Platform.Mastodon => typeof(MastodonClient),
            Platform.Threads => typeof(ThreadsClient),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
        using var client = http.CreateClient(clientType.Name);
        return platform.Platform switch
        {
            Platform.X => await new XClient(client, frozen).PostAsync(platform.Post, media, ct),
            Platform.Bluesky => await new BlueskyClient(client, frozen).PostAsync(platform.Post, media, ct),
            Platform.LinkedIn => await new LinkedInClient(client, frozen).PostAsync(platform.Post, media, ct),
            Platform.Mastodon => await new MastodonClient(client, frozen).PostAsync(platform.Post, media, ct),
            Platform.Threads => await new ThreadsClient(client, frozen).PostAsync(platform.Post, media, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
    }
}
