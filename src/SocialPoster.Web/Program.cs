using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SocialPoster.Core;
using SocialPoster.Components;
using SocialPoster.Core.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSocialPosterCore(options =>
    builder.Configuration.GetSection("SocialPoster").Bind(options));
builder.Services.AddSocialPosterScheduling(options =>
    builder.Configuration.GetSection("SocialPoster:Scheduling").Bind(options));
builder.Services.AddSocialPosterSchedulingWorker();
builder.Services.AddSocialPosterComponents(options => options.BasePath = "");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// This standalone local host owns its key ring and identity. Library registration never changes them.
var dataDir = builder.Configuration["SocialPoster:DataDirectory"]
    ?? new SocialPosterOptions().DataDirectory;
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
await app.Services.InitializeSocialPosterAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<SocialPoster.Web.Components.App>().AddInteractiveServerRenderMode();

// `dotnet run` ignores launchSettings' launchBrowser, so the sample opens the browser itself once it is listening.
// Turn off with SocialPoster:LaunchBrowser=false (env: SocialPoster__LaunchBrowser=false).
if (app.Configuration.GetValue("SocialPoster:LaunchBrowser", true))
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(app));
app.Run();

static void OpenBrowser(WebApplication app)
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
        .OrderByDescending(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();
    if (address is null) return;

    // Wildcard bindings (http://+:5000, http://0.0.0.0:5000, http://[::]:5000) aren't browsable; use localhost.
    var uri = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
    if (uri.Host is "0.0.0.0" or "[::]") uri.Host = "localhost";
    var url = uri.Uri.ToString();
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not open a browser. Browse to {Url} manually.", url);
    }
}
