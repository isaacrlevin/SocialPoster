using System.Text;
using System.Text.RegularExpressions;

namespace SocialPoster.Core;

public static partial class Slugs
{
    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex InvalidChars();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex RepeatedDashes();

    /// <summary>
    /// Builds a template key from a handle: <c>satyanadella.bsky.social</c> and
    /// <c>satyanadella.com</c> both become <c>satyanadella</c>.
    /// </summary>
    public static string FromHandle(string? handle)
    {
        var h = (handle ?? "").Trim().TrimStart('@');
        var dot = h.IndexOf('.');
        return Create(dot > 0 ? h[..dot] : h);
    }

    /// <summary>
    /// Builds a template key from a name, e.g. "Satya Nadella" becomes <c>satya-nadella</c>.
    /// </summary>
    public static string Create(string? handleOrName)
    {
        var s = (handleOrName ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (s.EndsWith(".bsky.social", StringComparison.Ordinal))
        {
            s = s[..^".bsky.social".Length];
        }

        s = RemoveDiacritics(s).Replace(' ', '-');
        s = InvalidChars().Replace(s, "-");
        s = RepeatedDashes().Replace(s, "-").Trim('-', '.', '_');
        return s.Length == 0 ? "contact" : s;
    }

    public static string MakeUnique(string baseKey, Func<string, bool> exists)
    {
        if (!exists(baseKey))
        {
            return baseKey;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseKey}-{i}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
