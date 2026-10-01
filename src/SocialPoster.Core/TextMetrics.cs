using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SocialPoster.Core;

public static partial class TextMetrics
{
    public const int XLimit = 280;
    public const int BlueskyLimit = 300;
    public const int LinkedInLimit = 3000;

    /// <summary>Every URL counts as this many characters on X (t.co wrapping).</summary>
    public const int XUrlLength = 23;

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<=^|\s)#(?<tag>[\p{L}\p{N}_]*[\p{L}_][\p{L}\p{N}_]*)")]
    private static partial Regex HashtagRegex();

    /// <summary>Finds URLs, trimming trailing punctuation that is almost never part of the link.</summary>
    public static IEnumerable<(int Index, int Length, string Url)> FindUrls(string text)
    {
        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
            if (url.Length > "https://".Length)
            {
                yield return (m.Index, url.Length, url);
            }
        }
    }

    public static IEnumerable<(int Index, int Length, string Tag)> FindHashtags(string text)
    {
        foreach (Match m in HashtagRegex().Matches(text))
        {
            yield return (m.Index, m.Length, m.Groups["tag"].Value);
        }
    }

    /// <summary>
    /// Shortens a URL the way Bluesky's official app displays link facets: host + start of the
    /// path/query/fragment, truncated with an ellipsis if that combined path is more than 15 characters.
    /// The full URL is still stored in the facet, so the link keeps working; only the displayed text (and
    /// therefore the character count) shrinks.
    /// </summary>
    public static string ShortenUrlForDisplay(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return url;
        }

        var path = (uri.PathAndQuery + uri.Fragment).TrimEnd('/');
        return path.Length > 15
            ? uri.Host + path[..13] + "..."
            : uri.Host + path;
    }

    public static int GraphemeCount(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// Approximates X's weighted length (twitter-text v3): URLs count 23, most Latin
    /// characters count 1, emoji and CJK count 2.
    /// </summary>
    public static int XWeightedLength(string text)
    {
        var total = 0;
        var cursor = 0;
        foreach (var (index, length, _) in FindUrls(text))
        {
            total += WeighPlain(text[cursor..index]);
            total += XUrlLength;
            cursor = index + length;
        }

        total += WeighPlain(text[cursor..]);
        return total;
    }

    private static int WeighPlain(string text)
    {
        var total = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (IsEmoji(element))
            {
                total += 2;
                continue;
            }

            foreach (var rune in element.EnumerateRunes())
            {
                total += IsLightWeight(rune.Value) ? 1 : 2;
            }
        }

        return total;
    }

    private static bool IsLightWeight(int cp) =>
        cp is (>= 0 and <= 4351) or (>= 8192 and <= 8205) or (>= 8208 and <= 8223) or (>= 8242 and <= 8247);

    private static bool IsEmoji(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is (>= 0x1F000 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F or 0x200D)
            {
                return true;
            }
        }

        return false;
    }

    public static int Utf8ByteOffset(string text, int charIndex) =>
        Encoding.UTF8.GetByteCount(text.AsSpan(0, charIndex));
}
