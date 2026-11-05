using System.Text;

namespace SocialPoster.Posting;

public delegate Contact? ContactResolver(string key);

/// <summary>Turns a platform-neutral template into platform-specific post text.</summary>
public static class PostRenderer
{
    public static RenderedPost Render(Platform platform, string? template, ContactResolver resolve) => platform switch
    {
        Platform.X => RenderX(template, resolve),
        Platform.Bluesky => RenderBluesky(template, resolve),
        Platform.LinkedIn => RenderLinkedIn(template, resolve),
        Platform.Mastodon => RenderMastodon(template, resolve),
        Platform.Threads => RenderThreads(template, resolve),
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    public static RenderedPost RenderThreads(string? template, ContactResolver resolve)
    {
        var text = new StringBuilder();
        var warnings = new List<string>();
        foreach (var segment in MentionTemplate.Parse(template))
        {
            if (segment is TextSegment t) text.Append(t.Text);
            else if (segment is MentionSegment m)
            {
                var contact = resolve(m.Key);
                if (contact is null)
                {
                    text.Append(m.Raw);
                    warnings.Add($"Unknown contact '{m.Key}'.");
                }
                else if (contact.HasIdentity(Platform.Threads))
                    text.Append('@').Append(contact.ThreadsUsername!.Trim().TrimStart('@'));
                else
                {
                    text.Append(contact.DisplayName);
                    warnings.Add($"{contact.DisplayName} has no Threads account linked; using their plain name.");
                }
            }
        }
        var value = text.ToString();
        var urls = TextMetrics.FindUrls(value).ToList();
        return new() { Platform = Platform.Threads, DisplayText = value, PayloadText = value,
            Length = TextMetrics.GraphemeCount(value), Limit = TextMetrics.ThreadsLimit, Warnings = warnings,
            LinkCardUrl = urls.Count > 0 ? urls[^1].Url : null };
    }

    public static RenderedPost RenderMastodon(string? template, ContactResolver resolve)
    {
        var text = new StringBuilder();
        var warnings = new List<string>();
        foreach (var segment in MentionTemplate.Parse(template))
        {
            if (segment is TextSegment t) text.Append(t.Text);
            else if (segment is MentionSegment m)
            {
                var contact = resolve(m.Key);
                if (contact is null)
                {
                    text.Append(m.Raw);
                    warnings.Add($"Unknown contact '{m.Key}'.");
                }
                else if (MastodonIdentity.NormalizeAcct(contact.MastodonAcct) is { } acct)
                    text.Append('@').Append(acct);
                else
                {
                    text.Append(contact.DisplayName);
                    warnings.Add($"{contact.DisplayName} has no Mastodon account linked; using their plain name.");
                }
            }
        }
        var value = text.ToString();
        return new() { Platform = Platform.Mastodon, DisplayText = value, PayloadText = value,
            Length = TextMetrics.MastodonLength(value), Limit = TextMetrics.MastodonLimit, Warnings = warnings };
    }

    public static RenderedPost RenderX(string? template, ContactResolver resolve)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();

        foreach (var segment in MentionTemplate.Parse(template))
        {
            switch (segment)
            {
                case TextSegment t:
                    sb.Append(t.Text);
                    break;
                case MentionSegment m when resolve(m.Key) is { } c:
                    if (c.HasIdentity(Platform.X))
                    {
                        sb.Append('@').Append(c.XUsername!.TrimStart('@'));
                    }
                    else
                    {
                        sb.Append(c.DisplayName);
                        warnings.Add($"{c.DisplayName} has no X account linked; using their plain name.");
                    }
                    break;
                case MentionSegment m:
                    sb.Append(m.Raw);
                    warnings.Add($"Unknown contact '{m.Key}'.");
                    break;
            }
        }

        var text = sb.ToString();
        return new RenderedPost
        {
            Platform = Platform.X,
            DisplayText = text,
            PayloadText = text,
            Warnings = warnings,
            Length = TextMetrics.XWeightedLength(text),
            Limit = TextMetrics.XLimit,
        };
    }

    public static RenderedPost RenderBluesky(string? template, ContactResolver resolve)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();
        var mentions = new List<(int Start, int End, string Did)>();

        foreach (var segment in MentionTemplate.Parse(template))
        {
            switch (segment)
            {
                case TextSegment t:
                    sb.Append(t.Text);
                    break;
                case MentionSegment m when resolve(m.Key) is { } c:
                    if (c.HasIdentity(Platform.Bluesky))
                    {
                        var start = sb.Length;
                        sb.Append('@').Append(c.BlueskyHandle!.TrimStart('@'));
                        if (!string.IsNullOrWhiteSpace(c.BlueskyDid))
                        {
                            mentions.Add((start, sb.Length, c.BlueskyDid));
                        }
                        else
                        {
                            warnings.Add($"{c.DisplayName}'s Bluesky DID isn't resolved; the mention won't be a link.");
                        }
                    }
                    else
                    {
                        sb.Append(c.DisplayName);
                        warnings.Add($"{c.DisplayName} has no Bluesky account linked; using their plain name.");
                    }
                    break;
                case MentionSegment m:
                    sb.Append(m.Raw);
                    warnings.Add($"Unknown contact '{m.Key}'.");
                    break;
            }
        }

        var text = sb.ToString();

        // Shorten displayed URLs the way Bluesky's app does (host + a bit of the path + "…"), while the
        // facet keeps the full URL as its link target, so long URLs don't eat into the 300-char limit.
        bool OverlapsMention(int s, int e) => mentions.Any(m => s < m.End && e > m.Start);
        var urls = TextMetrics.FindUrls(text).Where(u => !OverlapsMention(u.Index, u.Index + u.Length)).ToList();
        string finalText;
        var urlFacets = new List<(int Start, int End, string FullUrl)>();
        var mentionOffsets = mentions;

        if (urls.Count == 0)
        {
            finalText = text;
        }
        else
        {
            var finalSb = new StringBuilder();
            var replacements = new List<(int Index, int Length, int NewLength)>();
            var cursor = 0;
            foreach (var (index, length, url) in urls)
            {
                finalSb.Append(text, cursor, index - cursor);
                var shortLabel = TextMetrics.ShortenUrlForDisplay(url);
                var start = finalSb.Length;
                finalSb.Append(shortLabel);
                urlFacets.Add((start, finalSb.Length, url));
                replacements.Add((index, length, shortLabel.Length));
                cursor = index + length;
            }
            finalSb.Append(text, cursor, text.Length - cursor);
            finalText = finalSb.ToString();

            int MapOffset(int original)
            {
                var delta = 0;
                foreach (var r in replacements)
                {
                    if (r.Index + r.Length <= original) delta += r.NewLength - r.Length;
                    else break;
                }
                return original + delta;
            }

            mentionOffsets = mentions.Select(m => (MapOffset(m.Start), MapOffset(m.End), m.Did)).ToList();
        }

        var charFacets = new List<(int Start, int End, BlueskyFacetKind Kind, string Value)>();
        charFacets.AddRange(mentionOffsets.Select(m => (m.Start, m.End, BlueskyFacetKind.Mention, m.Did)));
        charFacets.AddRange(urlFacets.Select(f => (f.Start, f.End, BlueskyFacetKind.Link, f.FullUrl)));

        bool Overlaps(int s, int e) => charFacets.Any(f => s < f.End && e > f.Start);

        foreach (var (index, length, tag) in TextMetrics.FindHashtags(finalText))
        {
            if (!Overlaps(index, index + length))
            {
                charFacets.Add((index, index + length, BlueskyFacetKind.Tag, tag));
            }
        }

        var facets = charFacets
            .OrderBy(f => f.Start)
            .Select(f => new BlueskyFacet(
                TextMetrics.Utf8ByteOffset(finalText, f.Start),
                TextMetrics.Utf8ByteOffset(finalText, f.End),
                f.Kind,
                f.Value))
            .ToList();

        return new RenderedPost
        {
            Platform = Platform.Bluesky,
            DisplayText = finalText,
            PayloadText = finalText,
            Facets = facets,
            Warnings = warnings,
            Length = TextMetrics.GraphemeCount(finalText),
            Limit = TextMetrics.BlueskyLimit,
            LinkCardUrl = urlFacets.Count > 0 ? urlFacets[^1].FullUrl : null,
        };
    }

    public static RenderedPost RenderLinkedIn(string? template, ContactResolver resolve)
    {
        var display = new StringBuilder();
        var payload = new StringBuilder();
        var warnings = new List<string>();

        foreach (var segment in MentionTemplate.Parse(template))
        {
            switch (segment)
            {
                case TextSegment t:
                    display.Append(t.Text);
                    payload.Append(LinkedInText.EscapeWithHashtags(t.Text));
                    break;
                case MentionSegment m when resolve(m.Key) is { } c:
                    var name = string.IsNullOrWhiteSpace(c.LinkedInName) ? c.DisplayName : c.LinkedInName!;
                    display.Append(name);
                    if (c.HasIdentity(Platform.LinkedIn))
                    {
                        payload.Append("@[").Append(LinkedInText.Escape(name)).Append("](").Append(c.LinkedInUrn!.Trim()).Append(')');
                    }
                    else
                    {
                        payload.Append(LinkedInText.Escape(name));
                        warnings.Add(string.IsNullOrWhiteSpace(c.LinkedInProfileUrl)
                            ? $"{c.DisplayName} has no LinkedIn account linked; using their plain name."
                            : $"{c.DisplayName} will appear as a plain name; tag them by hand after posting (see the checklist below).");
                    }
                    break;
                case MentionSegment m:
                    display.Append(m.Raw);
                    payload.Append(LinkedInText.Escape(m.Raw));
                    warnings.Add($"Unknown contact '{m.Key}'.");
                    break;
            }
        }

        var text = display.ToString();
        return new RenderedPost
        {
            Platform = Platform.LinkedIn,
            DisplayText = text,
            PayloadText = payload.ToString(),
            Warnings = warnings,
            Length = text.Length,
            Limit = TextMetrics.LinkedInLimit,
        };
    }
}
