using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Infrastructure;
using Microsoft.Extensions.Logging;
using SocialPoster.Components;
using SocialPoster.Core;
using SocialPoster.Core.Scheduling;

namespace SocialPoster.MAUI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        try
        {
            return CreateMauiAppCore();
        }
        catch (Exception ex)
        {
            // An exception here kills the app with an opaque native crash report, so log it and tell the user first.
            StartupDiagnostics.Report(ex);
            throw;
        }
    }

    private static MauiApp CreateMauiAppCore()
    {
        StartupDiagnostics.EnsureRequirements();

        if (OperatingSystem.IsMacCatalyst()) UseInstalledCopilotCli();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Same data folder, database and key ring as the SocialPoster.Web sample, so both apps share accounts,
        // contacts, history and scheduled posts. The Store build is the exception (see DefaultDataDirectory).
        var dataDir = DefaultDataDirectory();
        // Data Protection's default discriminator reads IHostEnvironment.ContentRootPath, which MAUI doesn't implement.
        // Registered before AddSocialPosterCore (which adds Data Protection) so it replaces that default.
        builder.Services.AddSingleton<IApplicationDiscriminator>(new FixedApplicationDiscriminator("SocialPoster"));
        builder.Services.AddSocialPosterCore(options =>
        {
            options.DataDirectory = dataDir;
            options.Location = HostLocation.Local; // desktop apps always run on the user's own computer
        });
        builder.Services.AddSocialPosterScheduling();
        builder.Services.AddSocialPosterSchedulingWorker(); // started by App, since MAUI doesn't run hosted services
        builder.Services.AddSocialPosterComponents(options => options.BasePath = "");

        var keysDir = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(keysDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(keysDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var protection = builder.Services.AddDataProtection()
            .SetApplicationName("SocialPoster")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();

        var app = builder.Build();
        // Off the UI thread: blocking it on an async continuation that wants the UI thread would deadlock.
        Task.Run(() => app.Services.InitializeSocialPosterAsync()).GetAwaiter().GetResult();
        return app;
    }

    private static string DefaultDataDirectory()
    {
        // Match the Web sample on macOS. Mac Catalyst maps LocalApplicationData elsewhere.
        if (OperatingSystem.IsMacCatalyst())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "SocialPoster");

        var shared = new SocialPosterOptions().DataDirectory;
#if WINDOWS
        // The Microsoft Store (MSIX) build keeps its data in its own package folder, which Windows removes on
        // uninstall, rather than in the shared %LOCALAPPDATA%\SocialPoster (writing there would need the restricted
        // unvirtualizedResources capability). On first run it copies an existing desktop install's data over once.
        if (AppInfo.Current.PackagingModel == AppPackagingModel.Packaged)
        {
            var packaged = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "SocialPoster");
            ImportOnce(shared, packaged);
            return packaged;
        }
#endif
        return shared;
    }

#if WINDOWS
    private static void ImportOnce(string source, string target)
    {
        if (Directory.Exists(target) || !Directory.Exists(source)) return;
        var staging = target + ".import";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            if (Directory.Exists(staging)) Directory.Move(staging, target);
        }
        catch
        {
            // Importing is a convenience: if it fails (say the desktop app has the database open), start fresh.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }
#endif

    // The Mac app doesn't bundle the Copilot CLI (see the csproj), so point the SDK at an installed one. Apps started
    // from Finder get a minimal PATH, so look in the usual Homebrew/npm locations and add the CLI's folder to PATH
    // (the npm launcher runs `node` from there). Without a CLI, the agent reports it couldn't start Copilot.
    private static void UseInstalledCopilotCli()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"))) return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] knownDirs =
        [
            "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".npm-global", "bin"), Path.Combine(home, ".volta", "bin")
        ];
        var pathDirs =
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        var cli = pathDirs.Concat(knownDirs).Select(dir => Path.Combine(dir, "copilot")).FirstOrDefault(File.Exists);
        if (cli is null) return;

        Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", cli);
        var cliDir = Path.GetDirectoryName(cli)!;
        if (!pathDirs.Contains(cliDir))
            Environment.SetEnvironmentVariable("PATH", string.Join(':', pathDirs.Prepend(cliDir)));
    }

    private sealed class FixedApplicationDiscriminator(string discriminator) : IApplicationDiscriminator
    {
        public string Discriminator => discriminator;
    }
}