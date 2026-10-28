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
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Same data folder, database and key ring as the SocialPoster.Web sample, so both apps share accounts,
        // contacts, history and scheduled posts.
        var dataDir = DefaultDataDirectory();
        // Data Protection's default discriminator reads IHostEnvironment.ContentRootPath, which MAUI doesn't implement.
        // Registered before AddSocialPosterCore (which adds Data Protection) so it replaces that default.
        builder.Services.AddSingleton<IApplicationDiscriminator>(new FixedApplicationDiscriminator("SocialPoster"));
        builder.Services.AddSocialPosterCore(options => options.DataDirectory = dataDir);
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

    private static string DefaultDataDirectory() =>
        OperatingSystem.IsMacCatalyst()
            // Match the Web sample on macOS. Mac Catalyst maps LocalApplicationData elsewhere.
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "SocialPoster")
            : new SocialPosterOptions().DataDirectory;

    private sealed class FixedApplicationDiscriminator(string discriminator) : IApplicationDiscriminator
    {
        public string Discriminator => discriminator;
    }
}
