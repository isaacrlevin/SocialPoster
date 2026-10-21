using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Core;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

/// <summary>The SocialPoster.Posting package must work without the app's database, UI or Copilot dependencies.</summary>
public class PostingPackageTests
{
    [Fact]
    public void Package_does_not_reference_storage_web_or_copilot_assemblies()
    {
        var references = typeof(SocialPublisher).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("GitHub.Copilot", StringComparison.Ordinal));
        Assert.NotEqual(typeof(SocialPublisher).Assembly, typeof(PublishService).Assembly);
    }

    [Fact]
    public async Task AddSocialPosting_alone_resolves_the_publisher_with_an_in_memory_store()
    {
        await using var provider = new ServiceCollection().AddSocialPosting().BuildServiceProvider(validateScopes: true);
        Assert.IsType<InMemoryCredentialStore>(provider.GetRequiredService<ICredentialStore>());
        var publisher = provider.GetRequiredService<SocialPublisher>();
        var status = await publisher.GetStatusAsync();
        Assert.All(Enum.GetValues<Platform>(), p => Assert.False(status.IsConnected(p)));
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[] { nameof(BlueskyClient), nameof(XClient), nameof(LinkedInClient), nameof(MastodonClient), nameof(ThreadsClient) })
            using (factory.CreateClient(name)) { }
    }

    [Fact]
    public async Task A_host_supplied_credential_store_wins_over_the_default()
    {
        var store = new InMemoryCredentialStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<ICredentialStore>(store).AddSocialPosting().BuildServiceProvider();
        Assert.Same(store, provider.GetRequiredService<ICredentialStore>());
        await store.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.ThreadsUserId] = "1",
            [SettingKeys.ThreadsAccessToken] = "token",
        });
        var status = await provider.GetRequiredService<SocialPublisher>().GetStatusAsync([Platform.Threads, Platform.Bluesky]);
        Assert.True(status.Threads);
        Assert.False(status.Bluesky);
        Assert.False((await provider.GetRequiredService<SocialPublisher>().GetStatusAsync([Platform.Bluesky])).Threads);
    }

    [Fact]
    public async Task Unconfigured_platforms_and_invalid_posts_fail_per_platform_without_throwing()
    {
        await using var provider = new ServiceCollection().AddSocialPosting().BuildServiceProvider();
        var publisher = provider.GetRequiredService<SocialPublisher>();
        var results = await publisher.PublishTextAsync("Hello", [Platform.Mastodon, Platform.Threads, Platform.Mastodon]);
        Assert.Equal([Platform.Mastodon, Platform.Threads], results.Select(r => r.Platform));
        Assert.All(results, r => Assert.False(r.Success));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));

        var tooLong = await publisher.PublishTextAsync(new string('a', 301), [Platform.Bluesky]);
        Assert.Contains("301/300", Assert.Single(tooLong).Error);
        var empty = await publisher.PublishTextAsync("  ", [Platform.X]);
        Assert.Equal("Post is empty.", Assert.Single(empty).Error);
    }

    [Fact]
    public async Task X_browser_intent_needs_no_credentials_and_is_reported_as_opening_in_a_browser()
    {
        var store = new InMemoryCredentialStore([new(SettingKeys.XPostMethod, "intent")]);
        await using var provider = new ServiceCollection()
            .AddSingleton<ICredentialStore>(store).AddSocialPosting().BuildServiceProvider();
        var publisher = provider.GetRequiredService<SocialPublisher>();
        Assert.True((await publisher.GetStatusAsync([Platform.X])).X);
        var result = Assert.Single(await publisher.PublishTextAsync("Hi there", [Platform.X]));
        Assert.True(result.Success);
        Assert.True(result.OpensInBrowser);
        Assert.StartsWith("https://", result.Url);
    }

    [Fact]
    public async Task Mentions_resolve_through_the_supplied_resolver()
    {
        await using var provider = new ServiceCollection().AddSocialPosting().BuildServiceProvider();
        var contact = new Contact { Key = "jane", DisplayName = "Jane", ThreadsUsername = "jane.doe" };
        var results = await provider.GetRequiredService<SocialPublisher>().PublishAsync(
            new Dictionary<Platform, string> { [Platform.Threads] = "Hi {@jane} " + new string('a', 495) },
            key => key == "jane" ? contact : null);
        // "Hi @jane.doe " is 13 graphemes, so this is over the 500 limit only if the mention was resolved to its username.
        Assert.Contains("508/500", Assert.Single(results).Error);
    }

    [Fact]
    public async Task In_memory_store_overwrites_removes_and_snapshots()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("a", "1");
        await store.SetManyAsync(new Dictionary<string, string?> { ["b"] = "2", ["a"] = "3" });
        Assert.Equal("3", await store.GetAsync("a"));
        var snapshot = await store.GetSnapshotAsync(["a", "b", "missing"]);
        Assert.Equal("2", snapshot["b"]);
        Assert.Null(snapshot["missing"]);
        await store.SetAsync("a", "");
        Assert.Null(await store.GetAsync("a"));
        Assert.Equal("2", (await store.GetManyAsync("b"))["b"]);
    }

    [Fact]
    public async Task App_registration_routes_the_posting_clients_through_the_encrypted_secret_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "socialposter-posting-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection().AddSocialPosterCore(o => o.DataDirectory = directory);
            await using var provider = services.BuildServiceProvider();
            await provider.InitializeSocialPosterAsync();
            Assert.Same(provider.GetRequiredService<SecretStore>(), provider.GetRequiredService<ICredentialStore>());
            await using var scope = provider.CreateAsyncScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SocialPublisher>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublishService>());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
