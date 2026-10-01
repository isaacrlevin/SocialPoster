namespace SocialPoster.Core;

/// <summary>
/// Builds an X (Twitter) "intent" URL that opens the web composer with prefilled text. Anyone can use this,
/// even without X API access: the user still has to click Post themselves, but nothing goes through the paid API.
/// </summary>
public static class XIntent
{
    public static string BuildUrl(string text) => $"https://x.com/intent/post?text={Uri.EscapeDataString(text)}";
}
