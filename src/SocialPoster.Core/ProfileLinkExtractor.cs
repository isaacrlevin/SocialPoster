using System.Text.RegularExpressions;

namespace SocialPoster.Core;

public sealed record CrossPlatformHints(string? XUsername, string? LinkedInProfileUrl, string? LinkedInCompanyVanity);

/// <summary>Finds links to other platforms in a profile bio, used to suggest cross-platform matches.</summary>
public static partial class ProfileLinkExtractor
{
    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(?:x|twitter)\.com/(?<u>[A-Za-z0-9_]{1,15})\b", RegexOptions.IgnoreCase)]
    private static partial Regex XLink();

    [GeneratedRegex(@"(?:^|\s)(?:X|Twitter|𝕏)\s*[:：]?\s*@(?<u>[A-Za-z0-9_]{1,15})\b", RegexOptions.IgnoreCase)]
    private static partial Regex XInline();

    [GeneratedRegex(@"(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/in/(?<v>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInPerson();

    [GeneratedRegex(@"(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/company/(?<v>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInCompany();

    private static readonly HashSet<string> XReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "i", "intent", "share", "search", "hashtag", "explore", "settings", "messages",
    };

    public static CrossPlatformHints Extract(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio))
        {
            return new(null, null, null);
        }

        string? x = null;
        foreach (Match m in XLink().Matches(bio))
        {
            if (!XReservedPaths.Contains(m.Groups["u"].Value))
            {
                x = m.Groups["u"].Value;
                break;
            }
        }

        x ??= XInline().Match(bio) is { Success: true } inline ? inline.Groups["u"].Value : null;

        var person = LinkedInPerson().Match(bio);
        var company = LinkedInCompany().Match(bio);

        return new(
            x,
            person.Success ? $"https://www.linkedin.com/in/{person.Groups["v"].Value}" : null,
            company.Success ? company.Groups["v"].Value : null);
    }
}
