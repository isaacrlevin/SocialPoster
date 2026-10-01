using SocialPoster.Core;

namespace SocialPoster.Tests;

public class RendererTests
{
    private static readonly Contact Satya = new()
    {
        Key = "satya",
        DisplayName = "Satya Nadella",
        XUsername = "satyanadella",
        BlueskyHandle = "satya.bsky.social",
        BlueskyDid = "did:plc:satya",
        LinkedInUrn = "urn:li:person:abc123",
        LinkedInName = "Satya Nadella",
    };

    private static readonly Contact Msft = new()
    {
        Key = "microsoft",
        DisplayName = "Microsoft",
        XUsername = "Microsoft",
        LinkedInUrn = "urn:li:organization:1035",
    };

    private static Contact? Resolve(string key) => key switch
    {
        "satya" => Satya,
        "microsoft" => Msft,
        _ => null,
    };

    [Fact]
    public void Parse_SplitsTextAndMentions()
    {
        var segments = MentionTemplate.Parse("Hi {@satya} and {@microsoft}!");
        Assert.Collection(segments,
            s => Assert.Equal(new TextSegment("Hi "), s),
            s => Assert.Equal("satya", Assert.IsType<MentionSegment>(s).Key),
            s => Assert.Equal(new TextSegment(" and "), s),
            s => Assert.Equal("microsoft", Assert.IsType<MentionSegment>(s).Key),
            s => Assert.Equal(new TextSegment("!"), s));
    }

    [Fact]
    public void X_UsesHandles_AndFallsBackToName()
    {
        var r = PostRenderer.RenderX("Great talk {@satya} at {@microsoft}", Resolve);
        Assert.Equal("Great talk @satyanadella at @Microsoft", r.DisplayText);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Bluesky_FallsBackToName_WhenNoAccount()
    {
        var r = PostRenderer.RenderBluesky("Thanks {@microsoft}", Resolve);
        Assert.Equal("Thanks Microsoft", r.DisplayText);
        Assert.Single(r.Warnings);
        Assert.Empty(r.Facets);
    }

    [Fact]
    public void Bluesky_FacetsUseUtf8ByteOffsets()
    {
        var r = PostRenderer.RenderBluesky("🎉 hi {@satya} see https://example.com #dotnet", Resolve);
        Assert.Equal("🎉 hi @satya.bsky.social see example.com #dotnet", r.DisplayText);

        var bytes = System.Text.Encoding.UTF8.GetBytes(r.DisplayText);
        string Slice(BlueskyFacet f) => System.Text.Encoding.UTF8.GetString(bytes[f.ByteStart..f.ByteEnd]);

        Assert.Collection(r.Facets,
            f => { Assert.Equal(BlueskyFacetKind.Mention, f.Kind); Assert.Equal("did:plc:satya", f.Value); Assert.Equal("@satya.bsky.social", Slice(f)); Assert.Equal(8, f.ByteStart); },
            f => { Assert.Equal(BlueskyFacetKind.Link, f.Kind); Assert.Equal("https://example.com", f.Value); Assert.Equal("example.com", Slice(f)); },
            f => { Assert.Equal(BlueskyFacetKind.Tag, f.Kind); Assert.Equal("dotnet", f.Value); Assert.Equal("#dotnet", Slice(f)); });
    }

    [Fact]
    public void Bluesky_ShortensLongUrls_ButKeepsFullUrlInFacet()
    {
        const string url = "https://www.tiktok.com/@isaacrlevin/video/7690665568956960031";
        var r = PostRenderer.RenderBluesky($"Check this out: {url}", Resolve);

        Assert.Equal("Check this out: www.tiktok.com/@isaacrlevin...", r.DisplayText);
        Assert.True(r.Length < url.Length, "the displayed/counted text should be much shorter than the raw URL");

        var f = Assert.Single(r.Facets);
        Assert.Equal(BlueskyFacetKind.Link, f.Kind);
        Assert.Equal(url, f.Value);
        var bytes = System.Text.Encoding.UTF8.GetBytes(r.DisplayText);
        Assert.Equal("www.tiktok.com/@isaacrlevin...", System.Text.Encoding.UTF8.GetString(bytes[f.ByteStart..f.ByteEnd]));
    }

    [Fact]
    public void LinkedIn_EscapesReservedChars_AndFormatsMentionsAndHashtags()
    {
        var r = PostRenderer.RenderLinkedIn("Hi {@satya} (and {@microsoft}) #AI_rocks!", Resolve);
        Assert.Equal("Hi Satya Nadella (and Microsoft) #AI_rocks!", r.DisplayText);
        Assert.Equal(
            @"Hi @[Satya Nadella](urn:li:person:abc123) \(and @[Microsoft](urn:li:organization:1035)\) {hashtag|\#|AI\_rocks}!",
            r.PayloadText);
    }

    [Fact]
    public void UnknownContact_IsLeftAsIs_WithWarning()
    {
        var r = PostRenderer.RenderX("Hi {@nobody}", Resolve);
        Assert.Equal("Hi {@nobody}", r.DisplayText);
        Assert.Contains(r.Warnings, w => w.Contains("nobody"));
    }

    [Theory]
    [InlineData("hello", 5)]
    [InlineData("see https://example.com/a/very/long/path/that/is/long", 4 + 23)]
    [InlineData("🎉", 2)]
    [InlineData("日本", 4)]
    public void XWeightedLength(string text, int expected) =>
        Assert.Equal(expected, TextMetrics.XWeightedLength(text));

    [Fact]
    public void X_FlagsOverLimit()
    {
        var r = PostRenderer.RenderX(new string('a', 281), Resolve);
        Assert.True(r.IsOverLimit);
    }
}
