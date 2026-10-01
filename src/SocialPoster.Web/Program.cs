using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SocialPoster.Web.Components;
using SocialPoster.Web.Data;
using SocialPoster.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["SocialPoster:DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocialPoster");
Directory.CreateDirectory(dataDir);
builder.Configuration["SocialPoster:DataDirectory"] = dataDir;
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
if (!OperatingSystem.IsWindows())
{
    // No DPAPI here, so the key ring is only protected by file permissions: keep it owner-only.
    File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.SetUnixFileMode(keysDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("SocialPoster")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "socialposter.db")}"));

builder.Services.AddSingleton<SecretStore>();
// Media uploads can be hundreds of MB, so the default 100s timeout is far too short for the posting clients.
void ConfigurePostingClient(HttpClient c)
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SocialPoster/1.0");
    c.Timeout = TimeSpan.FromMinutes(30);
}

builder.Services.AddHttpClient<BlueskyClient>(ConfigurePostingClient)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
builder.Services.AddHttpClient<XClient>(ConfigurePostingClient);
builder.Services.AddHttpClient<LinkedInClient>(ConfigurePostingClient);
builder.Services.AddScoped<ContactService>();
builder.Services.AddScoped<PublishService>();
builder.Services.AddSingleton<AccountFinder>();
builder.Services.AddSingleton<AccountSearchService>();

builder.Services.AddMudServices();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await db.UpgradeSchemaAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
