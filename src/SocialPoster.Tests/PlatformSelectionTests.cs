using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MudBlazor;
using SocialPoster.Components;
using SocialPoster.Components.Pages;
using SocialPoster.Components.Shared;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class PlatformSelectionTests
{
    public static IEnumerable<object[]> Selections =>
        Enumerable.Range(0, 1 << Enum.GetValues<Platform>().Length).Select(mask => new object[]
        {
            Enum.GetValues<Platform>().Where(p => (mask & (1 << (int)p)) != 0).ToArray()
        });

    [Fact]
    public void Selection_validates_deduplicates_and_limits_discovery()
    {
        var selection = new PlatformSelection([Platform.Bluesky, Platform.LinkedIn, Platform.Bluesky]);
        Assert.Equal([Platform.Bluesky, Platform.LinkedIn], selection.Platforms);
        Assert.Equal([Platform.LinkedIn], selection.Searchable);
        Assert.False(selection.Contains(Platform.X));
        Assert.Throws<ArgumentException>(() => new PlatformSelection([(Platform)99]));
        Assert.Throws<ArgumentNullException>(() => new PlatformSelection(null!));
        Assert.Equal(Enum.GetValues<Platform>(), new SocialPosterComponentOptions().Platforms);
        var services = new ServiceCollection().AddSocialPosterComponents(o => o.Platforms = [(Platform)99]);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SocialPosterComponentOptions>>().Value);
    }

    [Fact]
    public void Search_request_never_falls_back_to_disabled_platforms()
    {
        var contact = new Contact
        {
            DisplayName = "Jane Doe",
            XUsername = "hidden-x",
            LinkedInProfileUrl = "https://www.linkedin.com/in/jane",
            BlueskyHandle = "hidden.bsky.social"
        };
        var request = AccountSearchService.CreateRequest(contact, null, null, [Platform.LinkedIn]);
        Assert.Equal([Platform.LinkedIn], request.Platforms);
        Assert.Null(request.KnownXUsername);
        Assert.Null(request.BlueskyHandle);
        Assert.Equal(contact.LinkedInProfileUrl, request.KnownLinkedInUrl);
        Assert.Empty(AccountSearchService.CreateRequest(contact, null, null, [Platform.Bluesky]).Platforms);
        Assert.Empty(AccountSearchService.CreateRequest(contact, null, null, []).Platforms);

        contact.LinkedInProfileUrl = null;
        Assert.Equal([Platform.LinkedIn], AccountSearchService.CreateRequest(contact, null, null, [Platform.LinkedIn]).Platforms);
        Assert.Equal(AccountSearch.KeyFor(1), AccountSearchService.SearchKey(1, null));
        Assert.Equal(AccountSearchService.SearchKey(1, [Platform.LinkedIn]),
            AccountSearchService.SearchKey(1, [Platform.LinkedIn, Platform.Bluesky, Platform.LinkedIn]));
        Assert.NotEqual(AccountSearchService.SearchKey(1, null), AccountSearchService.SearchKey(1, [Platform.LinkedIn]));
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task Settings_render_only_selected_platforms(Platform[] platforms)
    {
        await using var host = await RenderHost.CreateAsync();
        var html = await host.RenderAsync<Settings>(platforms);
        AssertPlatformLabels(html, platforms);
        Assert.Equal(platforms.Any(p => p is Platform.X or Platform.LinkedIn), html.Contains("Account finder agent"));
        Assert.Equal(platforms.Contains(Platform.X), html.Contains("developer.x.com"));
        Assert.Equal(platforms.Contains(Platform.LinkedIn), html.Contains("Verify token"));
        Assert.Equal(platforms.Contains(Platform.Mastodon), html.Contains("Instance URL"));
        Assert.Equal(platforms.Contains(Platform.Threads), html.Contains("threads_content_publish"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task X_credentials_are_disabled_only_for_browser_posting(bool browserPosting)
    {
        await using var host = await RenderHost.CreateAsync();
        await host.Provider.GetRequiredService<SecretStore>()
            .SetAsync(SettingKeys.XPostMethod, browserPosting ? "intent" : null);

        var html = await host.RenderAsync<Settings>([Platform.X]);
        var inputs = System.Text.RegularExpressions.Regex.Matches(html, "<input\\b[^>]*>")
            .Select(m => m.Value)
            .Where(input => input.Contains("type=\"password\""))
            .ToArray();
        // The four X credentials precede the optional Copilot token.
        Assert.Equal(5, inputs.Length);
        foreach (var input in inputs.Take(4))
            Assert.Equal(browserPosting, System.Text.RegularExpressions.Regex.IsMatch(input, @"\bdisabled(?:=|\s|>)"));
        Assert.DoesNotContain("disabled", inputs[4]);
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task Composer_contacts_and_badges_render_only_selected_platforms(Platform[] platforms)
    {
        await using var host = await RenderHost.CreateAsync();
        var composer = await host.RenderAsync<Home>(platforms);
        foreach (var platform in Enum.GetValues<Platform>())
            Assert.Equal(platforms.Contains(platform), composer.Contains($"platform-{platform.ToString().ToLowerInvariant()}"));

        var contact = new Contact
        {
            DisplayName = "Jane Doe", Key = "jane", XUsername = "excluded_x",
            BlueskyHandle = "jane.bsky.social", BlueskyDid = "did:plc:jane",
            LinkedInProfileUrl = "https://www.linkedin.com/in/jane"
        };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
        }
        var contacts = await host.RenderAsync<EditingContacts>(platforms, new() { [nameof(EditingContacts.ContactId)] = contact.Id });
        AssertPlatformLabels(contacts, platforms);
        Assert.Equal(platforms.Contains(Platform.X), contacts.Contains("excluded_x"));
        Assert.Equal(platforms.Contains(Platform.LinkedIn), contacts.Contains("linkedin.com/in/jane"));
        Assert.Equal(platforms.Contains(Platform.Bluesky), contacts.Contains("Sync Bluesky follows"));
        Assert.Equal(platforms.Contains(Platform.Mastodon), contacts.Contains("Mastodon acct"));

        var badges = await host.RenderAsync<PlatformBadges>(platforms, new() { [nameof(PlatformBadges.Contact)] = contact });
        Assert.Equal(platforms.Contains(Platform.X), badges.Contains("excluded_x"));
        Assert.Equal(platforms.Contains(Platform.Bluesky), badges.Contains("jane.bsky.social"));
        Assert.Equal(platforms.Contains(Platform.LinkedIn), badges.Contains("LinkedIn"));
    }

    [Fact]
    public async Task History_and_discovery_hide_existing_excluded_platform_data()
    {
        await using var host = await RenderHost.CreateAsync();
        var contact = new Contact { Key = "jane", DisplayName = "Jane Doe" };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            db.PostHistory.AddRange(
                new PostHistoryEntry { Template = "hidden X-only post", ResultsJson = JsonSerializer.Serialize(new[] { new PublishResult(Platform.X, true, "https://x.com/hidden", null) }) },
                new PostHistoryEntry { Template = "visible mixed post", ResultsJson = JsonSerializer.Serialize(new[] { new PublishResult(Platform.X, true, "https://x.com/hidden", null), new PublishResult(Platform.LinkedIn, true, "https://www.linkedin.com/posts/visible", null) }) });
            db.AccountSearches.Add(new AccountSearch
            {
                Key = AccountSearch.KeyFor(contact.Id), ContactId = contact.Id,
                Status = AccountSearchStatus.Failed, Error = "Old X search failed"
            });
            await db.SaveChangesAsync();
        }
        var history = await host.RenderAsync<History>([Platform.LinkedIn]);
        Assert.Contains("visible mixed post", history);
        Assert.Contains("linkedin.com/posts/visible", history);
        Assert.DoesNotContain("hidden X-only post", history);
        Assert.DoesNotContain("x.com", history);

        var finder = await host.RenderAsync<AccountFinderPanel>([Platform.LinkedIn],
            new() { [nameof(AccountFinderPanel.Contact)] = contact });
        Assert.Contains("Find on LinkedIn", finder);
        Assert.DoesNotContain("Old X search", finder);
        Assert.DoesNotContain("Find on X", finder);
        var emptyFinder = await host.RenderAsync<AccountFinderPanel>([Platform.Bluesky],
            new() { [nameof(AccountFinderPanel.Contact)] = contact }, wrap: false);
        Assert.Equal("", emptyFinder);

        var searches = host.Provider.GetRequiredService<AccountSearchService>();
        Assert.Null(await searches.GetAsync(contact.Id, platforms: [Platform.LinkedIn]));
        Assert.NotNull(await searches.GetAsync(contact.Id));
        searches.Start(contact, platforms: [Platform.Bluesky]);
        await searches.StartIfNeededAsync(contact, platforms: []);
        Assert.False(searches.IsRunning(contact.Id, [Platform.Bluesky]));
    }

    [Fact]
    public async Task View_cascades_selection_and_standalone_pages_use_registered_defaults()
    {
        await using var host = await RenderHost.CreateAsync([Platform.Bluesky]);
        var defaultSettings = await host.RenderAsync<Settings>(null);
        AssertPlatformLabels(defaultSettings, [Platform.Bluesky]);
        var explicitSettings = await host.RenderAsync<Settings>([Platform.LinkedIn]);
        AssertPlatformLabels(explicitSettings, [Platform.LinkedIn]);
        var view = await host.RenderAsync<SocialPosterView>([Platform.LinkedIn], new() { [nameof(SocialPosterView.Section)] = "settings" }, wrap: false);
        AssertPlatformLabels(view, [Platform.LinkedIn]);
    }

    [Fact]
    public async Task Mention_search_skips_disabled_live_platforms_and_filters_descriptions()
    {
        await using var host = await RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(new Contact { Key = "jane", DisplayName = "Jane Doe", XUsername = "hidden_x", BlueskyHandle = "hidden.bsky.social", LinkedInUrn = "urn:li:organization:1" });
            await db.SaveChangesAsync();
        }
        using var scope = host.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContactService>();
        var candidates = await service.SearchAsync("jane", xAvailable: true, platforms: [Platform.LinkedIn]);
        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateKind.Saved, candidate.Kind);
        Assert.DoesNotContain("hidden", candidate.Subtitle);
        Assert.Contains("in", candidate.Subtitle);
        Assert.Empty(await service.SearchAsync("hidden_x", xAvailable: true, platforms: [Platform.LinkedIn]));
        Assert.Equal(0, host.Http.RequestCount);
    }

    [Fact]
    public async Task Contact_edits_preserve_disabled_identities_without_validation_or_live_lookups()
    {
        await using var host = await RenderHost.CreateAsync();
        var contact = new Contact
        {
            Key = "jane", DisplayName = "Jane Doe",
            XUsername = "legacy-invalid-x-name", XUserId = "123",
            BlueskyHandle = "hidden.bsky.social", LinkedInProfileUrl = "https://www.linkedin.com/in/jane"
        };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
        }
        using var scope = host.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContactService>();
        contact.DisplayName = "Jane Smith";
        Assert.Null(await service.SaveAsync(contact, platforms: [Platform.LinkedIn]));
        var saved = await service.GetAsync(contact.Id);
        Assert.Equal("Jane Smith", saved!.DisplayName);
        Assert.Equal(contact.XUsername, saved.XUsername);
        Assert.Equal("123", saved.XUserId);
        Assert.Equal("hidden.bsky.social", saved.BlueskyHandle);
        Assert.Equal(0, host.Http.RequestCount);

        var actor = new BskyActor("did:plc:new", "new.bsky.social", "New Person", null,
            "https://x.com/hidden https://www.linkedin.com/in/new-person");
        var imported = await service.EnsureFromBlueskyAsync(actor, ContactSource.BlueskySearch,
            platforms: [Platform.Bluesky, Platform.LinkedIn]);
        Assert.Null(imported.Contact.XUsername);
        Assert.Equal("https://www.linkedin.com/in/new-person", imported.Contact.LinkedInProfileUrl);
        Assert.DoesNotContain(imported.Notes, n => n.Contains("X @"));
    }

    [Fact]
    public async Task Dismissing_scoped_discovery_results_preserves_other_cached_selections()
    {
        await using var host = await RenderHost.CreateAsync();
        var candidate = new AccountCandidate(Platform.LinkedIn, AccountKind.Person, "jane", "Jane Doe",
            "https://www.linkedin.com/in/jane", null, 90, null);
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            foreach (var platforms in new IReadOnlyCollection<Platform>[] { [Platform.X, Platform.LinkedIn], [Platform.LinkedIn] })
            {
                db.AccountSearches.Add(new AccountSearch
                {
                    Key = AccountSearchService.SearchKey(1, platforms), ContactId = 1,
                    Status = AccountSearchStatus.Done, CandidatesJson = JsonSerializer.Serialize(new[] { candidate })
                });
            }
            await db.SaveChangesAsync();
        }
        var searches = host.Provider.GetRequiredService<AccountSearchService>();
        await searches.DismissAsync(1, candidate, platforms: [Platform.LinkedIn]);
        Assert.Empty((await searches.GetAsync(1, platforms: [Platform.LinkedIn]))!.Candidates);
        Assert.Single((await searches.GetAsync(1))!.Candidates);
        Assert.Single(await searches.GetManyAsync([1], platforms: [Platform.LinkedIn]));
    }

    [Fact]
    public async Task Accounts_switches_persist_across_scopes_and_can_reenable_platforms()
    {
        await using var host = await RenderHost.CreateAsync();
        var secrets = host.Provider.GetRequiredService<SecretStore>();
        await secrets.SetAsync(SettingKeys.XConsumerKey, "saved-test-key");
        using (var scope = host.Provider.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<PlatformPreferences>();
            await preferences.LoadAsync();
            Assert.True(preferences.Apply(PlatformSelection.All).Contains(Platform.X));
            var notifications = 0;
            preferences.Changed += () => notifications++;
            await preferences.SetEnabledAsync(Platform.X, false);
            Assert.False(preferences.Apply(PlatformSelection.All).Contains(Platform.X));
            Assert.Equal(1, notifications);
        }

        var settings = await host.RenderAsync<Settings>(null);
        Assert.Contains("Selected platforms", settings);
        Assert.Contains("X", settings);
        Assert.DoesNotContain("developer.x.com", settings);
        Assert.Contains("Find", await host.RenderAsync<AccountFinderPanel>(null,
            new() { [nameof(AccountFinderPanel.Contact)] = new Contact() }));
        var composer = await host.RenderAsync<Home>(null);
        Assert.DoesNotContain("platform-x", composer);
        Assert.Contains("platform-bluesky", composer);
        var view = await host.RenderAsync<SocialPosterView>(null,
            new() { [nameof(SocialPosterView.Section)] = "settings" }, wrap: false);
        Assert.Contains("Selected platforms", view);
        Assert.DoesNotContain("developer.x.com", view);

        using (var scope = host.Provider.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<PlatformPreferences>();
            await preferences.LoadAsync();
            Assert.False(preferences.Apply(PlatformSelection.All).Contains(Platform.X));
            await preferences.SetEnabledAsync(Platform.X, true);
            Assert.False(preferences.Apply(new PlatformSelection([Platform.LinkedIn])).Contains(Platform.X));
        }
        Assert.Contains("developer.x.com", await host.RenderAsync<Settings>(null));
        Assert.Equal("saved-test-key", await secrets.GetAsync(SettingKeys.XConsumerKey));
    }

    [Fact]
    public async Task Accounts_allows_empty_selection_without_losing_reenable_switches()
    {
        await using var host = await RenderHost.CreateAsync();
        using (var scope = host.Provider.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<PlatformPreferences>();
            foreach (var platform in Enum.GetValues<Platform>())
                await preferences.SetEnabledAsync(platform, false);
        }
        var settings = await host.RenderAsync<Settings>(null);
        Assert.Contains("No platforms selected", settings);
        Assert.Contains("Selected platforms", settings);
        Assert.DoesNotContain("developer.x.com", settings);
        Assert.DoesNotContain("Verify token", settings);
        Assert.DoesNotContain("Account finder agent", settings);
        foreach (var platform in Enum.GetValues<Platform>())
            Assert.Contains(PlatformInfo.Label(platform), settings);
        var restricted = await host.RenderAsync<Settings>([Platform.LinkedIn]);
        Assert.DoesNotContain(">X<", restricted);
        Assert.DoesNotContain("Bluesky", restricted);
        Assert.Contains("LinkedIn", restricted);
    }

    [Fact]
    public async Task Mounted_view_updates_when_accounts_preferences_change()
    {
        await using var host = await RenderHost.CreateAsync();
        await using var scope = host.Provider.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, host.Provider.GetRequiredService<ILoggerFactory>());
        var preferences = scope.ServiceProvider.GetRequiredService<PlatformPreferences>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = await renderer.RenderComponentAsync<SocialPosterView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SocialPosterView.Section)] = "settings"
            }));
            Assert.Contains("developer.x.com", view.ToHtmlString());
            await preferences.SetEnabledAsync(Platform.X, false);
            await view.QuiescenceTask;
            Assert.DoesNotContain("developer.x.com", view.ToHtmlString());
            Assert.Contains("Selected platforms", view.ToHtmlString());
            await preferences.SetEnabledAsync(Platform.X, true);
            await view.QuiescenceTask;
            Assert.Contains("developer.x.com", view.ToHtmlString());
        });
    }

    private static void AssertPlatformLabels(string html, Platform[] platforms)
    {
        foreach (var platform in Enum.GetValues<Platform>())
        {
            var visibleText = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " "));
            Assert.Equal(platforms.Contains(platform),
                System.Text.RegularExpressions.Regex.IsMatch(visibleText, $@"\b{PlatformInfo.Label(platform)}\b"));
        }
    }

    [Fact]
    public async Task Contact_platform_filter_matches_any_checked_platform_before_paging()
    {
        await using var host = await RenderHost.CreateAsync();
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Contacts.AddRange(
                new Contact { Key = "x", DisplayName = "A X", XUsername = "x_user" },
                new Contact { Key = "bsky", DisplayName = "B Bluesky", BlueskyHandle = "b.bsky.social" },
                new Contact { Key = "li", DisplayName = "C LinkedIn", LinkedInProfileUrl = "https://www.linkedin.com/in/c" },
                new Contact { Key = "both", DisplayName = "D Both", XUsername = "both", LinkedInUrn = "urn:li:organization:1" },
                new Contact { Key = "none", DisplayName = "E None" },
                new Contact { Key = "blank", DisplayName = "F Blank", XUsername = " ", BlueskyHandle = "", LinkedInUrn = " " });
            await db.SaveChangesAsync();
        }
        using var scope = host.Provider.CreateScope();
        var contacts = scope.ServiceProvider.GetRequiredService<ContactService>();
        Assert.Equal(6, (await contacts.ListAsync(new ContactQuery())).TotalCount);
        Assert.Equal(["x", "both"], (await contacts.ListAsync(new ContactQuery(LinkedPlatforms: [Platform.X]))).Items.Select(c => c.Key));
        Assert.Equal(["bsky"], (await contacts.ListAsync(new ContactQuery(LinkedPlatforms: [Platform.Bluesky]))).Items.Select(c => c.Key));
        Assert.Equal(["li", "both"], (await contacts.ListAsync(new ContactQuery(LinkedPlatforms: [Platform.LinkedIn]))).Items.Select(c => c.Key));
        var page = await contacts.ListAsync(new ContactQuery(LinkedPlatforms: [Platform.X, Platform.LinkedIn], PageSize: 1, Page: 2));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal("li", Assert.Single(page.Items).Key);
        Assert.Empty((await contacts.ListAsync(new ContactQuery(LinkedPlatforms: []))).Items);
        Assert.Empty((await contacts.ListAsync(new ContactQuery(Platforms: [Platform.Bluesky], LinkedPlatforms: [Platform.X]))).Items);
        Assert.Equal("both", Assert.Single((await contacts.ListAsync(new ContactQuery(Filter: "Both", LinkedPlatforms: [Platform.X]))).Items).Key);
        var html = await host.RenderAsync<Contacts>([Platform.Bluesky, Platform.LinkedIn]);
        Assert.Contains("Show platforms", html);
        Assert.DoesNotContain("Any X status", html);
        Assert.DoesNotContain("Any LinkedIn status", html);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    public async Task Contacts_X_follow_button_requires_api_mode_credentials_and_enabled_X(
        bool configured, bool intent, bool selected, bool preferenceEnabled)
    {
        await using var host = await RenderHost.CreateAsync();
        var secrets = host.Provider.GetRequiredService<SecretStore>();
        if (configured)
            foreach (var key in new[] { SettingKeys.XConsumerKey, SettingKeys.XConsumerSecret, SettingKeys.XAccessToken, SettingKeys.XAccessTokenSecret })
                await secrets.SetAsync(key, "fake-test-value");
        if (intent) await secrets.SetAsync(SettingKeys.XPostMethod, "intent");
        if (!preferenceEnabled)
        {
            using var scope = host.Provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PlatformPreferences>().SetEnabledAsync(Platform.X, false);
        }
        var html = await host.RenderAsync<Contacts>(selected ? [Platform.X] : [Platform.Bluesky]);
        Assert.Equal(configured && !intent && selected && preferenceEnabled, html.Contains("Sync X follows"));
        Assert.Equal(0, host.Http.RequestCount);
    }

    internal sealed class RenderHost(ServiceProvider provider, string directory, RecordingHttpHandler http) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public RecordingHttpHandler Http { get; } = http;
        public IDbContextFactory<AppDbContext> Factory => Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        public static async Task<RenderHost> CreateAsync(Platform[]? defaults = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"socialposter-platform-tests-{Guid.NewGuid():N}");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            services.AddSingleton<NavigationManager, TestNavigation>();
            services.AddSocialPosterCore(o => o.DataDirectory = directory);
            services.AddSocialPosterComponents(o =>
            {
                if (defaults is not null) o.Platforms = defaults;
            });
            var http = new RecordingHttpHandler();
            services.AddHttpClient(nameof(BlueskyClient)).ConfigurePrimaryHttpMessageHandler(() => http);
            services.AddHttpClient(nameof(XClient)).ConfigurePrimaryHttpMessageHandler(() => http);
            services.AddHttpClient(nameof(MastodonClient)).ConfigurePrimaryHttpMessageHandler(() => http);
            var provider = services.BuildServiceProvider();
            await provider.InitializeSocialPosterAsync();
            return new RenderHost(provider, directory, http);
        }

        public async Task<string> RenderAsync<T>(Platform[]? platforms, Dictionary<string, object?>? extra = null, bool wrap = true) where T : IComponent
        {
            await using var scope = Provider.CreateAsyncScope();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, Provider.GetRequiredService<ILoggerFactory>());
            var parameters = extra ?? [];
            if (platforms is not null) parameters[nameof(PlatformComponent.Platforms)] = platforms;
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = wrap
                    ? await renderer.RenderComponentAsync<WithProviders>(ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(WithProviders.ChildContent)] = (RenderFragment)(builder =>
                        {
                            builder.OpenComponent<T>(0);
                            builder.AddMultipleAttributes(1, parameters.Select(p => new KeyValuePair<string, object>(p.Key, p.Value!)));
                            builder.CloseComponent();
                        })
                    }))
                    : await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
                return output.ToHtmlString();
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }

        public async Task<string> RenderDialogAsync<T>(Dictionary<string, object?> parameters, Func<T, Task>? interact = null,
            Action<DialogResult?>? onResult = null) where T : IComponent
        {
            await using var scope = Provider.CreateAsyncScope();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, Provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<WithProviders>();
                var dialogParameters = new DialogParameters();
                foreach (var parameter in parameters) dialogParameters.Add(parameter.Key, parameter.Value);
                var dialog = await scope.ServiceProvider.GetRequiredService<IDialogService>().ShowAsync<T>("Maintenance", dialogParameters);
                await output.QuiescenceTask;
                if (interact is not null)
                    await interact((T)dialog.Dialog!);
                if (onResult is not null)
                    onResult(await dialog.Result.WaitAsync(TimeSpan.FromSeconds(5)));
                return output.ToHtmlString();
            });
        }
    }

    private sealed class WithProviders : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(2);
            builder.CloseComponent();
            builder.AddContent(1, ChildContent);
        }
    }

    private sealed class EditingContacts : Contacts
    {
        [Parameter] public int ContactId { get; set; }
        protected override async Task OnInitializedAsync()
        {
            EditId = ContactId;
            await base.OnInitializedAsync();
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/socialposter");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    internal sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("No live platform requests should be made in these tests.");
        }
    }
}
