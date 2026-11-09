namespace SocialPoster.Core.Services;

/// <summary>Why an import was linked to a saved contact without hard identity evidence.</summary>
/// <param name="Contact">The saved contact the import was linked to.</param>
/// <param name="Username">The shared alphanumeric username.</param>
/// <param name="SavedPlatform">The platform on which the saved contact already uses that username.</param>
internal sealed record AutoLinkMatch(Contact Contact, string Username, Platform SavedPlatform);

public sealed partial class ContactService
{
    /// <summary>Shorter usernames are too often held by different people on different platforms.</summary>
    internal const int MinAutoLinkUsername = 6;

    /// <summary>
    /// The narrow cross-platform rule: link an import to a saved contact only when exactly one saved contact has the
    /// identical normalized name, already uses the same username (at least <see cref="MinAutoLinkUsername"/>
    /// characters) on another platform, has no account on the importing platform, and no profile link on either
    /// side points somewhere else.
    /// </summary>
    private static AutoLinkMatch? FindAutoLink(IEnumerable<Contact> contacts, Contact incoming, Platform platform,
        CrossPlatformHints hints, PlatformSelection selection)
    {
        if (hints.Ambiguous) return null;
        var name = CleanName(incoming.DisplayName);
        var username = UsernameLocal(platform, CanonicalHandle(PrimaryHandle(incoming, platform), platform));
        if (name.Length < 3 || username is not { Length: >= MinAutoLinkUsername }) return null;

        AutoLinkMatch? found = null;
        foreach (var c in contacts)
        {
            if (c.Id == incoming.Id || CleanName(c.DisplayName) != name || HasIdentityOn(c, platform)) continue;
            var print = new DuplicateFingerprint(c);
            if (!print.HandleLocals.TryGetValue(username, out var savedPlatform) || savedPlatform == platform) continue;
            if (Collisions(c, incoming).Count > 0 || ConflictsWithHints(c, hints)) continue;
            var savedHints = ImportHints(c.Bio, selection);
            if (savedHints.Ambiguous || ConflictsWithHints(incoming, savedHints)) continue;
            if (found is not null) return null; // Two equally plausible contacts: let the user decide.
            found = new(c, username, savedPlatform);
        }
        return found;
    }

    private static bool HasIdentityOn(Contact c, Platform platform) =>
        !string.IsNullOrWhiteSpace(PrimaryId(c, platform)) || !string.IsNullOrWhiteSpace(PrimaryHandle(c, platform)) ||
        platform == Platform.Mastodon && !string.IsNullOrWhiteSpace(c.MastodonProfileUrl);

    /// <summary>True when an explicit profile link names a different account than the contact already has.</summary>
    private static bool ConflictsWithHints(Contact c, CrossPlatformHints hints) =>
        Different(c.XUsername, hints.XUsername, (a, b) => SameHandle(a, b, Platform.X)) ||
        (hints.BlueskyIdentity?.StartsWith("did:", StringComparison.Ordinal) == true
            ? Different(c.BlueskyDid, hints.BlueskyIdentity, SameId)
            : Different(c.BlueskyHandle, hints.BlueskyIdentity, (a, b) => SameHandle(a, b, Platform.Bluesky))) ||
        Different(c.MastodonAcct, hints.MastodonAcct, (a, b) => SameHandle(a, b, Platform.Mastodon)) ||
        Different(c.LinkedInProfileUrl, hints.LinkedInProfileUrl, SameProfile);
}
