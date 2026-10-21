using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SocialPoster.Components;
using SocialPoster.Components.Pages;
using SocialPoster.Core;
using SocialPoster.Core.Data;
using SocialPoster.Core.Services;

namespace SocialPoster.Tests;

public class MastodonTests
{
    private const string Account = """{"id":"42","acct":"jane","uri":"https://social.example/users/jane","url":"https://social.example/@jane","display_name":"Jane","avatar":"https://social.example/avatar.png","note":"Engineer"}""";
    private static RenderedPost Post(string text = "Hello") => PostRenderer.Render(Platform.Mastodon, text, _ => null);
    private static MastodonAccount Jane(string acct = "jane@social.example", string uri = "https://social.example/users/jane",
        string? note = null) => new("42", acct, uri, MastodonIdentity.ProfileUrl(acct)!, "Jane", null, note);

    [Fact]
    public void Platform_values_and_qualified_mentions_are_backwards_compatible()
    {
        Assert.Equal(0, (int)Platform.X);
        Assert.Equal(1, (int)Platform.Bluesky);
        Assert.Equal(2, (int)Platform.LinkedIn);
        Assert.Equal(3, (int)Platform.Mastodon);
        Assert.Contains(Platform.Mastodon, PlatformSelection.All.Platforms);
        Assert.Empty(new PlatformSelection([Platform.Mastodon]).Searchable);
        var contact = new Contact { DisplayName = "Jane", MastodonAcct = "@Jane@SOCIAL.EXAMPLE" };
        var rendered = PostRenderer.Render(Platform.Mastodon, "Hi {@jane}", _ => contact);
        Assert.Equal("Hi @jane@social.example", rendered.DisplayText);
        Assert.Equal(rendered.DisplayText, rendered.PayloadText);
        Assert.Equal(8, rendered.Length);
        Assert.Equal(500, rendered.Limit);
        Assert.Empty(rendered.Warnings);
        contact.MastodonAcct = null;
        Assert.Equal("Hi Jane", PostRenderer.Render(Platform.Mastodon, "Hi {@jane}", _ => contact).DisplayText);
        Assert.Single(PostRenderer.Render(Platform.Mastodon, "{@jane}", _ => contact).Warnings);
        Assert.Single(PostRenderer.Render(Platform.Mastodon, "{@unknown}", _ => null).Warnings);
    }

    [Theory]
    [InlineData("😀", 1)]
    [InlineData("👩‍💻", 1)]
    [InlineData("👨‍👩‍👧‍👦", 1)]
    [InlineData("e\u0301", 1)]
    [InlineData("https://example.com/really/long/path", 23)]
    [InlineData("https://example.com.", 24)]
    [InlineData("www.example.com/really/long/path", 23)]
    [InlineData("example.com/really/long/path", 23)]
    [InlineData("person@example.com", 18)]
    [InlineData("@jane@social.example", 5)]
    [InlineData("@jane@social.example.", 6)]
    [InlineData("@jane@social.example @other@remote.example", 12)]
    [InlineData("https://example.com/@jane@social.example", 23)]
    [InlineData("@jane@social.example 👩‍💻 https://example.com/@other@remote.example", 31)]
    [InlineData("Hi @jane@social.example https://example.com/long", 32)]
    public void Counting_handles_unicode_urls_and_remote_mentions(string text, int expected) =>
        Assert.Equal(expected, TextMetrics.MastodonLength(text));

    [Theory]
    [InlineData("http://social.example")]
    [InlineData("https://social.example/@jane")]
    [InlineData("https://user:password@social.example")]
    [InlineData("https://social.example/?token=anything")]
    [InlineData("https://social.example/#fragment")]
    public void Instance_validation_requires_a_safe_https_origin(string instance) =>
        Assert.Throws<InvalidOperationException>(() => MastodonClient.NormalizeInstance(instance));

    [Fact]
    public async Task Verify_qualifies_local_accounts_and_protects_pair_without_default_authentication()
    {
        await using var host = await Host.CreateAsync();
        host.Http.Add(Account);
        var account = await host.Client.ConfigureAsync("https://SOCIAL.example/", "test-only-token");
        Assert.Equal("jane@social.example", account.Acct);
        Assert.Equal("https://social.example/api/v1/accounts/verify_credentials", Assert.Single(host.Http.Requests).Url);
        Assert.Equal("Bearer test-only-token", host.Http.Requests[0].Auth);
        Assert.Null(host.HttpClient.DefaultRequestHeaders.Authorization);
        Assert.Equal("https://social.example/", await host.Secrets.GetAsync(SettingKeys.MastodonInstance));
        await using var db = host.CreateDbContext();
        Assert.All(await db.Settings.ToListAsync(), s => Assert.DoesNotContain("test-only-token", s.ProtectedValue));
        host.Http.Add(Account);
        Assert.Equal(account, await host.Client.VerifyCredentialsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.ConfigureAsync("https://other.example", null));
        Assert.Equal(2, host.Http.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Api_errors_are_visible_and_failed_configuration_preserves_old_credentials(HttpStatusCode status)
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add("""{"error":"Rejected test-only-token"}""", status);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            host.Client.ConfigureAsync("https://other.example", "test-only-token"));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("Rejected", error.Message);
        Assert.DoesNotContain("test-only-token", error.Message);
        Assert.Equal("https://social.example/", await host.Secrets.GetAsync(SettingKeys.MastodonInstance));
        Assert.Equal("original-token", await host.Secrets.GetAsync(SettingKeys.MastodonAccessToken));
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Assert.Contains("Reconnect", error.Message);
    }

    [Fact]
    public async Task Search_and_lookup_return_qualified_identities_and_surface_malformed_responses()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add("""{"accounts":[""" + Account + "]}");
        Assert.Equal("jane@social.example", Assert.Single(await host.Client.SearchAsync("@jane")).Acct);
        Assert.Contains("resolve=true", host.Http.Requests[0].Url);
        host.Http.Add(Account);
        Assert.Equal("jane@social.example", (await host.Client.LookupAsync("jane")).Acct);
        Assert.Contains("acct=jane", host.Http.Requests[1].Url);
        host.Http.Add("""{"unexpected":true}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.SearchAsync("name"));
        host.Http.Add("""{"id":42}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.VerifyCredentialsAsync());
        host.Http.Add("""{"error":"Permission denied"}""", HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Contacts.SearchAsync("Jane", false, platforms: [Platform.Mastodon]));
    }

    [Fact]
    public async Task Local_acct_uses_canonical_actor_domain_not_web_connection_alias()
    {
        await using var host = await Host.CreateAsync();
        host.Http.Add(Account);
        var account = await host.Client.ConfigureAsync("https://web.social.example", "test-only-token");
        Assert.Equal("jane@social.example", account.Acct);
        Assert.StartsWith("https://web.social.example/", host.Http.Requests[0].Url);
        host.Http.Add(Account.Replace("\"uri\":\"https://social.example/users/jane\",", ""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.VerifyCredentialsAsync());
        Assert.Contains("4.2", error.Message);
    }

    [Fact]
    public async Task Unknown_remote_lookup_uses_resolving_search_with_original_connection()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        var remote = Account.Replace("social.example", "remote.example").Replace("\"acct\":\"jane\"", "\"acct\":\"jane@remote.example\"");
        host.Http.Add("""{"error":"Record not found"}""", HttpStatusCode.NotFound);
        host.Http.Add("""{"accounts":[""" + remote + "]}");
        host.Http.BeforeSend = async count =>
        {
            if (count == 1)
                await host.Secrets.SetManyAsync(new Dictionary<string, string?>
                {
                    [SettingKeys.MastodonInstance] = "https://other.example/",
                    [SettingKeys.MastodonAccessToken] = "other-token",
                });
        };
        var account = await host.Client.LookupAsync("jane@remote.example");
        Assert.Equal("jane@remote.example", account.Acct);
        Assert.Contains("/api/v2/search", host.Http.Requests[1].Url);
        Assert.Contains("resolve=true", host.Http.Requests[1].Url);
        Assert.All(host.Http.Requests, r =>
        {
            Assert.StartsWith("https://social.example/", r.Url);
            Assert.Equal("Bearer original-token", r.Auth);
        });
    }

    [Fact]
    public async Task Remote_resolution_rejects_unrelated_results_and_does_not_retry_authentication_errors()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add("""{"error":"Record not found"}""", HttpStatusCode.NotFound);
        host.Http.Add("""{"accounts":[""" + Account + "]}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.LookupAsync("jane@remote.example"));
        host.Http.Add("""{"error":"Unauthorized"}""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.LookupAsync("jane@remote.example"));
        Assert.Equal(3, host.Http.Requests.Count);
    }

    [Fact]
    public async Task Disabled_search_makes_no_requests_and_manual_contact_edits_persist_without_a_connection()
    {
        await using var host = await Host.CreateAsync();
        var contact = new Contact { Key = "manual", DisplayName = "Manual", MastodonAcct = "@Jane@SOCIAL.EXAMPLE" };
        Assert.Null(await host.Contacts.SaveAsync(contact, platforms: [Platform.Mastodon]));
        var saved = (await host.Contacts.GetByKeysAsync(["manual"]))["manual"];
        Assert.Equal("jane@social.example", saved.MastodonAcct);
        Assert.Equal("https://social.example/@jane", saved.MastodonProfileUrl);
        saved.DisplayName = "Edited";
        Assert.Null(await host.Contacts.SaveAsync(saved, platforms: [Platform.X]));
        Assert.Equal("jane@social.example", (await host.Contacts.GetAsync(saved.Id))!.MastodonAcct);
        await host.ConfigureAsync();
        Assert.Empty(await host.Contacts.SearchAsync("Jane", false, platforms: []));
        Assert.Empty(host.Http.Requests);
        saved.MastodonAcct = "invalid";
        Assert.Contains("fully qualified", await host.Contacts.SaveAsync(saved, platforms: [Platform.Mastodon]));
    }

    [Fact]
    public async Task Changing_instance_mid_post_does_not_send_the_old_token_to_the_new_server()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        var path = Path.GetTempFileName();
        try
        {
            host.Http.BeforeSend = async count =>
            {
                if (count == 1)
                    await host.Secrets.SetManyAsync(new Dictionary<string, string?>
                    {
                        [SettingKeys.MastodonInstance] = "https://other.example/",
                        [SettingKeys.MastodonAccessToken] = "other-token",
                    });
            };
            host.Http.Add("""{"id":"99","url":"https://social.example/image.png"}""");
            host.Http.Add("""{"url":"https://social.example/@jane/100"}""");
            await host.Client.PostAsync(Post(), [new()
            {
                FileName = "a.png", Path = path, Kind = MediaKind.Image, ContentType = "image/png", Size = 1,
            }]);
            Assert.All(host.Http.Requests, r =>
            {
                Assert.StartsWith("https://social.example/", r.Url);
                Assert.Equal("Bearer original-token", r.Auth);
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unsafe_post_links_and_invalid_response_bodies_are_not_accepted()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add("""{"url":"javascript:alert(1)"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(), []));
        host.Http.Add("not json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(), []));
        host.Http.Add("[]", HttpStatusCode.BadGateway);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.PostAsync(Post(), []));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task Posting_sends_public_text_and_media_descriptions_and_waits_until_ready()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            host.Http.Add("""{"id":"99","url":null}""", HttpStatusCode.Accepted);
            host.Http.Add("", HttpStatusCode.PartialContent);
            host.Http.Add("""{"id":"99","url":"https://social.example/media/image.png"}""");
            host.Http.Add("""{"id":"100","url":"https://social.example/@jane/100"}""");
            var url = await host.Client.PostAsync(Post(), [new()
            {
                FileName = "image.png", Path = path, Kind = MediaKind.Image, Size = 3,
                ContentType = "image/png", AltText = "An accessible description",
            }]);
            Assert.Equal("https://social.example/@jane/100", url);
            Assert.Equal(4, host.Http.Requests.Count);
            Assert.Contains("/api/v2/media", host.Http.Requests[0].Url);
            Assert.Contains("description", host.Http.Requests[0].Body);
            Assert.Contains("An accessible description", host.Http.Requests[0].Body);
            Assert.Contains("/api/v1/media/99", host.Http.Requests[1].Url);
            Assert.Contains("/api/v1/media/99", host.Http.Requests[2].Url);
            using var payload = JsonDocument.Parse(host.Http.Requests[3].Body!);
            Assert.Equal("Hello", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("public", payload.RootElement.GetProperty("visibility").GetString());
            Assert.Equal("99", Assert.Single(payload.RootElement.GetProperty("media_ids").EnumerateArray()).GetString());
            Assert.False(string.IsNullOrWhiteSpace(host.Http.Requests[3].Idempotency));
            Assert.All(host.Http.Requests, r => Assert.Equal("Bearer original-token", r.Auth));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_or_processing_failure_never_creates_a_status(bool processing)
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        var path = Path.GetTempFileName();
        try
        {
            if (processing)
            {
                host.Http.Add("""{"id":"99","url":null}""", HttpStatusCode.Accepted);
                host.Http.Add("", HttpStatusCode.PartialContent);
                host.Http.Add("""{"error":"Processing failed"}""", HttpStatusCode.UnprocessableEntity);
            }
            else host.Http.Add("""{"error":"Upload failed"}""", HttpStatusCode.UnprocessableEntity);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.PostAsync(Post(), [new()
            {
                FileName = "a.png", Path = path, Kind = MediaKind.Image, Size = 1, ContentType = "image/png",
            }]));
            Assert.Contains(processing ? "Processing failed" : "Upload failed", error.Message);
            Assert.DoesNotContain(host.Http.Requests, r => r.Url.EndsWith("/api/v1/statuses"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Invalid_media_text_and_cancelled_processing_do_not_publish()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        Assert.True(Post(new string('a', 501)).IsOverLimit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(new string('a', 501)), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Client.PostAsync(Post(), [new()
        {
            FileName = "large.png", Kind = MediaKind.Image, ContentType = "image/png", Size = 9 * 1024 * 1024,
        }]));
        Assert.Empty(host.Http.Requests);
        var path = Path.GetTempFileName();
        try
        {
            host.Http.Add("""{"id":"99","url":null}""", HttpStatusCode.Accepted);
            using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.PostAsync(Post(), [new()
            {
                FileName = "a.png", Path = path, Kind = MediaKind.Image, Size = 1, ContentType = "image/png",
            }], ct.Token));
            Assert.Single(host.Http.Requests);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Publish_records_success_failure_and_resolves_saved_contacts()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        await host.AddAsync(new() { Key = "jane", DisplayName = "Jane", MastodonAcct = "jane@social.example" });
        Assert.True((await host.Publisher.GetStatusAsync([Platform.Mastodon])).Mastodon);
        Assert.False((await host.Publisher.GetStatusAsync([])).Mastodon);
        host.Http.Add("""{"url":"https://social.example/@jane/100"}""");
        var success = Assert.Single(await host.Publisher.PublishAsync("Hi {@jane}", new Dictionary<Platform, string>
        { [Platform.Mastodon] = "Override {@jane}" }));
        Assert.True(success.Success);
        Assert.Contains("Override @jane@social.example", host.Http.Requests[0].Body);
        host.Http.Add("""{"error":"Token expired"}""", HttpStatusCode.Unauthorized);
        var failure = Assert.Single(await host.Publisher.PublishAsync("Hello", new Dictionary<Platform, string>
        { [Platform.Mastodon] = "Hello" }));
        Assert.False(failure.Success);
        Assert.Contains("Reconnect", failure.Error);
        var history = await host.Publisher.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(Platform.Mastodon, history[0].Results[0].Platform);
        Assert.Equal(1, (await host.Contacts.GetByKeysAsync(["jane"]))["jane"].UseCount);
    }

    [Fact]
    public async Task Follows_import_paginates_matches_stable_uri_and_preserves_user_edits()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        var contact = new Contact { Key = "saved", DisplayName = "Edited", MastodonAcct = "old@social.example",
            MastodonUri = "https://social.example/users/jane", XUsername = "keep", UseCount = 8 };
        await host.AddAsync(contact);
        host.Http.Add(Account);
        host.Http.Add("[" + Account + "]", link: """<https://social.example/api/v1/accounts/42/following?max_id=1>; rel="next" """);
        host.Http.Add("[]");
        var result = await host.Contacts.SyncMastodonFollowsAsync(platforms: [Platform.Mastodon]);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        var saved = Assert.Single(await host.Contacts.ListAsync());
        Assert.Equal("saved", saved.Key);
        Assert.Equal("Edited", saved.DisplayName);
        Assert.Equal("jane@social.example", saved.MastodonAcct);
        Assert.Equal("keep", saved.XUsername);
        Assert.Equal(8, saved.UseCount);
        Assert.Contains("max_id=1", host.Http.Requests[^1].Url);
        var again = await host.Contacts.EnsureFromMastodonAsync(Jane(), ContactSource.MastodonSearch);
        Assert.False(again.Created);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncMastodonFollowsAsync(platforms: []));
    }

    [Fact]
    public async Task Unsafe_follow_pagination_is_rejected_without_sending_credentials()
    {
        await using var host = await Host.CreateAsync();
        await host.ConfigureAsync();
        host.Http.Add(Account);
        host.Http.Add("[]", link: """<https://attacker.example/api/v1/accounts/42/following>; rel="next" """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.SyncMastodonFollowsAsync());
        Assert.Equal(2, host.Http.Requests.Count);
        Assert.All(host.Http.Requests, r => Assert.StartsWith("https://social.example/", r.Url));
    }

    [Fact]
    public async Task Imported_accounts_skip_conflicts_and_never_match_unrelated_same_names()
    {
        await using var host = await Host.CreateAsync();
        await host.AddAsync(new() { Key = "owned", DisplayName = "Jane", MastodonAcct = "jane@social.example",
            MastodonUri = "https://social.example/users/another" });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            host.Contacts.EnsureFromMastodonAsync(Jane(), ContactSource.MastodonFollows));
        var separate = await host.Contacts.EnsureFromMastodonAsync(Jane("jane@remote.example", "https://remote.example/users/jane"),
            ContactSource.MastodonFollows);
        Assert.True(separate.Created);
        Assert.Equal(2, (await host.Contacts.ListAsync()).Count);
    }

    [Fact]
    public async Task Contacts_filter_merge_aliases_and_purge_include_complete_mastodon_identity()
    {
        await using var host = await Host.CreateAsync();
        var first = new Contact { Key = "first", DisplayName = "First", MastodonAcct = "jane@social.example",
            MastodonUri = "https://social.example/users/jane", MastodonProfileUrl = "https://social.example/@jane" };
        var second = new Contact { Key = "second", DisplayName = "Second", MastodonAcct = "other@remote.example",
            MastodonUri = "https://remote.example/users/other", MastodonProfileUrl = "https://remote.example/@other", XUsername = "keep" };
        await host.AddAsync(first);
        await host.AddAsync(second);
        Assert.Equal(first.Id, Assert.Single((await host.Contacts.ListAsync(new ContactQuery(Filter: "social.example",
            Platforms: [Platform.Mastodon], LinkedPlatforms: [Platform.Mastodon]))).Items).Id);
        Assert.Empty((await host.Contacts.ListAsync(new ContactQuery(Filter: "social.example", Platforms: [Platform.X]))).Items);
        var preview = await host.Contacts.PreviewMergeAsync(first.Id, second.Id);
        Assert.Equal([Platform.Mastodon], preview.Collisions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Contacts.MergeAsync(preview, first.Id, new Dictionary<Platform, int>()));
        var merged = await host.Contacts.MergeAsync(preview, first.Id, new Dictionary<Platform, int> { [Platform.Mastodon] = second.Id });
        Assert.Equal(second.MastodonUri, merged.MastodonUri);
        Assert.Equal(second.MastodonProfileUrl, merged.MastodonProfileUrl);
        Assert.Equal(first.Id, (await host.Contacts.GetByKeysAsync(["second"]))["second"].Id);
        Assert.Equal(1, await host.Contacts.CountPurgeAsync(Platform.Mastodon));
        Assert.Equal(1, await host.Contacts.PurgePlatformAsync(Platform.Mastodon));
        var remaining = Assert.Single(await host.Contacts.ListAsync());
        Assert.Null(remaining.MastodonAcct);
        Assert.Null(remaining.MastodonUri);
        Assert.Null(remaining.MastodonProfileUrl);
        Assert.Equal("keep", remaining.XUsername);
        await host.Contacts.PurgePlatformAsync(Platform.X);
        Assert.Empty(await host.Contacts.ListAsync());
    }

    [Fact]
    public async Task Bio_hints_link_only_enabled_unambiguous_unowned_accounts_and_conflicts_are_suggested()
    {
        var hints = ProfileLinkExtractor.Extract("https://social.example/@Jane");
        Assert.Equal("jane@social.example", hints.MastodonAcct);
        Assert.Equal("jane@social.example", ProfileLinkExtractor.Extract("@jane@social.example").MastodonAcct);
        Assert.True(ProfileLinkExtractor.Extract("https://social.example/@jane @other@remote.example").Ambiguous);
        Assert.Null(ProfileLinkExtractor.Extract("https://www.youtube.com/@creator https://tiktok.com/@creator").MastodonAcct);
        var htmlHints = ProfileLinkExtractor.Extract("""<p><a href="https://x.com/jane">X</a><a href="https://bsky.app/profile/jane.bsky.social">Bluesky</a></p>""");
        Assert.Equal("jane", htmlHints.XUsername);
        Assert.Equal("jane.bsky.social", htmlHints.BlueskyIdentity);
        await using var host = await Host.CreateAsync();
        var existing = new Contact { Key = "existing", DisplayName = "Edited", MastodonAcct = "jane@social.example" };
        await host.AddAsync(existing);
        var linked = await host.Contacts.EnsureFromXAsync(new XUser("id", "handle", "Name", null, "@jane@social.example"),
            ContactSource.XFollows);
        Assert.False(linked.Created);
        Assert.Equal(existing.Id, linked.Contact.Id);
        var other = await host.Contacts.EnsureFromXAsync(new XUser("id2", "handle2", "Name2", null, "@other@remote.example"),
            ContactSource.XFollows, platforms: [Platform.X]);
        Assert.Null(other.Contact.MastodonAcct);
        await host.AddAsync(new() { Key = "duplicate", DisplayName = "Unrelated", MastodonAcct = "JANE@SOCIAL.EXAMPLE" });
        Assert.Contains(await host.Contacts.FindConflictsAsync(), c => c.Reason == "Shared normalized platform identity");
    }

    [Fact]
    public async Task Legacy_schema_upgrades_idempotently_and_preferences_keep_old_disabled_platforms()
    {
        await using var host = await Host.CreateAsync();
        await host.AddAsync(new() { Key = "legacy", DisplayName = "Legacy", XUsername = "keep" });
        await using (var db = host.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Contacts" DROP COLUMN "MastodonAcct";
                ALTER TABLE "Contacts" DROP COLUMN "MastodonUri";
                ALTER TABLE "Contacts" DROP COLUMN "MastodonProfileUrl";
                """);
            await db.UpgradeSchemaAsync();
            await db.UpgradeSchemaAsync();
        }
        Assert.Equal("keep", Assert.Single(await host.Contacts.ListAsync()).XUsername);
        await host.Secrets.SetAsync("ui.disabledPlatforms", "[0,2]");
        var preferences = new PlatformPreferences(host.Secrets);
        await preferences.LoadAsync();
        Assert.Equal([Platform.Bluesky, Platform.Mastodon, Platform.Threads], preferences.Apply(PlatformSelection.All).Platforms);
        await preferences.SetEnabledAsync(Platform.Mastodon, false);
        var restored = new PlatformPreferences(host.Secrets);
        await restored.LoadAsync();
        Assert.Equal([Platform.Bluesky, Platform.Threads], restored.Apply(PlatformSelection.All).Platforms);
    }

    [Fact]
    public async Task Mastodon_only_ui_uses_standard_previews_settings_and_disabled_preferences()
    {
        await using var host = await PlatformSelectionTests.RenderHost.CreateAsync();
        Assert.Contains("platform-mastodon", await host.RenderAsync<Home>([Platform.Mastodon]));
        var settings = await host.RenderAsync<Settings>([Platform.Mastodon]);
        Assert.Contains("Instance URL", settings);
        Assert.Contains("Test connection", settings);
        Assert.DoesNotContain("Account finder agent", settings);
        var preferences = new PlatformPreferences(host.Provider.GetRequiredService<SecretStore>());
        await preferences.SetEnabledAsync(Platform.Mastodon, false);
        Assert.DoesNotContain("platform-mastodon", await host.RenderAsync<Home>([Platform.Mastodon]));
    }

    private sealed record Request(string Url, string? Auth, string? Body, string? Idempotency);
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = [];
        public List<Request> Requests { get; } = [];
        public Func<int, Task>? BeforeSend { get; set; }
        public void Add(string body, HttpStatusCode status = HttpStatusCode.OK, string? link = null)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
            responses.Enqueue(response);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null));
            if (BeforeSend is not null) await BeforeSend(Requests.Count);
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request; tests never use live network.");
            return responses.Dequeue();
        }
    }

    private sealed class Host(SqliteConnection connection, DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>, IAsyncDisposable
    {
        public FakeHttp Http { get; } = new();
        public HttpClient HttpClient { get; private set; } = null!;
        public SecretStore Secrets { get; private set; } = null!;
        public MastodonClient Client { get; private set; } = null!;
        public ContactService Contacts { get; private set; } = null!;
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
            host.Contacts = new(host, bluesky, x, NullLogger<ContactService>.Instance, host.Client);
            host.Publisher = new(host.Contacts, new SocialPublisher(bluesky, x, new LinkedInClient(host.HttpClient, host.Secrets), host.Client,
                new ThreadsClient(host.HttpClient, host.Secrets), host.Secrets), host);
            return host;
        }
        public Task ConfigureAsync() => Secrets.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.MastodonInstance] = "https://social.example/",
            [SettingKeys.MastodonAccessToken] = "original-token",
        });
        public async Task AddAsync(Contact contact)
        {
            await using var db = CreateDbContext();
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await connection.DisposeAsync();
        }
    }
}

