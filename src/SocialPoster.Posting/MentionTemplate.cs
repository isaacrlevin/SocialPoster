using System.Text.RegularExpressions;

namespace SocialPoster.Posting;

public abstract record TemplateSegment;

public sealed record TextSegment(string Text) : TemplateSegment;

public sealed record MentionSegment(string Key, string Raw) : TemplateSegment;

/// <summary>
/// Parses platform-neutral post templates. A mention is written as <c>{@key}</c>,
/// where <c>key</c> refers to a saved <see cref="Contact"/>.
/// </summary>
public static partial class MentionTemplate
{
    [GeneratedRegex(@"\{@(?<key>[A-Za-z0-9][A-Za-z0-9._-]*)\}")]
    private static partial Regex MentionRegex();

    public static IReadOnlyList<TemplateSegment> Parse(string? template)
    {
        var segments = new List<TemplateSegment>();
        if (string.IsNullOrEmpty(template))
        {
            return segments;
        }

        var last = 0;
        foreach (Match m in MentionRegex().Matches(template))
        {
            if (m.Index > last)
            {
                segments.Add(new TextSegment(template[last..m.Index]));
            }

            segments.Add(new MentionSegment(m.Groups["key"].Value, m.Value));
            last = m.Index + m.Length;
        }

        if (last < template.Length)
        {
            segments.Add(new TextSegment(template[last..]));
        }

        return segments;
    }

    public static IReadOnlyList<string> GetMentionKeys(string? template) =>
        Parse(template).OfType<MentionSegment>()
            .Select(m => m.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Token(string key) => $"{{@{key}}}";
}
