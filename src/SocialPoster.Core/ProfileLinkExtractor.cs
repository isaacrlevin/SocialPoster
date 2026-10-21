using System.Text.RegularExpressions;

namespace SocialPoster.Core;

public sealed record CrossPlatformHints(string? XUsername, string? LinkedInProfileUrl, string? LinkedInCompanyVanity,
    string? BlueskyIdentity = null, bool Ambiguous = false, string? MastodonAcct = null);

/// <summary>Finds links to other platforms in a profile bio, used to suggest cross-platform matches.</summary>
public static partial class ProfileLinkExtractor
{
    [GeneratedRegex(@"https://(?<host>[a-z0-9.-]+(?::[0-9]+)?)/@(?<user>[a-z0-9_]+)(?=[/?#\s<>""']|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MastodonLink();

    [GeneratedRegex(@"(?<![\w/])@(?<user>[a-z0-9_]+)@(?<host>[a-z0-9.-]+(?::[0-9]+)?)(?=[\s<>""',!?)]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MastodonAcctLink();
    [GeneratedRegex(@"(?<![\w./-])(?:https?://)?(?:www\.)?(?:x|twitter)\.com/(?<u>[A-Za-z0-9_]{1,15})(?=[/?#\s<>""']|$)", RegexOptions.IgnoreCase)]
    private static partial Regex XLink();

    [GeneratedRegex(@"(?:^|\s)(?:X|Twitter|𝕏)\s*[:：]?\s*@(?<u>[A-Za-z0-9_]{1,15})\b", RegexOptions.IgnoreCase)]
    private static partial Regex XInline();

    [GeneratedRegex(@"(?<![\w./-])(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/in/(?<v>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInPerson();

    [GeneratedRegex(@"(?<![\w./-])(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/company/(?<v>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInCompany();

    [GeneratedRegex(@"(?<![\w./-])(?:https?://)?bsky\.app/profile/(?<u>did:plc:[a-z0-9]+|[a-z0-9][a-z0-9.-]+\.[a-z0-9.-]+)(?=[/?#\s<>""']|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BlueskyLink();

    private static readonly HashSet<string> XReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "i", "intent", "share", "search", "hashtag", "explore", "settings", "messages",
    };

    // These services also use /@user URLs; that shape alone isn't Mastodon evidence there.
    private static readonly HashSet<string> NonMastodonHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "x.com", "twitter.com", "tiktok.com", "threads.net", "threads.com",
        "youtube.com", "instagram.com", "facebook.com", "bsky.app",
    };

    public static CrossPlatformHints Extract(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio))
        {
            return new(null, null, null);
        }

        var xs = XLink().Matches(bio).Concat(XInline().Matches(bio))
            .Select(m => m.Groups["u"].Value).Where(u => !XReservedPaths.Contains(u))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var bskys = BlueskyLink().Matches(bio).Select(m => m.Groups["u"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mastodons = MastodonLink().Matches(bio).Concat(MastodonAcctLink().Matches(bio))
            .Where(m => !NonMastodonHosts.Contains(m.Groups["host"].Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? m.Groups["host"].Value[4..] : m.Groups["host"].Value))
            .Select(m => MastodonIdentity.NormalizeAcct($"{m.Groups["user"].Value}@{m.Groups["host"].Value}"))
            .Where(a => a is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var person = LinkedInPerson().Match(bio);
        var company = LinkedInCompany().Match(bio);

        return new(
            xs.Length == 1 ? xs[0] : null,
            person.Success ? $"https://www.linkedin.com/in/{person.Groups["v"].Value}" : null,
            company.Success ? company.Groups["v"].Value : null,
            bskys.Length == 1 ? bskys[0] : null,
            xs.Length > 1 || bskys.Length > 1 || mastodons.Length > 1 ||
            LinkedInPerson().Matches(bio).Select(m => "in/" + m.Groups["v"].Value)
                .Concat(LinkedInCompany().Matches(bio).Select(m => "company/" + m.Groups["v"].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1,
            mastodons.Length == 1 ? mastodons[0] : null);
    }

    /// <summary>Individual explicit links for review suggestions, including ambiguous bios. Never apply these automatically.</summary>
    public static IReadOnlyList<CrossPlatformHints> ExtractReviewHints(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio)) return [];
        return new[] { XLink(), XInline(), BlueskyLink(), MastodonLink(), MastodonAcctLink(),
                LinkedInPerson(), LinkedInCompany() }
            .SelectMany(regex => regex.Matches(bio).Select(match => Extract(match.Value)))
            .Distinct().ToList();
    }
}
