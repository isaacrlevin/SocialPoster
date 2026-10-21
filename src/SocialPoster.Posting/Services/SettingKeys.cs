namespace SocialPoster.Posting.Services;

/// <summary>The credential keys the platform clients read from an <see cref="ICredentialStore"/>.</summary>
public static class SettingKeys
{
    public const string MastodonInstance = "mastodon.instance";
    public const string MastodonAccessToken = "mastodon.accessToken";

    public const string ThreadsUserId = "threads.userId";
    public const string ThreadsAccessToken = "threads.accessToken";

    public const string BlueskyHandle = "bluesky.handle";
    public const string BlueskyAppPassword = "bluesky.appPassword";
    public const string BlueskyService = "bluesky.service";

    public const string XConsumerKey = "x.consumerKey";
    public const string XConsumerSecret = "x.consumerSecret";
    public const string XAccessToken = "x.accessToken";
    public const string XAccessTokenSecret = "x.accessTokenSecret";

    /// <summary>"intent" posts via a browser intent link instead of the paid X API. Anything else (or unset) uses the API.</summary>
    public const string XPostMethod = "x.postMethod";

    public const string LinkedInApiVersion = "linkedin.apiVersion";
    public const string LinkedInAccessToken = "linkedin.accessToken";
    public const string LinkedInPersonUrn = "linkedin.personUrn";
    public const string LinkedInName = "linkedin.name";
}
