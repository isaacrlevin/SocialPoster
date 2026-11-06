using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SocialPoster.Components.Pages;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class AgentSettingsStoreTests
{
    private static AgentProvider Provider(string name = "Local", string url = "http://localhost:11434/v1", string? key = null, string model = "llama3.1") =>
        new("", name, url, key, model);

    private static async Task<(AgentSettingsStore Store, SecretStore Secrets, IAsyncDisposable Db)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new SingleConnectionFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var secrets = new SecretStore(factory, new EphemeralDataProtectionProvider());
        return (new AgentSettingsStore(secrets), secrets, connection);
    }

    private sealed class SingleConnectionFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Defaults_to_copilot_with_no_providers_and_search_off()
    {
        var (store, _, db) = await CreateAsync();
        await using var _ = db;
        Assert.Equal(AgentSettingsStore.CopilotId, await store.GetActiveIdAsync());
        Assert.Empty(await store.GetProvidersAsync());
        Assert.Null(await store.GetActiveProviderAsync());
        Assert.Equal(SearchKind.None, (await store.GetSearchAsync()).Kind);
    }

    [Fact]
    public async Task Providers_are_added_edited_selected_and_removed_and_keys_stay_encrypted()
    {
        var (store, secrets, db) = await CreateAsync();
        await using var _ = db;
        var openAi = await store.SaveProviderAsync(Provider("OpenAI", "https://api.openai.com/v1/", "sk-test-secret", "gpt-4o-mini"));
        var local = await store.SaveProviderAsync(Provider("Ollama"));
        Assert.NotEqual(openAi.Id, local.Id);
        Assert.Equal("https://api.openai.com/v1", openAi.BaseUrl);
        Assert.Equal(2, (await store.GetProvidersAsync()).Count);

        await store.SetActiveAsync(openAi.Id);
        Assert.Equal("gpt-4o-mini", (await store.GetActiveProviderAsync())!.Model);

        // Editing with a blank key keeps the saved key; the id is stable.
        var edited = await store.SaveProviderAsync(openAi with { ApiKey = "", Model = "gpt-4.1-mini" });
        Assert.Equal(openAi.Id, edited.Id);
        Assert.Equal("sk-test-secret", edited.ApiKey);
        Assert.Equal(2, (await store.GetProvidersAsync()).Count);

        await using (var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(((SqliteConnection)db)).Options))
            Assert.All(await context.Settings.ToListAsync(), s => Assert.DoesNotContain("sk-test-secret", s.ProtectedValue));

        await store.RemoveProviderAsync(openAi.Id);
        Assert.Equal(AgentSettingsStore.CopilotId, await store.GetActiveIdAsync());
        Assert.Null(await secrets.GetAsync(AgentSettingKeys.ActiveProvider));
        Assert.Equal("Ollama", Assert.Single(await store.GetProvidersAsync()).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetActiveAsync("missing"));
        await store.SetActiveAsync(AgentSettingsStore.CopilotId);
    }

    [Theory]
    [InlineData("", "https://api.openai.com/v1", "m")]
    [InlineData("GitHub Copilot", "https://api.openai.com/v1", "m")]
    [InlineData("X", "https://api.openai.com/v1", "")]
    [InlineData("X", "not a url", "m")]
    [InlineData("X", "ftp://example.com/v1", "m")]
    [InlineData("X", "https://user:pass@example.com/v1", "m")]
    [InlineData("X", "https://example.com/v1?key=1", "m")]
    [InlineData("X", "http://api.example.com/v1", "m")]
    [InlineData("X", "http://8.8.8.8/v1", "m")]
    public void Invalid_providers_are_rejected(string name, string url, string model) =>
        Assert.NotNull(AgentSettingsStore.Validate(Provider(name, url, null, model)));

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://192.168.1.20:11434/v1")]
    [InlineData("http://ollama:11434/v1")]
    [InlineData("http://box.local:11434/v1")]
    public void Https_or_local_http_endpoints_are_accepted(string url) =>
        Assert.Null(AgentSettingsStore.Validate(Provider("Ok", url)));

    [Fact]
    public async Task Duplicate_names_and_whitespace_keys_are_rejected()
    {
        var (store, _, db) = await CreateAsync();
        await using var _ = db;
        await store.SaveProviderAsync(Provider("Mine"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProviderAsync(Provider("mine")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProviderAsync(Provider("Other", key: "has space")));
        Assert.Single(await store.GetProvidersAsync());
    }

    [Fact]
    public async Task Search_settings_validate_keep_keys_and_clear()
    {
        var (store, _, db) = await CreateAsync();
        await using var _ = db;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSearchAsync(new(SearchKind.Brave)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSearchAsync(new(SearchKind.SearXNG, "http://search.example.com")));
        await store.SaveSearchAsync(new(SearchKind.Brave, null, "brave-key"));
        var kept = await store.SaveSearchAsync(new(SearchKind.Brave, null, ""));
        Assert.Equal("brave-key", kept.ApiKey);
        var searx = await store.SaveSearchAsync(new(SearchKind.SearXNG, "http://localhost:8080/", "ignored"));
        Assert.Null(searx.ApiKey);
        Assert.Equal("http://localhost:8080/", (await store.GetSearchAsync()).Endpoint);
        await store.SaveSearchAsync(SearchSettings.Disabled);
        Assert.Equal(SearchKind.None, (await store.GetSearchAsync()).Kind);
    }

    [Fact]
    public async Task Corrupt_saved_json_is_ignored_instead_of_crashing_the_page()
    {
        var (store, secrets, db) = await CreateAsync();
        await using var _ = db;
        await secrets.SetAsync(AgentSettingKeys.Providers, "{not json");
        await secrets.SetAsync(AgentSettingKeys.Search, "[1,2");
        await secrets.SetAsync(AgentSettingKeys.ActiveProvider, "gone");
        Assert.Empty(await store.GetProvidersAsync());
        Assert.Equal(SearchKind.None, (await store.GetSearchAsync()).Kind);
        Assert.Equal(AgentSettingsStore.CopilotId, await store.GetActiveIdAsync());
    }

    [Theory]
    [InlineData("https://my-res.services.ai.azure.com", "gpt-4.1", "https://my-res.services.ai.azure.com/openai/v1", "gpt-4.1")]
    [InlineData("https://my-res.services.ai.azure.com/api/projects/proj1", "gpt-4.1", "https://my-res.services.ai.azure.com/openai/v1", "gpt-4.1")]
    [InlineData("https://My-Res.openai.azure.com/", "dep", "https://my-res.openai.azure.com/openai/v1", "dep")]
    [InlineData("https://my-res.cognitiveservices.azure.com/openai/deployments/gpt-4o-mini/chat/completions?api-version=2025-01-01-preview", "", "https://my-res.cognitiveservices.azure.com/openai/v1", "gpt-4o-mini")]
    [InlineData("https://my-res.openai.azure.com/openai/deployments/a/chat/completions?api-version=1", "typed", "https://my-res.openai.azure.com/openai/v1", "typed")]
    public void Foundry_endpoints_become_the_v1_api_and_fill_the_deployment(string url, string model, string expectedUrl, string expectedModel)
    {
        var p = AgentSettingsStore.Normalize(Provider("Foundry", url, "key", model));
        Assert.Equal(expectedUrl, p.BaseUrl);
        Assert.Equal(expectedModel, p.Model);
        Assert.Null(AgentSettingsStore.Validate(p));
    }

    [Fact]
    public void Foundry_needs_a_key_and_other_urls_are_untouched()
    {
        Assert.NotNull(AgentSettingsStore.Validate(Provider("Foundry", "https://my-res.services.ai.azure.com", null, "dep")));
        Assert.Equal("https://api.openai.com/v1", AgentSettingsStore.Normalize(Provider("O", "https://api.openai.com/v1/")).BaseUrl);
        Assert.Null(AgentSettingsStore.FoundryHost("https://evil.example.com/x.openai.azure.com"));
    }

    [Fact]
    public void Presets_are_valid_starting_points_except_the_blank_ones()
    {
        Assert.Contains(AgentProviderPresets.All, p => p.Name.StartsWith("Ollama") && !p.NeedsApiKey);
        foreach (var preset in AgentProviderPresets.All.Where(p => p.BaseUrl.Length > 0 && !p.BaseUrl.Contains("YOUR-")))
            Assert.Null(AgentSettingsStore.Validate(Provider("Preset", preset.BaseUrl, null, preset.ModelExample.Length > 0 ? preset.ModelExample : "m")));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("https://localhost:1234/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://192.168.1.20:11434/v1")]
    [InlineData("http://169.254.169.254/v1")]
    [InlineData("http://ollama:11434/v1")]
    public void Hosted_installs_refuse_localhost_and_private_network_endpoints(string url)
    {
        Assert.NotNull(AgentSettingsStore.Validate(Provider("Local", url), allowLocal: false));
        Assert.NotNull(AgentSettingsStore.Validate(new SearchSettings(SearchKind.SearXNG, url), allowLocal: false));
    }

    [Fact]
    public void Hosted_installs_still_accept_public_https_endpoints_and_hide_the_local_presets()
    {
        Assert.Null(AgentSettingsStore.Validate(Provider("OpenAI", "https://api.openai.com/v1"), allowLocal: false));
        Assert.Null(AgentSettingsStore.Validate(new SearchSettings(SearchKind.SearXNG, "https://searx.example.com"), allowLocal: false));
        Assert.DoesNotContain(AgentProviderPresets.For(false), p => p.RequiresLocalHost);
        Assert.Contains(AgentProviderPresets.For(false), p => p.Name == "Custom");
        Assert.Equal(AgentProviderPresets.All.Count, AgentProviderPresets.For(true).Count);
    }

    [Fact]
    public async Task A_remote_store_refuses_to_save_a_local_provider()
    {
        var (_, secrets, db) = await CreateAsync();
        await using var _ = db;
        var remote = new AgentSettingsStore(secrets, new HostLocality(Microsoft.Extensions.Options.Options.Create(new SocialPosterOptions { Location = HostLocation.Remote })));
        Assert.False(remote.AllowLocal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => remote.SaveProviderAsync(Provider("Ollama")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => remote.SaveSearchAsync(new(SearchKind.SearXNG, "http://localhost:8080")));
        Assert.Empty(await remote.GetProvidersAsync());
        await remote.SaveProviderAsync(Provider("OpenAI", "https://api.openai.com/v1", "k", "gpt-4o-mini"));
    }
}

public class HostLocalityTests
{
    private static Func<string, string?> Env(params string[] set) => name => set.Contains(name) ? "1" : null;

    [Fact]
    public void Explicit_location_wins_over_detection()
    {
        Assert.True(HostLocality.Resolve(HostLocation.Local, Env("DOTNET_RUNNING_IN_CONTAINER")));
        Assert.False(HostLocality.Resolve(HostLocation.Remote, Env()));
    }

    [Theory]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    [InlineData("KUBERNETES_SERVICE_HOST")]
    [InlineData("WEBSITE_SITE_NAME")]
    public void Auto_treats_containers_and_cloud_hosts_as_remote(string marker) =>
        Assert.False(HostLocality.Resolve(HostLocation.Auto, Env(marker)));

    [Fact]
    public void Auto_treats_a_plain_machine_as_local() =>
        Assert.True(HostLocality.Resolve(HostLocation.Auto, Env()));
}

public class SafeWebFetcherTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("2002:7f00:1::", false)]
    public void Only_public_addresses_are_allowed(string address, bool expected) =>
        Assert.Equal(expected, SafeWebFetcher.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("not a url", "not a valid")]
    [InlineData("file:///etc/passwd", "only http and https")]
    [InlineData("ftp://example.com/x", "only http and https")]
    [InlineData("https://user:pw@example.com/", "credentials")]
    [InlineData("http://example.com:8080/", "port")]
    public async Task Bad_urls_are_refused_without_any_network_access(string url, string reason)
    {
        using var fetcher = new SafeWebFetcher();
        var text = await fetcher.FetchAsync(url);
        Assert.StartsWith("Error:", text);
        Assert.Contains(reason, text);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.1/")]
    public async Task Private_and_local_hosts_are_blocked_at_connect_time(string url)
    {
        using var fetcher = new SafeWebFetcher();
        var text = await fetcher.FetchAsync(url);
        Assert.StartsWith("Error:", text);
        Assert.Contains("private or local", text);
    }

    [Fact]
    public async Task Pages_are_read_as_text_and_failures_come_back_as_text()
    {
        await using var server = new TinyServer(path => path switch
        {
            "/page" => ("200 OK", "text/html; charset=utf-8", "<html><head><title>Jane Doe</title><meta name=\"description\" content=\"CEO of Contoso\"></head><body><script>evil()</script><p>Hello &amp; welcome</p></body></html>", null),
            "/missing" => ("404 Not Found", "text/html", "nope", null),
            "/json" => ("200 OK", "application/json", "{\"a\":1}", null),
            "/image" => ("200 OK", "image/png", "xx", null),
            "/old" => ("301 Moved Permanently", "text/plain", "", "/page"),
            "/loop" => ("302 Found", "text/plain", "", "/loop"),
            _ => ("404 Not Found", "text/plain", "", null),
        });
        using var fetcher = new SafeWebFetcher(allowPrivateNetworks: true);

        var page = await fetcher.FetchAsync(server.Url("/page"));
        Assert.Contains("Title: Jane Doe", page);
        Assert.Contains("description: CEO of Contoso", page);
        Assert.Contains("Hello & welcome", page);
        Assert.DoesNotContain("evil()", page);

        Assert.StartsWith("HTTP 404", await fetcher.FetchAsync(server.Url("/missing")));
        Assert.Equal("{\"a\":1}", await fetcher.FetchAsync(server.Url("/json")));
        Assert.Contains("image/png", await fetcher.FetchAsync(server.Url("/image")));
        Assert.Contains("Jane Doe", await fetcher.FetchAsync(server.Url("/old")));
        Assert.Contains("too many redirects", await fetcher.FetchAsync(server.Url("/loop")));
    }

    [Fact]
    public async Task Huge_pages_are_truncated()
    {
        await using var server = new TinyServer(_ => ("200 OK", "text/plain", new string('a', 2_000_000), null));
        using var fetcher = new SafeWebFetcher(allowPrivateNetworks: true);
        var text = await fetcher.FetchAsync(server.Url("/"));
        Assert.True(text.Length <= SafeWebFetcher.MaxTextChars + 20);
        Assert.EndsWith("[truncated]", text);
    }

    [Fact]
    public void Html_is_reduced_to_title_meta_and_visible_text()
    {
        var text = SafeWebFetcher.HtmlToText("""
            <html><head><title>  A &amp; B </title>
            <meta property="og:description" content="Bio &quot;here&quot;">
            <meta name="viewport" content="width=device-width">
            <style>.x{color:red}</style></head>
            <body><!-- hidden --><h1>Heading</h1><p>One</p><p>Two <b>bold</b></p><script>alert(1)</script></body></html>
            """);
        Assert.StartsWith("Title: A & B", text);
        Assert.Contains("og:description: Bio \"here\"", text);
        Assert.DoesNotContain("viewport", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("Heading\nOne\nTwo bold", text);
    }

    /// <summary>Just enough HTTP to exercise the fetcher without touching the internet.</summary>
    private sealed class TinyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public TinyServer(Func<string, (string Status, string Type, string Body, string? Location)> route)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var stream = c.GetStream();
                        var buffer = new byte[4096];
                        var read = await stream.ReadAsync(buffer);
                        var path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                        var (status, type, body, location) = route(path);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                                   (location is null ? "" : $"Location: {location}\r\n") + "Connection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                        try { await stream.WriteAsync(bytes); } catch (IOException) { /* client stopped reading a huge body */ }
                    });
                }
            });
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }
    }
}

public class WebSearchTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Auth, string? Token, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Subscription-Token", out var t) ? t.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Brave_sends_the_token_header_and_cleans_snippets()
    {
        var handler = new Handler(_ => Json("""{"web":{"results":[{"title":"Jane <b>Doe</b>","url":"https://example.com/jane","description":"CEO &amp; founder <strong>here</strong>"},{"title":"bad","url":"javascript:alert(1)"}]}}"""));
        var results = await WebSearch.Create(new(SearchKind.Brave, null, "brave-key"), new HttpClient(handler))!.SearchAsync("jane doe x", 5);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("brave-key", request.Token);
        Assert.StartsWith("https://api.search.brave.com/res/v1/web/search?count=5&q=jane%20doe%20x", request.Url);
        var result = Assert.Single(results);
        Assert.Equal("Jane Doe", result.Title);
        Assert.Equal("CEO & founder here", result.Snippet);
    }

    [Fact]
    public async Task Tavily_posts_json_with_a_bearer_key()
    {
        var handler = new Handler(_ => Json("""{"results":[{"title":"T","url":"https://example.com/t","content":"C"}]}"""));
        var results = await WebSearch.Create(new(SearchKind.Tavily, null, "tvly-key"), new HttpClient(handler))!.SearchAsync("q", 3);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer tvly-key", request.Auth);
        Assert.Equal("https://api.tavily.com/search", request.Url);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("q", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal("C", Assert.Single(results).Snippet);
    }

    [Fact]
    public async Task Searxng_uses_the_instance_url_and_json_format()
    {
        var handler = new Handler(_ => Json("""{"results":[{"title":"S","url":"https://example.com/s","content":"c"}]}"""));
        var results = await WebSearch.Create(new(SearchKind.SearXNG, "http://localhost:8080/"), new HttpClient(handler))!.SearchAsync("a b", 8);
        Assert.Equal("http://localhost:8080/search?format=json&q=a%20b", Assert.Single(handler.Requests).Url);
        Assert.Single(results);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Check the API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "quota")]
    public async Task Service_errors_name_the_service_and_never_include_the_key(HttpStatusCode status, string advice)
    {
        var handler = new Handler(_ => Json("""{"error":"secret-key-echo"}""", status));
        var search = WebSearch.Create(new(SearchKind.Brave, null, "secret-key-echo"), new HttpClient(handler))!;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync("q", 1));
        Assert.Contains("Brave Search", ex.Message);
        Assert.Contains(advice, ex.Message);
        Assert.DoesNotContain("secret-key-echo", ex.Message);
    }

    [Fact]
    public async Task Non_json_responses_and_unreachable_services_give_actionable_errors()
    {
        var html = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebSearch.Create(new(SearchKind.SearXNG, "http://localhost:8080"), new HttpClient(html))!.SearchAsync("q", 1));
        Assert.Contains("isn't JSON", ex.Message);

        var down = new Handler(_ => throw new HttpRequestException("connection refused"));
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebSearch.Create(new(SearchKind.SearXNG, "http://localhost:8080"), new HttpClient(down))!.SearchAsync("q", 1));
        Assert.Contains("Couldn't reach SearXNG", ex.Message);
    }

    [Fact]
    public void No_search_is_configured_when_the_kind_is_none() =>
        Assert.Null(WebSearch.Create(SearchSettings.Disabled, new HttpClient()));
}

public class ChatAgentBackendTests
{
    private sealed class ScriptedChat(params Func<IList<ChatMessage>, ChatOptions?, ChatResponse>[] steps) : IChatClient
    {
        public int Calls { get; private set; }
        public List<string> ToolNames { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            if (Calls == 0) ToolNames.AddRange(options?.Tools?.Select(t => t.Name) ?? []);
            if (Calls >= steps.Length) throw new InvalidOperationException("The model was called more often than the test expected.");
            return Task.FromResult(steps[Calls++](messages.ToList(), options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls]));

    private static FunctionCallContent Call(string id, string name, object args) =>
        new(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)));

    private sealed class FakeSearch : IWebSearch
    {
        public string Name => "Fake";
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<WebSearchResult>>([new("Jane", "https://example.com/jane", "CEO")]);
        }
    }

    private static (AgentTask Task, TaskCompletionSource<bool> Done) Verification(bool requiresSearch = false)
    {
        var done = new TaskCompletionSource<bool>();
        var report = AIFunctionFactory.Create((bool exists, string? name, string? reason) =>
        {
            done.TrySetResult(exists);
            return "Recorded.";
        }, "report_verification", "Report");
        return (new AgentTask("system", "user", report, done.Task, requiresSearch), done);
    }

    private static ChatAgentBackend Backend(IChatClient chat, IWebSearch? search = null) =>
        new(new AgentProvider("id", "Test provider", "http://localhost:1/v1", null, "model-x"), search, new SafeWebFetcher(),
            NullLogger.Instance, chat);

    [Fact]
    public async Task Runs_the_tool_loop_and_stops_as_soon_as_the_report_is_called()
    {
        var search = new FakeSearch();
        var chat = new ScriptedChat(
            (_, _) => Calls(Call("1", "web_search", new { query = "who is jane" })),
            (messages, _) =>
            {
                // The search result was fed back to the model.
                Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                    r => r.Result?.ToString()!.Contains("https://example.com/jane") == true);
                return Calls(Call("2", "report_verification", new { exists = true, name = "Jane", reason = "found" }));
            });
        var (task, done) = Verification(requiresSearch: true);
        var progress = new List<string>();

        await Backend(chat, search).RunAsync(task, new Sync(progress), default);

        Assert.True(await done.Task);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(["who is jane"], search.Queries);
        Assert.Contains("web_search", chat.ToolNames);
        Assert.Contains("web_fetch", chat.ToolNames);
        Assert.Contains("report_verification", chat.ToolNames);
        Assert.Contains("Searching: who is jane", progress);
    }

    private sealed class Sync(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    [Fact]
    public async Task Search_is_required_only_for_jobs_that_need_it_and_the_tool_is_hidden_without_it()
    {
        var chat = new ScriptedChat((_, _) => Calls(Call("1", "report_verification", new { exists = false, name = (string?)null, reason = "404" })));
        var (needsSearch, _) = Verification(requiresSearch: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Backend(chat).RunAsync(needsSearch, null, default));
        Assert.Contains("web search provider", ex.Message);
        Assert.Equal(0, chat.Calls);

        var (readOnly, done) = Verification(requiresSearch: false);
        await Backend(chat).RunAsync(readOnly, null, default);
        Assert.False(await done.Task);
        Assert.DoesNotContain("web_search", chat.ToolNames);
        Assert.Contains("web_fetch", chat.ToolNames);
    }

    [Fact]
    public async Task Fetch_failures_are_returned_to_the_model_as_text_not_thrown()
    {
        string? seen = null;
        var chat = new ScriptedChat(
            (_, _) => Calls(Call("1", "web_fetch", new { url = "http://127.0.0.1/admin" })),
            (messages, _) =>
            {
                seen = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
                return Calls(Call("2", "report_verification", new { exists = false, name = (string?)null, reason = "blocked" }));
            });
        var (task, done) = Verification();
        await Backend(chat).RunAsync(task, null, default);
        Assert.False(await done.Task);
        Assert.StartsWith("Error:", seen);
        Assert.Contains("private or local", seen);
    }

    [Fact]
    public async Task A_looping_model_is_cut_off_after_the_tool_budget()
    {
        var search = new FakeSearch();
        var steps = Enumerable.Range(0, ChatAgentBackend.MaxToolCalls + 2)
            .Select<int, Func<IList<ChatMessage>, ChatOptions?, ChatResponse>>(i => (_, _) => Calls(Call($"c{i}", "web_search", new { query = $"q{i}" })))
            .ToArray();
        var chat = new ScriptedChat(steps);
        var (task, done) = Verification(requiresSearch: true);

        await Assert.ThrowsAnyAsync<Exception>(() => Backend(chat, search).RunAsync(task, null, default));

        Assert.False(done.Task.IsCompleted);
        Assert.Equal(ChatAgentBackend.MaxToolCalls, search.Queries.Count);
    }

    [Fact]
    public async Task Unreachable_servers_say_which_provider_could_not_be_reached()
    {
        var chat = new ScriptedChat((_, _) => throw new HttpRequestException("connection refused"));
        var (task, _) = Verification();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Backend(chat).RunAsync(task, null, default));
        Assert.Contains("Couldn't reach Test provider", ex.Message);
    }

    [Fact]
    public async Task Test_reports_the_model_and_the_search_state()
    {
        var chat = new ScriptedChat((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        var message = await Backend(chat, new FakeSearch()).TestAsync();
        Assert.Contains("Connected to Test provider using model model-x", message);
        Assert.Contains("Fake search works", message);

        var noSearch = await Backend(new ScriptedChat((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")))).TestAsync();
        Assert.Contains("No web search is configured", noSearch);
    }
}

public class AgentSettingsCardTests
{
    [Fact]
    public async Task Settings_shows_copilot_by_default_and_a_selected_providers_details_and_search_section()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        var initial = await host.RenderAsync<Settings>([Platform.X]);
        Assert.Contains("Account finder agent", initial);
        Assert.Contains("GitHub Copilot", initial);
        Assert.Contains("Add provider", initial);
        Assert.Contains("GitHub token (optional)", initial);
        Assert.DoesNotContain("Web search", initial);

        var store = host.Provider.GetRequiredService<AgentSettingsStore>();
        var saved = await store.SaveProviderAsync(new("", "My Ollama", "http://localhost:11434/v1", null, "llama3.1"));
        await store.SaveProviderAsync(new("", "OpenAI", "https://api.openai.com/v1", "sk-test-secret", "gpt-4o-mini"));
        await store.SetActiveAsync(saved.Id);

        var html = await host.RenderAsync<Settings>([Platform.X]);
        Assert.Contains("My Ollama", html);
        Assert.Contains("OpenAI", html);
        Assert.Contains("llama3.1", html);
        Assert.Contains("localhost:11434", html);
        Assert.Contains("Edit provider", html);
        Assert.Contains("Web search", html);
        Assert.Contains("SearXNG", html);
        Assert.DoesNotContain("GitHub token (optional)", html);
        Assert.DoesNotContain("sk-test-secret", html);
    }

    [Fact]
    public async Task Settings_hides_the_agent_card_when_no_searchable_platform_is_enabled()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        Assert.DoesNotContain("Account finder agent", await host.RenderAsync<Settings>([Platform.Bluesky, Platform.Mastodon]));
    }
}

public class GeneratedPostCheckTests
{
    private static readonly Uri Source = new("https://example.com/post");

    [Fact]
    public void Over_limit_and_missing_drafts_are_reported_with_counts()
    {
        var link = Source.ToString();
        var (posts, problems) = AccountFinder.CheckPosts(
            [new("Shared", $"Short {link}"), new("Threads", new string('a', 520) + " " + link)],
            [Platform.Bluesky, Platform.Threads], Source);

        Assert.Equal(2, posts.Count);
        Assert.Contains(problems, p => p.Contains("Threads post is") && p.Contains("500-character limit"));
        Assert.Contains(problems, p => p == "Missing posts for: Bluesky.");
    }

    [Fact]
    public void Length_budgets_account_for_how_each_platform_counts_the_link()
    {
        var uri = new Uri("https://duendesoftware.com/blog/are-csharp-and-dotnet-worth-learning-in-2026");
        // Threads counts the whole 76-character URL; X always counts 23.
        Assert.True(AccountFinder.TargetLength(Platform.Threads, uri) < 500 - 76);
        Assert.True(AccountFinder.TargetLength(Platform.X, uri) < 280 - 23);
        Assert.Contains("uses 76", AccountFinder.LengthGuide(Platform.Threads, uri));
        Assert.Contains("uses 23", AccountFinder.LengthGuide(Platform.X, uri));
        foreach (var platform in Enum.GetValues<Platform>())
        {
            var text = new string('a', AccountFinder.TargetLength(platform, uri)) + " " + uri;
            Assert.False(SocialPoster.Posting.PostRenderer.Render(platform, text, _ => null).IsOverLimit);
        }
    }

    [Fact]
    public void Valid_drafts_have_no_problems()
    {
        var link = Source.ToString();
        var (posts, problems) = AccountFinder.CheckPosts(
            [new("Shared", $"Shared {link}"), new("threads", $"Threads {link}"), new("Bluesky", $"Sky {link}")],
            [Platform.Bluesky, Platform.Threads], Source);

        Assert.Equal(3, posts.Count);
        Assert.Empty(problems);
    }
}
