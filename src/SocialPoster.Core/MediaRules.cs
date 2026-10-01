namespace SocialPoster.Core;

public enum MediaKind
{
    Image,
    Gif,
    Video,
}

/// <summary>A file the user picked to attach to a post.</summary>
public sealed class MediaAttachment
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string FileName { get; init; }
    public required MediaKind Kind { get; init; }
    public required string ContentType { get; init; }
    public required long Size { get; init; }

    /// <summary>Where the bytes live on disk (null in tests).</summary>
    public string? Path { get; init; }

    public string? AltText { get; set; }
}

/// <summary>Per-platform media limits and the checks that enforce them.</summary>
public static class MediaRules
{
    public const long MaxFileSize = 512L * 1024 * 1024;
    public const int MaxAltTextLength = 1000;

    private const long MB = 1024 * 1024;

    private sealed record Limits(
        string[] ImageExtensions,
        long ImageMax,
        int MaxImages,
        long? GifMax,
        string[] VideoExtensions,
        long VideoMin,
        long VideoMax,
        string Hint);

    // Sources: X media upload docs, AT Protocol app.bsky.embed.images/video lexicons, LinkedIn Images/Videos API docs.
    private static readonly Dictionary<Platform, Limits> LimitsByPlatform = new()
    {
        [Platform.X] = new([".jpg", ".jpeg", ".png", ".webp"], 5 * MB, 4, 15 * MB, [".mp4", ".mov"], 0, 512 * MB,
            "Up to 4 images (JPG/PNG/WebP, 5 MB each), or 1 GIF (15 MB), or 1 video (MP4/MOV, 512 MB). Can't be mixed."),
        [Platform.Bluesky] = new([".jpg", ".jpeg", ".png", ".webp"], 1_000_000, 4, null, [".mp4", ".mov", ".webm"], 0, 100_000_000,
            "Up to 4 images (JPG/PNG/WebP, under 1 MB each), or 1 video (MP4/MOV/WebM, 100 MB, 3 min). No GIFs. Can't be mixed."),
        [Platform.LinkedIn] = new([".jpg", ".jpeg", ".png"], 10 * MB, 20, 10 * MB, [".mp4"], 75 * 1024, 500 * MB,
            "Up to 20 images (JPG/PNG, 10 MB each), or 1 GIF, or 1 video (MP4, 75 KB–500 MB, 3 s–30 min). Can't be mixed."),
    };

    public static readonly IReadOnlyList<string> AcceptedExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".gif", ".mp4", ".mov", ".webm"];

    public static string Hint(Platform platform) => LimitsByPlatform[platform].Hint;

    public static bool TryClassify(string fileName, out MediaKind kind, out string contentType)
    {
        (kind, contentType) = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => (MediaKind.Image, "image/jpeg"),
            ".png" => (MediaKind.Image, "image/png"),
            ".webp" => (MediaKind.Image, "image/webp"),
            ".gif" => (MediaKind.Gif, "image/gif"),
            ".mp4" => (MediaKind.Video, "video/mp4"),
            ".mov" => (MediaKind.Video, "video/quicktime"),
            ".webm" => (MediaKind.Video, "video/webm"),
            _ => (default, ""),
        };
        return contentType.Length > 0;
    }

    /// <summary>Returns every reason this set of attachments can't be posted to <paramref name="platform"/> (empty when OK).</summary>
    public static IReadOnlyList<string> Validate(Platform platform, IReadOnlyList<MediaAttachment> media)
    {
        var errors = new List<string>();
        if (media.Count == 0) return errors;

        var l = LimitsByPlatform[platform];
        var name = platform.ToString();

        var images = media.Where(m => m.Kind == MediaKind.Image).ToList();
        var gifs = media.Where(m => m.Kind == MediaKind.Gif).ToList();
        var videos = media.Where(m => m.Kind == MediaKind.Video).ToList();

        foreach (var m in images)
        {
            if (!l.ImageExtensions.Contains(Path.GetExtension(m.FileName).ToLowerInvariant()))
                errors.Add($"{m.FileName}: {name} only accepts {Join(l.ImageExtensions)} images.");
            else if (m.Size > l.ImageMax)
                errors.Add($"{m.FileName} is {Size(m.Size)}; {name} allows images up to {Size(l.ImageMax)}.");
        }

        foreach (var m in gifs)
        {
            if (l.GifMax is not { } gifMax)
                errors.Add($"{m.FileName}: {name} doesn't support GIFs.");
            else if (m.Size > gifMax)
                errors.Add($"{m.FileName} is {Size(m.Size)}; {name} allows GIFs up to {Size(gifMax)}.");
        }

        foreach (var m in videos)
        {
            if (!l.VideoExtensions.Contains(Path.GetExtension(m.FileName).ToLowerInvariant()))
                errors.Add($"{m.FileName}: {name} only accepts {Join(l.VideoExtensions)} video.");
            else if (m.Size > l.VideoMax)
                errors.Add($"{m.FileName} is {Size(m.Size)}; {name} allows video up to {Size(l.VideoMax)}.");
            else if (m.Size < l.VideoMin)
                errors.Add($"{m.FileName} is {Size(m.Size)}; {name} needs video of at least {Size(l.VideoMin)}.");
        }

        if (images.Count > l.MaxImages) errors.Add($"{name} allows at most {l.MaxImages} images per post (you have {images.Count}).");
        if (gifs.Count > 1) errors.Add($"{name} allows only one GIF per post.");
        if (videos.Count > 1) errors.Add($"{name} allows only one video per post.");

        var kinds = (images.Count > 0 ? 1 : 0) + (gifs.Count > 0 ? 1 : 0) + (videos.Count > 0 ? 1 : 0);
        if (kinds > 1) errors.Add($"{name} can't combine images, GIFs and videos in one post.");

        return errors;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= MB => $"{bytes / (double)MB:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    private static string Join(string[] extensions) => string.Join("/", extensions.Select(e => e.TrimStart('.').ToUpperInvariant()));
}
