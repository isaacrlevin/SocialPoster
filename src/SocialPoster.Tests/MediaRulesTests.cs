using SocialPoster.Core;

namespace SocialPoster.Tests;

public class MediaRulesTests
{
    private const long MB = 1024 * 1024;

    private static MediaAttachment Make(string name, long size)
    {
        Assert.True(MediaRules.TryClassify(name, out var kind, out var contentType));
        return new MediaAttachment { FileName = name, Kind = kind, ContentType = contentType, Size = size };
    }

    private static IReadOnlyList<string> Check(Platform p, params MediaAttachment[] media) => MediaRules.Validate(p, media);

    [Theory]
    [InlineData("a.JPG", MediaKind.Image, "image/jpeg")]
    [InlineData("a.gif", MediaKind.Gif, "image/gif")]
    [InlineData("a.mov", MediaKind.Video, "video/quicktime")]
    public void Classify(string name, MediaKind kind, string type)
    {
        Assert.True(MediaRules.TryClassify(name, out var k, out var t));
        Assert.Equal((kind, type), (k, t));
    }

    [Fact]
    public void Classify_RejectsUnknown() => Assert.False(MediaRules.TryClassify("notes.pdf", out _, out _));

    [Fact]
    public void NoMedia_IsAlwaysValid()
    {
        foreach (var p in Enum.GetValues<Platform>()) Assert.Empty(Check(p));
    }

    [Fact]
    public void Bluesky_ImageOverOneMegabyte_IsRejected_ButOthersAccept()
    {
        var photo = Make("p.jpg", 3 * MB);
        Assert.Single(Check(Platform.Bluesky, photo));
        Assert.Empty(Check(Platform.X, photo));
        Assert.Empty(Check(Platform.LinkedIn, photo));
    }

    [Fact]
    public void XImage_OverFiveMegabytes_IsRejected() => Assert.Single(Check(Platform.X, Make("p.png", 6 * MB)));

    [Fact]
    public void Gif_NotSupportedOnBluesky() => Assert.Single(Check(Platform.Bluesky, Make("a.gif", MB)));

    [Fact]
    public void Gif_SupportedOnXAndLinkedIn_WithinLimits()
    {
        Assert.Empty(Check(Platform.X, Make("a.gif", 10 * MB)));
        Assert.Single(Check(Platform.X, Make("a.gif", 16 * MB)));
        Assert.Empty(Check(Platform.LinkedIn, Make("a.gif", 8 * MB)));
    }

    [Fact]
    public void ImageCount_Limits()
    {
        var five = Enumerable.Range(0, 5).Select(i => Make($"{i}.jpg", 1000)).ToArray();
        Assert.Single(Check(Platform.X, five));
        Assert.Single(Check(Platform.Bluesky, five));
        Assert.Empty(Check(Platform.LinkedIn, five));
        Assert.Empty(Check(Platform.X, five.Take(4).ToArray()));
    }

    [Fact]
    public void OnlyOneVideo_AndNoMixing()
    {
        Assert.Single(Check(Platform.X, Make("a.mp4", MB), Make("b.mp4", MB)));
        Assert.Contains(Check(Platform.LinkedIn, Make("a.mp4", MB), Make("a.jpg", MB)), e => e.Contains("combine"));
        Assert.Contains(Check(Platform.X, Make("a.gif", MB), Make("a.jpg", MB)), e => e.Contains("combine"));
    }

    [Fact]
    public void Video_FormatAndSize()
    {
        Assert.Empty(Check(Platform.X, Make("a.mov", 100 * MB)));
        Assert.Single(Check(Platform.LinkedIn, Make("a.mov", MB)));       // LinkedIn is MP4 only
        Assert.Single(Check(Platform.Bluesky, Make("a.mp4", 150 * MB)));  // Bluesky caps at 100 MB
        Assert.Single(Check(Platform.LinkedIn, Make("a.mp4", 10 * 1024))); // under LinkedIn's 75 KB minimum
        Assert.Empty(Check(Platform.Bluesky, Make("a.webm", 10 * MB)));
    }
}
