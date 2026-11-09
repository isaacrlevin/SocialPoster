using Microsoft.JSInterop;

namespace SocialPoster.MAUI;

/// <summary>
/// Native replacements for browser features a web view doesn't provide reliably: pop-up windows (WKWebView has
/// none), the async clipboard and file downloads. Called from wwwroot/js/maui.js.
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

    /// <summary>Writes <paramref name="text"/> to the user's Downloads folder without overwriting existing files.</summary>
    /// <returns>The full path of the saved file.</returns>
    [JSInvokable]
    public static async Task<string> SaveToDownloads(string fileName, string text)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            safeName = "SocialPoster-export.txt";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        var path = Path.Combine(folder, safeName);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
        await File.WriteAllTextAsync(path, text);
        return path;
    }
}
