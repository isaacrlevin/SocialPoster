using SocialPoster.Core;

namespace SocialPoster.Components;

public static class PlatformInfo
{
    public static string Label(Platform p) => p switch
    {
        Platform.X => "X",
        Platform.Bluesky => "Bluesky",
        Platform.LinkedIn => "LinkedIn",
        Platform.Mastodon => "Mastodon",
        Platform.Threads => "Threads",
        _ => p.ToString(),
    };

    public static string Icon(Platform p) => p switch
    {
        Platform.X => "𝕏",
        Platform.Bluesky => "🦋",
        Platform.LinkedIn => "in",
        Platform.Mastodon => "Ⓜ",
        Platform.Threads => "@",
        _ => "",
    };
}
