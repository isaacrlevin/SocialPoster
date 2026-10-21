using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Components;
using SocialPoster.Components.Pages;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class SettingsPersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Test_uses_draft_credentials_without_saving_and_save_survives_restart()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var services = new ServiceCollection();
        services.AddSocialPosterComponents();
        using var ui = services.BuildServiceProvider();
        var secrets = host.Services.GetRequiredService<SecretStore>();
        await secrets.SetManyAsync(new Dictionary<string, string?> {
            [SettingKeys.BlueskyHandle] = "old.example", [SettingKeys.BlueskyAppPassword] = "old-password" });
        var settings = new Settings();
        foreach (var property in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<InjectAttribute>() is null) continue;
            var value = property.PropertyType == typeof(IHttpClientFactory) ? new FakeFactory() :
                property.PropertyType == typeof(IServiceProvider) ? host.Services :
                property.PropertyType == typeof(PlatformPreferences) ? new PlatformPreferences(secrets) :
                ui.GetService(property.PropertyType) ?? host.Services.GetService(property.PropertyType);
            property.SetValue(settings, value);
        }
        await Invoke(settings, "OnParametersSetAsync");
        Set(settings, "bskyHandle", "new.example");
        Set(settings, "bskyPassword", "new-password");
        // A parent render must not restore the old handle over the draft.
        await Invoke(settings, "OnParametersSetAsync");
        await Invoke(settings, "TestBlueskyAsync");
        Assert.Contains("Logged in", (string)Get(settings, "bskyMsg")!);
        Assert.Equal("old.example", await secrets.GetAsync(SettingKeys.BlueskyHandle));
        Assert.Equal("new-password", Get(settings, "bskyPassword"));
        typeof(Settings).GetProperty("HttpClients", Private)!.SetValue(settings, new FailedFactory());
        await Invoke(settings, "TestBlueskyAsync");
        Assert.True((bool)Get(settings, "bskyErr")!);
        Assert.Equal("new.example", Get(settings, "bskyHandle"));
        Assert.Equal("new-password", Get(settings, "bskyPassword"));
        await Invoke(settings, "SaveBlueskyAsync");
        await host.RestartAsync();
        secrets = host.Services.GetRequiredService<SecretStore>();
        Assert.Equal("new.example", await secrets.GetAsync(SettingKeys.BlueskyHandle));
        Assert.Equal("new-password", await secrets.GetAsync(SettingKeys.BlueskyAppPassword));
    }

    [Fact]
    public async Task Blank_secrets_are_preserved_and_preview_identity_is_not_saved()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var secrets = host.Services.GetRequiredService<SecretStore>();
        await secrets.SetAsync(SettingKeys.BlueskyAppPassword, "kept-password");
        var settings = new Settings();
        typeof(Settings).GetProperty("Secrets", Private)!.SetValue(settings, secrets);
        Set(settings, "bskyHandle", "draft.example");
        var values = await (Task<Dictionary<string, string?>>)typeof(Settings).GetMethod("BlueskyValues", Private)!.Invoke(settings, null)!;
        Assert.Equal("kept-password", values[SettingKeys.BlueskyAppPassword]);
        var preview = await secrets.CreatePreviewAsync(values);
        await preview.SetAsync(SettingKeys.LinkedInName, "preview identity");
        Assert.Null(await secrets.GetAsync(SettingKeys.LinkedInName));
        Assert.Equal("draft.example", Get(settings, "bskyHandle"));
    }

    private static Task Invoke(Settings settings, string method) => (Task)typeof(Settings).GetMethod(method, Private)!.Invoke(settings, null)!;
    private static void Set(Settings settings, string name, object value) => typeof(Settings).GetField(name, Private)!.SetValue(settings, value);
    private static object? Get(Settings settings, string name) => typeof(Settings).GetField(name, Private)!.GetValue(settings);

    private sealed class FailedFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailedHandler());
    }
    private sealed class FailedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Invalid credentials") });
    }
    private sealed class FakeFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("new.example", body);
            Assert.Contains("new-password", body);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"accessJwt":"jwt","did":"did:plc:test","handle":"new.example","didDoc":{}}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
