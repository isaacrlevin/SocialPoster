using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class ThreadsTests
{
    private static RenderedPost Post(string text = "Hello") => PostRenderer.Render(Platform.Threads, text, _ => null);

    [Fact]
    public void Platform_value_is_appended_and_mentions_use_threads_usernames()
    {
        Assert.Equal(4, (int)Platform.Threads);
        var contact = new Contact { DisplayName = "Jane", ThreadsUsername = "@jane.doe" };
        var rendered = PostRenderer.Render(Platform.Threads, "Hi {@jane}", _ => contact);
        Assert.Equal("Hi @jane.doe", rendered.PayloadText);
        Assert.Equal(500, rendered.Limit);
        Assert.Empty(rendered.Warnings);
        contact.ThreadsUsername = null;
        var plain = PostRenderer.Render(Platform.Threads, "Hi {@jane}", _ => contact);
        Assert.Equal("Hi Jane", plain.DisplayText);
        Assert.Single(plain.Warnings);
        Assert.Single(PostRenderer.Render(Platform.Threads, "{@nobody}", _ => null).Warnings);
    }

    [Fact]
    public void Posts_over_500_graphemes_are_over_limit_and_media_is_rejected()
    {
        Assert.False(Post(new string('a', 500)).IsOverLimit);
        Assert.True(Post(new string('a', 501)).IsOverLimit);
        Assert.Empty(MediaRules.Validate(Platform.Threads, []));
        var image = new MediaAttachment { FileName = "a.png", Kind = MediaKind.Image, ContentType = "image/png", Size = 10 };
        Assert.Single(MediaRules.Validate(Platform.Threads, [image]));
        Assert.NotEmpty(MediaRules.Hint(Platform.Threads));
    }

    [Fact]
    public async Task Configure_verifies_token_then_saves_it_with_the_reported_user_id()
    {
        await using var host = await Host.CreateAsync();
        host.Http.Add("""{"id":"123","username":"jane"}""");
        var account = await host.Client.ConfigureAsync("  test-only-token ");
        Assert.Equal(new ThreadsAccount("123", "jane"), account);
        var request = Assert.Single(host.Http.Requests);
        Assert.Equal("https://graph.threads.net/v1.0/me?fields=id,username", request.Url);
        Assert.Equal("Bearer test-only-token", request.Auth);
        Assert.Null(host.HttpClient.DefaultRequestHeaders.Authorization);
        Assert.True(await host.Client.IsConfiguredAsync());
        Assert.Equal("123", await host.Secrets.GetAsync(SettingKeys.ThreadsUserId));
        await using var db = host.CreateDbContext();
        Assert.All(await db.Settings.ToListAsync(), s => Assert.DoesNotContain("test-only-token", s.ProtectedValue));
    }

    [Fact]
    public async Task Failed_verification_saves_nothing_and_redacts_the_token()
    {
        await using var host = await Host.CreateAsync();
        host.Http.Add("""{"error":{"message":"Bad token test-only-token","code":190}}""", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.ConfigureAsync("test-only-token"));
        Assert.DoesNotContain("test-only-token", ex.Message);
        Assert.Contains("[redacted]", ex.Message);
        Assert.False(await host.Client.IsConfiguredAsync());
    }

    [Fact]
    public async Task Post_creates_a_text_container_publishes_it_and_returns_the_permalink()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add("""{"id":"container1"}""");
        host.Http.Add("""{"id":"thread9"}""");
        host.Http.Add("""{"id":"thread9","permalink":"https://www.threads.com/@jane/post/abc"}""");
        var url = await host.Client.PostAsync(Post("Hello & welcome"), []);
        Assert.Equal("https://www.threads.com/@jane/post/abc", url);
        Assert.Equal(3, host.Http.Requests.Count);
        Assert.Equal("https://graph.threads.net/v1.0/123/threads", host.Http.Requests[0].Url);
        Assert.Equal("media_type=TEXT&text=Hello+%26+welcome", host.Http.Requests[0].Body);
        Assert.Equal("https://graph.threads.net/v1.0/123/threads_publish", host.Http.Requests[1].Url);
        Assert.Equal("creation_id=container1", host.Http.Requests[1].Body);
        Assert.Equal("https://graph.threads.net/v1.0/thread9?fields=permalink", host.Http.Requests[2].Url);
        Assert.All(host.Http.Requests, r => Assert.Equal("Bearer original-token", r.Auth));
    }

    [Fact]
    public async Task Post_rejects_invalid_input_before_any_request()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(""), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(new string('a', 501)), []));
        var image = new MediaAttachment { FileName = "a.png", Kind = MediaKind.Image, ContentType = "image/png", Size = 10 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(), [image]));
        Assert.Empty(host.Http.Requests);
    }

    [Fact]
    public async Task Unconfigured_client_and_publish_service_report_a_clear_failure()
    {
        await using var host = await Host.CreateAsync();
        Assert.False((await host.Publisher.GetStatusAsync([Platform.Threads])).Threads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(), []));
        var results = await host.Publisher.PublishAsync("Hello", new Dictionary<Platform, string> { [Platform.Threads] = "Hello" });
        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("Connect Threads", result.Error);
        Assert.Empty(host.Http.Requests);
    }

    [Fact]
    public async Task Publish_service_posts_to_threads_and_reports_status()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        Assert.True((await host.Publisher.GetStatusAsync([Platform.Threads])).IsConnected(Platform.Threads));
        host.Http.Add("""{"id":"c"}""");
        host.Http.Add("""{"id":"t"}""");
        host.Http.Add("""{"permalink":"https://www.threads.com/@jane/post/xyz"}""");
        var result = Assert.Single(await host.Publisher.PublishAsync("Hello", new Dictionary<Platform, string> { [Platform.Threads] = "Hello" }));
        Assert.True(result.Success);
        Assert.Equal("https://www.threads.com/@jane/post/xyz", result.Url);
    }

    private sealed record Request(string Url, string? Auth, string? Body);

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = [];
        public List<Request> Requests { get; } = [];
        public void Add(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body) });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request; tests never use live network.");
            return responses.Dequeue();
        }
    }

    private sealed class Host(SqliteConnection connection, DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>, IAsyncDisposable
    {
        public FakeHttp Http { get; } = new();
        public HttpClient HttpClient { get; private set; } = null!;
        public SecretStore Secrets { get; private set; } = null!;
        public ThreadsClient Client { get; private set; } = null!;
        public PublishService Publisher { get; private set; } = null!;
        public AppDbContext CreateDbContext() => new(options);

        public static async Task<Host> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var host = new Host(connection, new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await using var db = host.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            host.Secrets = new(host, new EphemeralDataProtectionProvider());
            host.HttpClient = new(host.Http);
            host.Client = new(host.HttpClient, host.Secrets);
            var x = new XClient(host.HttpClient, host.Secrets);
            var bluesky = new BlueskyClient(host.HttpClient, host.Secrets);
            var contacts = new ContactService(host, bluesky, x, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContactService>.Instance);
            host.Publisher = new(contacts, new SocialPublisher(bluesky, x, new LinkedInClient(host.HttpClient, host.Secrets),
                new MastodonClient(host.HttpClient, host.Secrets), host.Client, host.Secrets), host);
            return host;
        }

        public Task ConfigureAsync() => Secrets.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.ThreadsUserId] = "123",
            [SettingKeys.ThreadsAccessToken] = "original-token",
        });

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await connection.DisposeAsync();
        }
    }
}
