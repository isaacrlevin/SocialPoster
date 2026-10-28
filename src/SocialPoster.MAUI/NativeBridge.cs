using Microsoft.JSInterop;

namespace SocialPoster.MAUI;

/// <summary>
/// Native replacements for browser features a web view doesn't provide reliably: pop-up windows (WKWebView has
/// none) and the async clipboard. Called from wwwroot/js/maui.js.
/// </summary>
public static class NativeBridge
{
    [JSInvokable]
    public static async Task<bool> OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        return await MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(uri));
    }

    [JSInvokable]
    public static Task CopyText(string text) =>
        MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
}
