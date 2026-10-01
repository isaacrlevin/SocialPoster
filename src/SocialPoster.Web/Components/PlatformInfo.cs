using SocialPoster.Core;

namespace SocialPoster.Web.Components;

public static class PlatformInfo
{
    public static string Label(Platform p) => p switch
    {
        Platform.X => "X",
        Platform.Bluesky => "Bluesky",
        Platform.LinkedIn => "LinkedIn",
        _ => p.ToString(),
    };

    public static string Icon(Platform p) => p switch
    {
        Platform.X => "𝕏",
        Platform.Bluesky => "🦋",
        Platform.LinkedIn => "in",
        _ => "",
    };
}
