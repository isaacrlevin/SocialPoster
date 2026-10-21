using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialPoster.Components;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class RegistrationTests
{
    [Fact]
    public async Task Initialization_upgrades_legacy_schema_without_losing_contacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"socialposter-tests-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection().AddSocialPosterCore(o => o.DataDirectory = directory);
            await using var provider = services.BuildServiceProvider();
            await provider.InitializeSocialPosterAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Contacts.Add(new Contact { Key = "legacy", DisplayName = "Legacy contact" });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    DROP TABLE "AccountSearches";
                    ALTER TABLE "Contacts" DROP COLUMN "XVerifiedByCopilotAtUtc";
                    ALTER TABLE "Contacts" DROP COLUMN "LinkedInVerifiedByCopilotAtUtc";
                    """);
            }
            await provider.InitializeSocialPosterAsync();
            await using var upgraded = await factory.CreateDbContextAsync();
            Assert.Equal("legacy", (await upgraded.Contacts.SingleAsync()).Key);
            Assert.Empty(await upgraded.AccountSearches.ToListAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Core_preserves_host_protection_and_initializes_dedicated_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"socialposter-tests-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName("ConsumerHost");
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSocialPosterCore(o =>
            {
                o.DataDirectory = directory;
                o.DatabaseFileName = "consumer.db";
                o.PostingTimeout = TimeSpan.FromMinutes(5);
            });
            Assert.False(Directory.Exists(directory));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            Assert.Equal("ConsumerHost", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
            Assert.False(Directory.Exists(directory));

            await provider.InitializeSocialPosterAsync();
            await provider.InitializeSocialPosterAsync();
            Assert.True(File.Exists(Path.Combine(directory, "consumer.db")));
            await using var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
            Assert.Empty(await db.Contacts.ToListAsync());
            Assert.Empty(await db.AccountSearches.ToListAsync());
            Assert.Empty(await db.PostHistory.ToListAsync());
            var secrets = provider.GetRequiredService<SecretStore>();
            await secrets.SetAsync("test", "not-a-real-credential");
            Assert.Equal("not-a-real-credential", await secrets.GetAsync("test"));
            Assert.NotEqual("not-a-real-credential", (await db.Settings.SingleAsync()).ProtectedValue);
            await using var scope = provider.CreateAsyncScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ContactService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublishService>());
            Assert.NotNull(provider.GetRequiredService<AccountFinder>());
            Assert.NotNull(provider.GetRequiredService<AccountSearchService>());
            using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(XClient));
            Assert.Equal(TimeSpan.FromMinutes(5), http.Timeout);
            Assert.Contains("SocialPoster/1.0", http.DefaultRequestHeaders.UserAgent.ToString());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("nested/file.db")]
    [InlineData("..")]
    public void Core_rejects_invalid_database_file_names(string fileName)
    {
        var services = new ServiceCollection().AddSocialPosterCore(o => o.DatabaseFileName = fileName);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SocialPosterOptions>>().Value);
    }

    [Fact]
    public void Core_rejects_empty_directory_and_invalid_timeout()
    {
        var services = new ServiceCollection().AddSocialPosterCore(o =>
        {
            o.DataDirectory = " ";
            o.PostingTimeout = TimeSpan.Zero;
        });
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SocialPosterOptions>>().Value);
        Assert.Equal(2, error.Failures.Count());
    }

    [Theory]
    [InlineData("", "./", "contacts")]
    [InlineData("socialposter", "socialposter", "socialposter/contacts")]
    [InlineData("tools/poster", "tools/poster", "tools/poster/contacts")]
    public void Components_use_base_relative_navigation(string basePath, string compose, string contacts)
    {
        var services = new ServiceCollection().AddSocialPosterComponents(o => o.BasePath = basePath);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var navigation = scope.ServiceProvider.GetRequiredService<SocialPosterNavigation>();
        Assert.Equal(compose, navigation.Link());
        Assert.Equal(contacts, navigation.Link("contacts"));
    }

    [Theory]
    [InlineData("/socialposter")]
    [InlineData("socialposter/")]
    [InlineData("https://example.org")]
    [InlineData("../poster")]
    public void Components_reject_non_relative_mount_paths(string basePath)
    {
        var services = new ServiceCollection().AddSocialPosterComponents(o => o.BasePath = basePath);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SocialPosterComponentOptions>>().Value);
    }

    [Fact]
    public void Library_routes_are_opt_in_and_prefixed_without_host_error_or_root_routes()
    {
        var routes = typeof(SocialPosterPage).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>())
            .Select(a => a.Template).Order().ToArray();
        Assert.Equal(["/socialposter", "/socialposter/{Section}"], routes);
        Assert.DoesNotContain(typeof(SocialPosterPage).Assembly.GetReferencedAssemblies(), a => a.Name == "SocialPoster.Web");
    }
}
