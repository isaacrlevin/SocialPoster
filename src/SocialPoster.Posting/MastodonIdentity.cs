using System.Text.RegularExpressions;

namespace SocialPoster.Posting;

/// <summary>Normalizes federation identities, never treating an instance-local numeric ID as a global identity.</summary>
public static partial class MastodonIdentity
{
    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex Username();

    /// <summary>Qualifies a local acct using its serving instance; fully qualified accts keep their original domain.</summary>
    public static string? NormalizeAcct(string? value, Uri? instance = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().TrimStart('@');
        var parts = clean.Split('@');
        if (!Username().IsMatch(parts[0])) return null;
        var domain = parts.Length == 1 ? instance?.Authority : parts.Length == 2 ? parts[1] : null;
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') ||
            domain.Contains('\\') || domain.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate("https://" + domain, UriKind.Absolute, out var host) ||
            host.UserInfo.Length != 0 || host.Query.Length != 0 || host.Fragment.Length != 0 ||
            host.HostNameType == UriHostNameType.Unknown) return null;
        return $"{parts[0].ToLowerInvariant()}@{host.Authority.ToLowerInvariant()}";
    }

    public static string? ProfileUrl(string? acct)
    {
        var normalized = NormalizeAcct(acct);
        if (normalized is null) return null;
        var parts = normalized.Split('@');
        return $"https://{parts[1]}/@{parts[0]}";
    }
}
