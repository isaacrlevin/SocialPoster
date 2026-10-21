using Microsoft.AspNetCore.DataProtection;
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
app.Run();
