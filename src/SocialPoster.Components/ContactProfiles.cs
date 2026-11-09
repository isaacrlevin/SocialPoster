using SocialPoster.Core;
using SocialPoster.Posting;

namespace SocialPoster.Components;

/// <summary>Public profile links for a contact's saved accounts.</summary>
public static class ContactProfiles
{
    /// <summary>The browser URL of the contact's profile on <paramref name="platform"/>, or null when none can be built.</summary>
    public static string? Url(Contact contact, Platform platform)
    {
        if (platform == Platform.Mastodon)
            return MastodonIdentity.ProfileUrl(contact.MastodonAcct) ?? HttpUrl(contact.MastodonProfileUrl);
        if (platform == Platform.Threads && !string.IsNullOrWhiteSpace(contact.ThreadsUsername))
            return $"https://www.threads.net/@{Uri.EscapeDataString(contact.ThreadsUsername.Trim().TrimStart('@'))}";
        if (platform == Platform.X && !string.IsNullOrWhiteSpace(contact.XUsername))
            return AccountCandidates.Normalize([new("X", null, contact.XUsername, null, null, 100, null)])
                .SingleOrDefault()?.Url;
        if (platform == Platform.Bluesky)
        {
            var identity = !string.IsNullOrWhiteSpace(contact.BlueskyDid) ? contact.BlueskyDid : contact.BlueskyHandle;
            return string.IsNullOrWhiteSpace(identity) ? null : $"https://bsky.app/profile/{Uri.EscapeDataString(identity.Trim().TrimStart('@'))}";
        }
        if (platform == Platform.LinkedIn)
        {
            if (!string.IsNullOrWhiteSpace(contact.LinkedInProfileUrl))
                return AccountCandidates.Normalize([new("LinkedIn", null, contact.LinkedInProfileUrl, null, null, 100, null)])
                    .SingleOrDefault()?.Url;
            const string prefix = "urn:li:organization:";
            if (contact.LinkedInUrn?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                contact.LinkedInUrn[prefix.Length..] is { Length: > 0 } id && id.All(char.IsAsciiDigit))
                return $"https://www.linkedin.com/company/{id}";
        }
        return null;
    }

    // Stored profile URLs come from remote servers; only ever link to http(s).
    private static string? HttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri : null;
}
