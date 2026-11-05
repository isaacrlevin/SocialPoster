namespace SocialPoster.Posting;

public enum Platform
{
    X,
    Bluesky,
    LinkedIn,
    Mastodon,
    Threads,
}

public enum ContactSource
{
    Manual,
    BlueskySearch,
    BlueskyFollows,
    XLookup,
    AgentSearch,
    XFollows,
    MastodonSearch,
    MastodonFollows,
}

/// <summary>A person or organization, plus their identity on each platform.</summary>
public class Contact
{
    public int Id { get; set; }

    /// <summary>Stable key used in templates, e.g. <c>{@satya}</c>.</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }

    public string? BlueskyHandle { get; set; }
    public string? BlueskyDid { get; set; }

    /// <summary>Fully qualified acct, without the leading @ (user@server).</summary>
    public string? MastodonAcct { get; set; }
    /// <summary>Canonical ActivityPub actor URI; numeric account IDs are instance-local.</summary>
    public string? MastodonUri { get; set; }
    public string? MastodonProfileUrl { get; set; }

    /// <summary>Threads username, without the leading @.</summary>
    public string? ThreadsUsername { get; set; }

    public string? XUsername { get; set; }
    public string? XUserId { get; set; }

    /// <summary>Set when the Copilot agent (not the X API) confirmed this account exists.</summary>
    public DateTime? XVerifiedByCopilotAtUtc { get; set; }

    /// <summary><c>urn:li:person:…</c> or <c>urn:li:organization:…</c>.</summary>
    public string? LinkedInUrn { get; set; }

    /// <summary>Exact LinkedIn name; LinkedIn only links a mention when this matches the entity's name.</summary>
    public string? LinkedInName { get; set; }

    public string? LinkedInProfileUrl { get; set; }

    /// <summary>Set when the Copilot agent confirmed <see cref="LinkedInProfileUrl"/> is a real profile.</summary>
    public DateTime? LinkedInVerifiedByCopilotAtUtc { get; set; }

    public ContactSource Source { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
    public int UseCount { get; set; }

    /// <summary>Invalidates discovery work and editor snapshots after contact maintenance.</summary>
    public int DiscoveryRevision { get; set; }

    public bool HasIdentity(Platform platform) => platform switch
    {
        Platform.X => !string.IsNullOrWhiteSpace(XUsername),
        Platform.Bluesky => !string.IsNullOrWhiteSpace(BlueskyHandle),
        Platform.LinkedIn => !string.IsNullOrWhiteSpace(LinkedInUrn),
        Platform.Mastodon => !string.IsNullOrWhiteSpace(MastodonAcct),
        Platform.Threads => !string.IsNullOrWhiteSpace(ThreadsUsername),
        _ => false,
    };

    /// <summary>True when the LinkedIn profile is known but can't be tagged through the API, so it must be tagged by hand.</summary>
    public bool NeedsManualLinkedInTag => string.IsNullOrWhiteSpace(LinkedInUrn) && !string.IsNullOrWhiteSpace(LinkedInProfileUrl);

    /// <summary>Platforms an account search could still fill in.</summary>
    public IReadOnlyList<Platform> MissingSearchablePlatforms()
    {
        var list = new List<Platform>(2);
        if (string.IsNullOrWhiteSpace(XUsername)) list.Add(Platform.X);
        if (string.IsNullOrWhiteSpace(LinkedInUrn) && string.IsNullOrWhiteSpace(LinkedInProfileUrl)) list.Add(Platform.LinkedIn);
        return list;
    }
}

public enum BlueskyFacetKind
{
    Mention,
    Link,
    Tag,
}

/// <summary>A Bluesky rich-text facet. Offsets are UTF-8 byte offsets into the post text.</summary>
public sealed record BlueskyFacet(int ByteStart, int ByteEnd, BlueskyFacetKind Kind, string Value);

public sealed class RenderedPost
{
    public required Platform Platform { get; init; }

    /// <summary>Human-readable text; used for preview and clipboard.</summary>
    public required string DisplayText { get; init; }

    /// <summary>Text sent to the platform API (LinkedIn uses its escaped "little text" format).</summary>
    public required string PayloadText { get; init; }

    public IReadOnlyList<BlueskyFacet> Facets { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public required int Length { get; init; }
    public required int Limit { get; init; }

    /// <summary>The URL to render as a link-preview card, if any: Bluesky's external embed and Threads'
    /// <c>link_attachment</c>. This is the full, un-shortened URL of the last link in the post, matching the
    /// official Bluesky app's behavior.</summary>
    public string? LinkCardUrl { get; init; }

    public bool IsOverLimit => Length > Limit;
    public bool IsEmpty => string.IsNullOrWhiteSpace(DisplayText);
}
