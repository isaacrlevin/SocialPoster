using System.Text;
using System.Text.RegularExpressions;

namespace SocialPoster.Posting;

/// <summary>
/// Helpers for LinkedIn's "little text" commentary format used by the Posts API, in which
/// reserved characters must be backslash-escaped and hashtags use <c>{hashtag|\#|tag}</c>.
/// </summary>
public static partial class LinkedInText
{
    private const string Reserved = @"\|{}@[]()<>#*_~";

    [GeneratedRegex(@"^urn:li:(person|organization):[A-Za-z0-9_-]+$")]
    private static partial Regex UrnRegex();

    public static bool IsValidMentionUrn(string? urn) => urn is not null && UrnRegex().IsMatch(urn.Trim());

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Reserved.Contains(ch))
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    public static string EscapeWithHashtags(string text)
    {
        var sb = new StringBuilder();
        var cursor = 0;
        foreach (var (index, length, tag) in TextMetrics.FindHashtags(text))
        {
            sb.Append(Escape(text[cursor..index]));
            sb.Append(@"{hashtag|\#|").Append(Escape(tag)).Append('}');
            cursor = index + length;
        }

        sb.Append(Escape(text[cursor..]));
        return sb.ToString();
    }
}
