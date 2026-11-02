using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SocialPoster.MAUI;

/// <summary>
/// Startup failures happen before any window exists, so MAUI can't show them and the OS only reports an opaque native
/// crash. This checks the hard requirements, writes failures to a log and tells the user in a native dialog.
/// </summary>
internal static class StartupDiagnostics
{
    private const string Title = "SocialPoster can't start";

    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsMacCatalyst() ? Path.Combine("Library", "Logs", "SocialPoster") : ".socialposter");

    /// <summary>Throws a <see cref="RequirementException"/> with instructions when a required component is missing.</summary>
    public static void EnsureRequirements()
    {
#if WINDOWS
        if (!WebView2RuntimeInstalled())
            throw new RequirementException(
                "SocialPoster needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n" +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ (the Evergreen " +
                "Bootstrapper), then start SocialPoster again.");
#endif
    }

    /// <summary>Logs a startup failure and shows it to the user. Never throws.</summary>
    public static void Report(Exception ex)
    {
        var logPath = TryLog(ex);
        var message = ex is RequirementException
            ? ex.Message
            : $"SocialPoster hit an unexpected error while starting:\n\n{ex.GetBaseException().Message}" +
              (logPath is null ? "" : $"\n\nDetails were saved to:\n{logPath}\n\nPlease include that file when reporting the problem.");
        TryShowDialog(message);
    }

    private static string? TryLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, "startup-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null; // logging must never mask the original failure
        }
    }

    private static void TryShowDialog(string message)
    {
        try
        {
            if (OperatingSystem.IsMacCatalyst())
            {
                // No window yet, so use AppleScript's dialog (Process.Start works in the unsandboxed app).
                var script = $"display dialog \"{message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\" " +
                             $"with title \"{Title}\" buttons {{\"OK\"}} default button \"OK\" with icon stop";
                var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                info.ArgumentList.Add("-e");
                // AppleScript treats \n inside a string literal as a newline.
                info.ArgumentList.Add(script);
                using var process = Process.Start(info);
                process?.WaitForExit(120_000);
            }
            else if (OperatingSystem.IsWindows())
            {
                MessageBoxW(IntPtr.Zero, message, Title, 0x10 /* MB_ICONERROR */);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

#if WINDOWS
    // The WebView2 Evergreen runtime registers its version under this client GUID, per machine or per user.
    private static bool WebView2RuntimeInstalled()
    {
        const string clientKey = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        string[] keys = [@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", clientKey];
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var key in keys)
        {
            using var subKey = hive.OpenSubKey(key);
            if (subKey?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
        }
        return false;
    }
#endif

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal sealed class RequirementException(string message) : Exception(message);
}
