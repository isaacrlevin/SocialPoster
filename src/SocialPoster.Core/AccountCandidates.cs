using System.ComponentModel;
using System.Text.RegularExpressions;

namespace SocialPoster.Core;

public enum AccountKind
{
    Person,
    Organization,
}

/// <summary>A possible X or LinkedIn account for a contact, as proposed by the account finder.</summary>
public sealed record AccountCandidate(
    Platform Platform,
    AccountKind Kind,
    string Handle,
    string? Name,
    string Url,
    string? OrganizationId,
    int Confidence,
    string? Reason)
{
    public string? LinkedInUrn => Platform == Platform.LinkedIn && OrganizationId is not null ? $"urn:li:organization:{OrganizationId}" : null;
}

/// <summary>Shape of the candidates the AI agent reports. Validated by <see cref="AccountCandidates.Normalize"/>.</summary>
public sealed record ReportedCandidate(
    [property: Description("\"X\" or \"LinkedIn\"")] string Platform,
    [property: Description("\"person\" or \"organization\"")] string? Kind,
    [property: Description("Profile URL exactly as it appeared in search results, e.g. https://x.com/jack or https://www.linkedin.com/in/jane-doe or https://www.linkedin.com/company/microsoft")] string Url,
    [property: Description("Display name shown on the profile")] string? Name,
    [property: Description("LinkedIn company pages only: the numeric organization id, if you saw it (e.g. from linkedin.com/company/1035). Otherwise null.")] string? OrganizationId,
    [property: Description("0-100: how sure you are this is the same person/organization")] int Confidence,
    [property: Description("One short sentence: the evidence that it matches")] string? Reason);

public static partial class AccountCandidates
{
    [GeneratedRegex(@"^(?:https?://)?(?:www\.|mobile\.)?(?:x|twitter)\.com/@?([A-Za-z0-9_]{1,15})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex XUrlRegex();

    [GeneratedRegex(@"^@?([A-Za-z0-9_]{1,15})$")]
    private static partial Regex XHandleRegex();

    [GeneratedRegex(@"^(?:https?://)?(?:[a-z]{2,3}\.|www\.)?linkedin\.com/(in|company|school|showcase)/([^/?#\s]+)/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInUrlRegex();

    private static readonly HashSet<string> ReservedXPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "search", "explore", "i", "intent", "share", "hashtag", "settings", "login", "signup", "messages", "notifications", "tos", "privacy",
    };

    /// <summary>Validates and cleans up agent output: drops malformed or duplicate URLs and sorts by confidence.</summary>
    public static List<AccountCandidate> Normalize(IEnumerable<ReportedCandidate> reported, int maxPerPlatform = 5)
    {
        var result = new List<AccountCandidate>();
        foreach (var r in reported)
        {
            if (Parse(r) is not { } c)
            {
                continue;
            }

            var i = result.FindIndex(e => e.Platform == c.Platform && string.Equals(e.Handle, c.Handle, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                result.Add(c);
            }
            else if (c.Confidence > result[i].Confidence)
            {
                result[i] = c with { OrganizationId = c.OrganizationId ?? result[i].OrganizationId };
            }
        }

        return result
            .OrderBy(c => c.Platform)
            .ThenByDescending(c => c.Confidence)
            .GroupBy(c => c.Platform)
            .SelectMany(g => g.Take(maxPerPlatform))
            .ToList();
    }

    private static AccountCandidate? Parse(ReportedCandidate r)
    {
        var url = (r.Url ?? "").Trim();
        var confidence = Math.Clamp(r.Confidence, 0, 100);
        var name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name.Trim();
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();

        if (r.Platform.Trim().Equals("X", StringComparison.OrdinalIgnoreCase) || r.Platform.Contains("twitter", StringComparison.OrdinalIgnoreCase))
        {
            var handle = XUrlRegex().Match(url) is { Success: true } m ? m.Groups[1].Value
                : XHandleRegex().Match(url) is { Success: true } h ? h.Groups[1].Value
                : null;
            if (handle is null || ReservedXPaths.Contains(handle))
            {
                return null;
            }

            return new AccountCandidate(Platform.X, AccountKind.Person, handle, name, $"https://x.com/{handle}", null, confidence, reason);
        }

        if (r.Platform.Contains("linkedin", StringComparison.OrdinalIgnoreCase) && LinkedInUrlRegex().Match(url) is { Success: true } li)
        {
            var section = li.Groups[1].Value.ToLowerInvariant();
            var slug = Uri.UnescapeDataString(li.Groups[2].Value);
            var isOrg = section != "in";
            var orgId = r.OrganizationId?.Trim();
            if (isOrg && orgId is null && slug.All(char.IsAsciiDigit))
            {
                orgId = slug;
            }

            if (orgId is not null && (!isOrg || orgId.Length == 0 || !orgId.All(char.IsAsciiDigit)))
            {
                orgId = null;
            }

            return new AccountCandidate(Platform.LinkedIn, isOrg ? AccountKind.Organization : AccountKind.Person, slug, name,
                $"https://www.linkedin.com/{section}/{Uri.EscapeDataString(slug)}", orgId, confidence, reason);
        }

        return null;
    }
}
